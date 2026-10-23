using System.Text;
using System.Text.Json;
using Bonds.Core.Policy;

namespace Bonds.Core.Telegram;

/// <summary>Кнопка inline-клавиатуры: либо <see cref="CallbackData"/>, либо <see cref="Url"/>.</summary>
public sealed record InlineButton(string Text, string? CallbackData = null, string? Url = null);

/// <summary>Inline-клавиатура: строки кнопок.</summary>
public sealed record InlineKeyboard(IReadOnlyList<IReadOnlyList<InlineButton>> Rows);

/// <summary>Готовое сообщение бота (HTML + необязательная клавиатура).</summary>
public sealed record BotMessage(string Html, InlineKeyboard? Keyboard = null);

/// <summary>
/// Формат сообщения о плане (задача 64). Нейтральные формулировки («план», «оценка»), пометка «режим наблюдения: сделка не отправляется» пока план dry-run.
/// Сообщение — ОЦЕНКА по стакану на момент построения, не рекомендация. Лимит Telegram — 4096 символов: длинный план обрезается с пометкой «ещё N строк».
/// </summary>
public static class PlanMessageFormatter
{
    public const string ObservationNote = "режим наблюдения: сделка не отправляется";
    private const int MaxLength = 3800;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string RuleTitle(string rule) => rule switch
    {
        PlanRules.P1Sweep => "размещение свободных денег",
        PlanRules.P2Inflow => "распределение пополнения",
        PlanRules.P3bLiqToBonds => "перелив ликвидности в облигации",
        PlanRules.P4Rebalance => "возврат корзин к целям",
        PlanRules.P5Withdraw => "вывод средств",
        PlanRules.P7FreedomTopUp => "пополнение Freedom",
        PlanRules.Manual => "выход из корзины",
        _ => rule,
    };

    /// <summary>Строка плана: <c>КУПИТЬ TMON@ ×44 @ 166.57 ≈ 7 329 ₽, комиссия 0</c> (количество — штуки, цена — за штуку в валюте цены).</summary>
    public static string FormatLine(TradePlanLine line)
    {
        var verb = line.Side == PlanSides.Sell ? "ПРОДАТЬ" : "КУПИТЬ";
        var fee = line.EstFee > 0m ? BotFormat.Money(line.EstFee, line.Currency) : "0";
        return $"{verb} {line.Ticker} ×{BotFormat.Number(line.QuantityPieces, line.QuantityPieces % 1m == 0m ? 0 : 4)} @ {BotFormat.Price(line.LimitPrice)} ≈ {BotFormat.Money(line.EstAmount, line.Currency)}, комиссия {fee}";
    }

    /// <summary>Полный текст плана. <paramref name="footer"/> — итог решения («✅ Подтверждено 14:05 МСК»), если уже принято.</summary>
    public static string FormatHtml(TradePlan plan, DateTime nowUtc, string? footer = null)
    {
        var summary = ParseSummary(plan.SummaryJson);
        var sb = new StringBuilder();
        sb.Append($"<b>План №{plan.Id}</b> · {BotFormat.Html(RuleTitle(plan.Rule))}\n");
        if (!string.IsNullOrWhiteSpace(plan.Trigger)) sb.Append($"Причина: {BotFormat.Html(plan.Trigger)}\n");
        sb.Append('\n');

        var lines = plan.Lines.OrderBy(l => l.LineNo).ToList();
        var shown = 0;
        var length = sb.Length;
        foreach (var line in lines)
        {
            var text = $"<code>{BotFormat.Html(FormatLine(line))}</code>\n";
            if (length + text.Length > MaxLength) break;
            sb.Append(text);
            length += text.Length;
            shown++;
        }

        if (shown < lines.Count) sb.Append($"… и ещё {lines.Count - shown} стр.\n");
        if (lines.Count == 0) sb.Append("Строк нет.\n");

        if (summary is not null)
        {
            sb.Append('\n');
            var parts = new List<string>();
            if (summary.BuyTotal > 0m) parts.Add($"покупки ≈ {BotFormat.Money(summary.BuyTotal, summary.Currency)}");
            if (summary.SellTotal > 0m) parts.Add($"продажи ≈ {BotFormat.Money(summary.SellTotal, summary.Currency)}");
            parts.Add($"комиссия ≈ {BotFormat.Money(summary.FeeTotal, summary.Currency)}");
            if (summary.TaxTotal > 0m) parts.Add($"налог ≈ {BotFormat.Money(summary.TaxTotal, summary.Currency)}");
            sb.Append($"Итого (оценка): {BotFormat.Html(string.Join(", ", parts))}\n");
            sb.Append($"Свободные деньги: {BotFormat.Html(BotFormat.Money(summary.CashBefore, summary.Currency))} → {BotFormat.Html(BotFormat.Money(summary.CashAfter, summary.Currency))}\n");
            if (summary.NetToWithdraw is { } net) sb.Append($"Доступно к выводу после налога и комиссии: {BotFormat.Html(BotFormat.Money(net, summary.Currency))}\n");
            if (summary.Shortfall is { } gap && gap > 0m) sb.Append($"Не хватает: {BotFormat.Html(BotFormat.Money(gap, summary.Currency))}\n");
            foreach (var warning in summary.Warnings.Take(5)) sb.Append($"⚠ {BotFormat.Html(warning)}\n");
            if (summary.PricesPossiblyStale) sb.Append("⚠ цены могут быть устаревшими (вне часов торгов)\n");
        }

        sb.Append($"\nДействует до {BotFormat.MoscowTime(plan.ExpiresAt)}");
        if (plan.Status == PlanStatuses.PendingApproval && plan.ExpiresAt <= nowUtc) sb.Append(" (срок истёк)");
        sb.Append('\n');
        if (plan.DryRun) sb.Append($"<i>{ObservationNote}</i>\n");
        if (!string.IsNullOrWhiteSpace(footer)) sb.Append($"\n{footer}\n");
        return sb.ToString().TrimEnd();
    }

    /// <summary>Сообщение о новом плане: PendingApproval — с кнопками «Подтвердить / Отклонить / Детали», остальные статусы — информационное без кнопок (ссылку на план шлёт веб).</summary>
    public static BotMessage ForNewPlan(TradePlan plan, DateTime nowUtc, string? webBaseUrl)
    {
        if (plan.Status != PlanStatuses.PendingApproval)
        {
            var prefix = plan.Status == PlanStatuses.AutoApproved ? "ℹ Авто-подтверждено по лимитам. " : string.Empty;
            return new BotMessage(prefix + FormatHtml(plan, nowUtc));
        }

        var nonce = plan.BotNonce ?? string.Empty;
        var rows = new List<IReadOnlyList<InlineButton>>();
        if (nonce.Length > 0)
        {
            rows.Add(
            [
                new InlineButton("✅ Подтвердить", BotCallback.Build(plan.Id, BotPlanAction.Approve, nonce)),
                new InlineButton("❌ Отклонить", BotCallback.Build(plan.Id, BotPlanAction.Reject, nonce)),
            ]);
        }

        if (DetailsUrl(plan.Id, webBaseUrl) is { } url) rows.Add([new InlineButton("🔎 Детали", Url: url)]);
        return new BotMessage(FormatHtml(plan, nowUtc), rows.Count == 0 ? null : new InlineKeyboard(rows));
    }

    /// <summary>Клавиатура только со ссылкой «Детали» (без кнопок решения) или null, если базовый адрес не задан.</summary>
    public static InlineKeyboard? DetailsOnly(ulong planId, string? webBaseUrl) =>
        DetailsUrl(planId, webBaseUrl) is { } url ? new InlineKeyboard([[new InlineButton("🔎 Детали", Url: url)]]) : null;

    /// <summary>Ссылка на страницу плана в вебе: <c>{base}/bonds/plans/{id}</c>. Не http(s) или пусто — null (Telegram отвергает кнопку с таким адресом).</summary>
    public static string? DetailsUrl(ulong planId, string? webBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(webBaseUrl)) return null;
        var baseUrl = webBaseUrl.Trim().TrimEnd('/');
        if (!baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && !baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return null;
        return $"{baseUrl}/bonds/plans/{planId}";
    }

    private static TradePlanSummary? ParseSummary(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<TradePlanSummary>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

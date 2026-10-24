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
/// Сообщение — ОЦЕНКА по стакану на момент построения, не рекомендация. Лимит Telegram — 4096 символов: длинный план обрезается с пометкой «… и ещё N стр.».
/// </summary>
public static class PlanMessageFormatter
{
    public const string ObservationNote = "режим наблюдения: сделка не отправляется";

    /// <summary>Жирная пометка живого плана (<c>dry_run = 0</c>, задача 67): после подтверждения уйдут настоящие заявки брокеру.</summary>
    public const string RealOrdersMark = "⚠️ РЕАЛЬНЫЕ ЗАЯВКИ";

    private const int MaxExecutionNote = 300;

    /// <summary>Лимит Telegram на текст сообщения (символы UTF-16).</summary>
    public const int TelegramLimit = 4096;

    private const int MaxTrigger = 300;
    private const int MaxRuleTitle = 100;
    private const int MaxWarning = 200;
    private const int MaxWarnings = 5;
    private const int MaxFooter = 300;
    private const int MoreLinesReserve = 40;
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

    /// <summary>
    /// Полный текст плана; итоговая длина ГАРАНТИРОВАННО ≤ <see cref="TelegramLimit"/> (UTF-16, после HTML-экранирования). <paramref name="footer"/> — итог решения
    /// («✅ Подтверждено 14:05 МСК») СЫРЫМ текстом: экранируется здесь. Причина, предупреждения и итог ограничены по длине, строки плана — по оставшемуся бюджету
    /// с пометкой «… и ещё N стр.»; резка идёт по целым символам до экранирования/по целым строкам, сущности и теги не разрываются.
    /// </summary>
    public static string FormatHtml(TradePlan plan, DateTime nowUtc, string? footer = null)
    {
        var summary = ParseSummary(plan.SummaryJson);

        var head = new StringBuilder();
        if (!plan.DryRun)
        {
            // Задача 67 (#4): живой план — заметно и первой строкой, с заметкой исполнения (кому и когда уйдут заявки).
            head.Append($"<b>{RealOrdersMark}</b>\n");
            if (!string.IsNullOrWhiteSpace(summary?.ExecutionNote)) head.Append($"{BotFormat.HtmlTruncated(summary.ExecutionNote, MaxExecutionNote)}\n");
        }

        head.Append($"<b>План №{plan.Id}</b> · {BotFormat.HtmlTruncated(RuleTitle(plan.Rule), MaxRuleTitle)}\n");
        if (!string.IsNullOrWhiteSpace(plan.Trigger)) head.Append($"Причина: {BotFormat.HtmlTruncated(plan.Trigger, MaxTrigger)}\n");
        head.Append('\n');

        var tail = new StringBuilder();
        if (summary is not null)
        {
            tail.Append('\n');
            var parts = new List<string>();
            if (summary.BuyTotal > 0m) parts.Add($"покупки ≈ {BotFormat.Money(summary.BuyTotal, summary.Currency)}");
            if (summary.SellTotal > 0m) parts.Add($"продажи ≈ {BotFormat.Money(summary.SellTotal, summary.Currency)}");
            parts.Add($"комиссия ≈ {BotFormat.Money(summary.FeeTotal, summary.Currency)}");
            if (summary.TaxTotal > 0m) parts.Add($"налог ≈ {BotFormat.Money(summary.TaxTotal, summary.Currency)}");
            tail.Append($"Итого (оценка): {BotFormat.Html(string.Join(", ", parts))}\n");
            tail.Append($"Свободные деньги: {BotFormat.Html(BotFormat.Money(summary.CashBefore, summary.Currency))} → {BotFormat.Html(BotFormat.Money(summary.CashAfter, summary.Currency))}\n");
            if (summary.NetToWithdraw is { } net) tail.Append($"Доступно к выводу после налога и комиссии: {BotFormat.Html(BotFormat.Money(net, summary.Currency))}\n");
            if (summary.Shortfall is { } gap && gap > 0m) tail.Append($"Не хватает: {BotFormat.Html(BotFormat.Money(gap, summary.Currency))}\n");
            foreach (var warning in summary.Warnings.Take(MaxWarnings)) tail.Append($"⚠ {BotFormat.HtmlTruncated(warning, MaxWarning)}\n");
            if (summary.Warnings.Count > MaxWarnings) tail.Append($"… и ещё {summary.Warnings.Count - MaxWarnings} предупр.\n");
            if (summary.PricesPossiblyStale) tail.Append("⚠ цены могут быть устаревшими (вне часов торгов)\n");
        }

        tail.Append($"\nДействует до {BotFormat.MoscowTime(plan.ExpiresAt)}");
        if (plan.Status == PlanStatuses.PendingApproval && plan.ExpiresAt <= nowUtc) tail.Append(" (срок истёк)");
        tail.Append('\n');
        if (plan.DryRun) tail.Append($"<i>{ObservationNote}</i>\n");
        if (!string.IsNullOrWhiteSpace(footer)) tail.Append($"\n{BotFormat.HtmlTruncated(footer, MaxFooter)}\n");

        // Бюджет строк плана: всё, что осталось от лимита после шапки, хвоста и пометки «… и ещё N стр.» (в ней максимум 5-значное N).
        var budget = TelegramLimit - head.Length - tail.Length - MoreLinesReserve;
        var lines = plan.Lines.OrderBy(l => l.LineNo).ToList();
        var body = new StringBuilder();
        var shown = 0;
        foreach (var line in lines)
        {
            var text = $"<code>{BotFormat.Html(FormatLine(line))}</code>\n";
            if (body.Length + text.Length > budget) break;
            body.Append(text);
            shown++;
        }

        if (shown < lines.Count) body.Append($"… и ещё {lines.Count - shown} стр.\n");
        if (lines.Count == 0) body.Append("Строк нет.\n");

        // Страховка: по построению не срабатывает (шапка/хвост ограничены), но сообщение никогда не должно превысить лимит Telegram.
        return BotFormat.TruncateHtml((head.ToString() + body + tail).TrimEnd(), TelegramLimit);
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

    /// <summary>
    /// Второй шаг подтверждения ЖИВОГО плана (задача 67, #4): первое нажатие «Подтвердить» ничего не решает, а показывает этот текст — план с пометкой
    /// <see cref="RealOrdersMark"/> и списком РЕАЛЬНЫХ заявок (строки плана) — и кнопку «Отправить заявки» с НОВЫМ <paramref name="nonce"/> (старые кнопки
    /// после смены nonce недействительны, каждая кнопка одноразовая). Подтверждает план только «Отправить заявки».
    /// </summary>
    public static BotMessage ForSendConfirmation(TradePlan plan, DateTime nowUtc, string nonce, string? webBaseUrl)
    {
        const string footer = "Проверьте заявки выше: это настоящие лимитные заявки брокеру. Отправить — «📨 Отправить заявки», передумали — «❌ Отклонить».";
        var rows = new List<IReadOnlyList<InlineButton>>
        {
            new InlineButton[]
            {
                new("📨 Отправить заявки", BotCallback.Build(plan.Id, BotPlanAction.Send, nonce)),
                new("❌ Отклонить", BotCallback.Build(plan.Id, BotPlanAction.Reject, nonce)),
            },
        };
        if (DetailsUrl(plan.Id, webBaseUrl) is { } url) rows.Add([new InlineButton("🔎 Детали", Url: url)]);
        return new BotMessage(FormatHtml(plan, nowUtc, footer), new InlineKeyboard(rows));
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

using System.Text;
using Bonds.Core.Capital;
using Bonds.Core.Policy;

namespace Bonds.Core.Telegram;

/// <summary>Портфель для <c>/status</c>: отклонения корзин (<see cref="BasketDriftService"/>) и свободные деньги, всё в ВАЛЮТЕ портфеля.</summary>
public sealed record StatusPortfolio(string Portfolio, string Currency, BasketDriftResult Drift, decimal FreeCash, bool DataIncomplete);

/// <summary>Тексты ответов бота на команды (задача 64): справка, <c>/status</c>, <c>/plans</c>. HTML, русская локаль, нейтральные формулировки.</summary>
public static class BotTexts
{
    public const string NoAccess = "Нет доступа.";

    public static string Help(bool paused) =>
        "<b>Команды</b>\n" +
        "/status — капитал, корзины и отклонения, свободные деньги, открытые планы\n" +
        "/plans — открытые планы\n" +
        "/inflow 50000 — построить план распределения суммы (/inflow 500 usd)\n" +
        "/withdraw 15% · /withdraw 50000 · /withdraw 200 usd — план вывода\n" +
        "/exit temporary — план выхода из корзины без цели\n" +
        "/stop — пауза: авто-подтверждение выключено, планы ждут вашего решения\n" +
        "/resume — снять паузу\n" +
        "/help — эта справка\n\n" +
        (paused ? "Сейчас: пауза (авто-подтверждение выключено).\n" : string.Empty) +
        $"<i>{PlanMessageFormatter.ObservationNote}</i>";

    public static string Status(IReadOnlyList<StatusPortfolio> portfolios, int openPlans, bool paused, bool dryRun)
    {
        var sb = new StringBuilder("<b>Капитал</b>\n");
        if (portfolios.Count == 0) sb.Append("Счета брокеров не подключены.\n");

        foreach (var p in portfolios)
        {
            var title = p.Portfolio == CapitalPortfolios.Usd ? "Freedom (доллары)" : "T-Invest (рубли)";
            sb.Append($"\n<b>{BotFormat.Html(title)}</b>: всего {BotFormat.Html(BotFormat.Money(p.Drift.TotalValue, p.Currency))}, свободные деньги {BotFormat.Html(BotFormat.Money(p.FreeCash, p.Currency))}\n");
            foreach (var row in p.Drift.Rows)
            {
                if (row.IsSystem && row.Value == 0m) continue;
                sb.Append(StatusRow(row, p.Currency));
            }

            if (p.DataIncomplete) sb.Append("⚠ данные неполные: по части бумаг нет цены или курса\n");
        }

        sb.Append($"\nОткрытых планов: {openPlans}\n");
        if (paused) sb.Append("Пауза: авто-подтверждение выключено (/resume — снять).\n");
        if (dryRun) sb.Append($"<i>{PlanMessageFormatter.ObservationNote}</i>");
        return sb.ToString().TrimEnd();
    }

    private static string StatusRow(BasketDriftRow row, string currency)
    {
        var name = BotFormat.Html(row.Title);
        var value = BotFormat.Html(BotFormat.Money(row.Value, currency));
        if (row.Target is null || row.Drift is null)
            return row.State == BasketState.NoData
                ? $"• {name}: {value}, нет данных для отклонения\n"
                : $"• {name}: {value}\n";

        var mark = row.State switch
        {
            BasketState.Ok => "✓",
            BasketState.Below => "↓",
            BasketState.Above => "↑",
            BasketState.HardBelow => "⚠↓",
            BasketState.HardAbove => "⚠↑",
            _ => string.Empty,
        };
        var deficit = row.DeficitToTarget is { } d && row.State != BasketState.Ok
            ? $", до цели {BotFormat.Html(BotFormat.Money(d, currency))}"
            : string.Empty;
        return $"• {name}: {BotFormat.Percent(row.Weight)} при цели {BotFormat.Percent(row.Target.Value)} ({BotFormat.PercentPoints(row.Drift.Value)}) {mark}, {value}{deficit}\n";
    }

    /// <summary>Список открытых планов (<paramref name="plans"/> — от новых к старым; показываются первые <paramref name="maxPlans"/>, остальные — строкой «… и ещё N — см. веб», кнопок ≤ 3 на план): строка на план; для ждущих решения — кнопки (подтвердить/отклонить) в клавиатуре.</summary>
    public static BotMessage Plans(IReadOnlyList<TradePlan> plans, string? webBaseUrl, int maxPlans = 10)
    {
        if (plans.Count == 0) return new BotMessage("Открытых планов нет.");
        var hidden = Math.Max(0, plans.Count - maxPlans);

        var sb = new StringBuilder("<b>Открытые планы</b>\n");
        var rows = new List<IReadOnlyList<InlineButton>>();
        foreach (var plan in plans.Take(maxPlans))
        {
            var status = plan.Status switch
            {
                PlanStatuses.PendingApproval => "ждёт решения",
                PlanStatuses.AutoApproved => "авто-подтверждён",
                PlanStatuses.Approved => "подтверждён",
                PlanStatuses.Draft => "черновик",
                _ => plan.Status,
            };
            var lines = plan.Lines.Count;
            sb.Append($"№{plan.Id} · {BotFormat.Html(PlanMessageFormatter.RuleTitle(plan.Rule))} · {status} · строк: {lines} · до {BotFormat.MoscowTime(plan.ExpiresAt)}\n");

            if (plan.Status == PlanStatuses.PendingApproval && !string.IsNullOrEmpty(plan.BotNonce))
            {
                var buttons = new List<InlineButton>
                {
                    new($"✅ №{plan.Id}", BotCallback.Build(plan.Id, BotPlanAction.Approve, plan.BotNonce)),
                    new($"❌ №{plan.Id}", BotCallback.Build(plan.Id, BotPlanAction.Reject, plan.BotNonce)),
                };
                if (PlanMessageFormatter.DetailsUrl(plan.Id, webBaseUrl) is { } url) buttons.Add(new InlineButton($"🔎 №{plan.Id}", Url: url));
                rows.Add(buttons);
            }
        }

        if (hidden > 0) sb.Append($"… и ещё {hidden} — см. веб\n");
        return new BotMessage(BotFormat.TruncateHtml(sb.ToString().TrimEnd(), PlanMessageFormatter.TelegramLimit), rows.Count == 0 ? null : new InlineKeyboard(rows));
    }
}

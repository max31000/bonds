using Bonds.Core.Execution;
using Bonds.Core.Policy;

namespace Bonds.Core.Telegram;

/// <summary>
/// Тексты уведомлений исполнителя (задача 65): отправлено, исполнено (цена, лоты, комиссия), ошибка, отменено, расхождение комиссии, итог плана, включение режима.
/// Всё внешнее экранируется; итог ≤ <see cref="PlanMessageFormatter.TelegramLimit"/> (короткие сообщения, причины режутся <see cref="BotFormat.HtmlTruncated"/>).
/// Кнопка — только «Отменить» у висящей заявки (callback плана с действием Cancel).
/// </summary>
public static class ExecutionMessages
{
    private const int MaxReason = 400;

    private static string Head(TradePlan plan, TradePlanLine line) =>
        $"План #{plan.Id}, {(line.Side == PlanSides.Sell ? "ПРОДАЖА" : "ПОКУПКА")} {BotFormat.HtmlTruncated(line.Ticker, 32)}";

    private static string PriceText(decimal price, string unit, string currency) =>
        unit == OrderPriceUnits.Points ? BotFormat.Price(price) + " п." : BotFormat.Price(price) + " " + BotFormat.Html(currency);

    public static BotMessage Submitted(TradePlan plan, TradePlanLine line, TradeOrder order) => new(
        Limit($"📤 {Head(plan, line)}: заявка отправлена — {order.LotsRequested} лот(ов) по {PriceText(order.LimitPrice, order.PriceUnit, order.Currency)}" +
              (order.Attempt > 1 ? $" (попытка {order.Attempt})" : string.Empty)),
        CancelKeyboard(plan));

    public static string Filled(TradePlan plan, TradePlanLine line, long lots, decimal? avgPrice, decimal commission, string currency) =>
        Limit($"✅ {Head(plan, line)}: исполнено {lots} лот(ов)" +
              (avgPrice is { } p ? $", средняя цена {BotFormat.Price(p)} {BotFormat.Html(currency)}" : string.Empty) +
              $", комиссия {BotFormat.Number(commission, 2)} {BotFormat.Html(currency)}");

    public static string Failed(TradePlan plan, TradePlanLine line, string reason) =>
        Limit($"⚠ {Head(plan, line)}: не исполнено — {BotFormat.HtmlTruncated(reason, MaxReason)}");

    public static string Cancelled(TradePlan plan, TradePlanLine line, string reason) =>
        Limit($"⏹ {Head(plan, line)}: заявка снята — {BotFormat.HtmlTruncated(reason, MaxReason)}");

    public static string FeeMismatch(TradePlan plan, TradePlanLine line, decimal expected, decimal actual, string currency) =>
        Limit($"ℹ {Head(plan, line)}: комиссия {BotFormat.Number(actual, 2)} {BotFormat.Html(currency)} отличается от оценки {BotFormat.Number(expected, 2)} {BotFormat.Html(currency)}");

    public static string PlanFinished(TradePlan plan, string status) => Limit(status switch
    {
        PlanStatuses.Done => $"🏁 План #{plan.Id} исполнен полностью.",
        PlanStatuses.PartiallyDone => $"🏁 План #{plan.Id} исполнен частично — см. строки на странице «Планы».",
        _ => $"🏁 План #{plan.Id} не исполнен — см. причины на странице «Планы».",
    });

    public static string PlanNotStarted(TradePlan plan, string reason) =>
        Limit($"⚠ План #{plan.Id} не передан на исполнение: {BotFormat.HtmlTruncated(reason, MaxReason)}");

    public const string ExecutionEnabled = "🟢 Исполнение включено: подтверждённые планы будут отправляться брокеру T-Invest лимитными заявками. Остановить — /stop.";

    /// <summary>«Исполнение включено» + (если ворота ещё закрыты, например сервер выключен) — почему заявки пока не отправляются.</summary>
    public static string Enabled(IReadOnlyList<string> pendingReasons) => pendingReasons.Count == 0
        ? ExecutionEnabled
        : Limit(ExecutionEnabled + "\n⚠ Пока заявки не отправляются: " +BotFormat.HtmlTruncated(string.Join("; ", pendingReasons), MaxReason));

    public const string ExecutionDisabled = "⚪ Режим наблюдения: заявки не отправляются.";

    private static InlineKeyboard? CancelKeyboard(TradePlan plan) =>
        string.IsNullOrEmpty(plan.BotNonce)
            ? null
            : new InlineKeyboard([[new InlineButton("Отменить", BotCallback.Build(plan.Id, BotPlanAction.CancelExecution, plan.BotNonce))]]);

    private static string Limit(string html) => BotFormat.TruncateHtml(html, PlanMessageFormatter.TelegramLimit);
}

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

    /// <param name="currency">Валюта цены строки.</param>
    /// <param name="commissionCurrency">Валюта комиссии (null — как <paramref name="currency"/>; у Freedom обычно USD даже для сделки в EUR).</param>
    /// <param name="feeNotReconciled">Комиссия не пришла в выписке за окно сверки — сумма может быть неполной.</param>
    public static string Filled(
        TradePlan plan, TradePlanLine line, long lots, decimal? avgPrice, decimal commission, string currency, string? commissionCurrency = null, bool feeNotReconciled = false) =>
        Limit($"✅ {Head(plan, line)}: исполнено {lots} лот(ов)" +
              (avgPrice is { } p ? $", средняя цена {BotFormat.Price(p)} {BotFormat.Html(currency)}" : string.Empty) +
              $", комиссия {BotFormat.Number(commission, 2)} {BotFormat.Html(commissionCurrency ?? currency)}" +
              (feeNotReconciled ? " (комиссия не сверена — в выписке брокера пришла не по всем сделкам)" : string.Empty));

    /// <summary>Информационное: комиссия в другой валюте, чем строка — со сметой (в валюте строки) не сравнивается, курс не применяется.</summary>
    public static string FeeNotCompared(TradePlan plan, TradePlanLine line, decimal actual, string commissionCurrency, string lineCurrency, string? note = null) =>
        Limit($"ℹ {Head(plan, line)}: комиссия {BotFormat.Number(actual, 2)} {BotFormat.Html(commissionCurrency)} — в другой валюте, чем сделка ({BotFormat.Html(lineCurrency)}), " +
              "со сметой не сравнивалась" + (string.IsNullOrWhiteSpace(note) ? string.Empty : $" ({BotFormat.HtmlTruncated(note, 120)})"));

    /// <summary>
    /// Заявка без ответа брокера опознана НЕ по нашему номеру, а по реквизитам (тикер, сторона, количество, цена, время) — исполнитель продолжит её вести; пользователь
    /// проверяет в терминале, что это действительно заявка сервиса.
    /// </summary>
    public static string OrderAdoptedByAttributes(string ticker, long lots, decimal price, string currency) =>
        Limit($"🔎 Заявка {BotFormat.HtmlTruncated(ticker, 32)} ×{lots} по {BotFormat.Price(price)} {BotFormat.Html(currency)}: ответ брокера потерян, заявка найдена у брокера " +
              "по реквизитам (номер заявки не совпал) — проверьте в терминале, что это заявка сервиса");

    public static string Failed(TradePlan plan, TradePlanLine line, string reason) =>
        Limit($"⚠ {Head(plan, line)}: не исполнено — {BotFormat.HtmlTruncated(reason, MaxReason)}");

    public static string Cancelled(TradePlan plan, TradePlanLine line, string reason) =>
        Limit($"⏹ {Head(plan, line)}: заявка снята — {BotFormat.HtmlTruncated(reason, MaxReason)}");

    /// <param name="note">Пояснение (Freedom: фактический маршрут из выписки, по которому посчитана модель), null — нет.</param>
    public static string FeeMismatch(TradePlan plan, TradePlanLine line, decimal expected, decimal actual, string currency, string? note = null) =>
        Limit($"ℹ {Head(plan, line)}: комиссия {BotFormat.Number(actual, 2)} {BotFormat.Html(currency)} отличается от оценки {BotFormat.Number(expected, 2)} {BotFormat.Html(currency)}" +
              (string.IsNullOrWhiteSpace(note) ? string.Empty : $" ({BotFormat.HtmlTruncated(note, 120)})"));

    public static string PlanFinished(TradePlan plan, string status) => Limit(status switch
    {
        PlanStatuses.Done => $"🏁 План #{plan.Id} исполнен полностью.",
        PlanStatuses.PartiallyDone => $"🏁 План #{plan.Id} исполнен частично — см. строки на странице «Планы».",
        _ => $"🏁 План #{plan.Id} не исполнен — см. причины на странице «Планы».",
    });

    /// <summary>Исполнитель не может снять висящую заявку (нет торгового токена, токен на другой счёт, брокер отказал) — ОДИН раз на заявку.</summary>
    public static string CancelImpossible(string ticker, long lots) =>
        Limit($"🚨 Заявка {BotFormat.HtmlTruncated(ticker, 32)} ×{lots} не снята автоматически — снимите её в терминале брокера");

    public static string PlanNotStarted(TradePlan plan, string reason) =>
        Limit($"⚠ План #{plan.Id} не передан на исполнение: {BotFormat.HtmlTruncated(reason, MaxReason)}");

    /// <summary>Задача 67 (#11): подтверждённый план ждёт — исполнение закрыто (пауза, режим, ключ); исполнится, если откроется до срока.</summary>
    public static string PlanWaiting(TradePlan plan, string reason) =>
        Limit($"⏸ План #{plan.Id} ждёт: исполнение сейчас закрыто — {BotFormat.HtmlTruncated(reason, MaxReason)}. " +
              $"Если исполнение откроется (например, /resume) до {BotFormat.Html(TradingHours.ToMoscow(plan.ExpiresAt).ToString("dd.MM HH:mm", System.Globalization.CultureInfo.InvariantCulture))} МСК, план исполнится; иначе срок истечёт.");

    public const string ExecutionEnabled = "🟢 Исполнение включено: подтверждённые планы будут отправляться брокеру (T-Invest, Freedom — при проверенном торговом ключе) лимитными заявками. Остановить — /stop.";

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

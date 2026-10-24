namespace Bonds.Core.Execution;

/// <summary>Режим исполнения пользователя (<c>user_settings.execution_mode</c>, задача 65). По умолчанию <see cref="Observe"/>.</summary>
public static class ExecutionModes
{
    /// <summary>Наблюдение: планы строятся, заявки НЕ отправляются (дефолт).</summary>
    public const string Observe = "observe";

    /// <summary>Исполнение: заявки отправляются, если выполнены ВСЕ условия <see cref="ExecutionGate"/>.</summary>
    public const string Live = "live";

    public static bool IsValid(string? mode) => mode is Observe or Live;

    /// <summary>Неизвестное/пустое значение из БД трактуется как <see cref="Observe"/> (безопасный дефолт).</summary>
    public static string Normalize(string? mode) => mode == Live ? Live : Observe;
}

/// <summary>Статусы заявки исполнителя (<c>trade_orders.status</c>).</summary>
public static class TradeOrderStatuses
{
    /// <summary>Записана в БД ДО отправки; ответа брокера ещё нет (или он потерян) — сверяется через GetOrderState, повторно НЕ отправляется.</summary>
    public const string New = "New";
    public const string Submitted = "Submitted";
    public const string PartiallyFilled = "PartiallyFilled";
    public const string Filled = "Filled";

    /// <summary>Снята. Промежуточная (перевыставление, <see cref="TradeOrderErrorCodes.Reprice"/>) или окончательная (закрытие сессии, отмена пользователем, остановка исполнения).</summary>
    public const string Cancelled = "Cancelled";

    /// <summary>Брокер отклонил заявку.</summary>
    public const string Rejected = "Rejected";

    /// <summary>Не отправлена: не прошла предполётную проверку, либо состояние после сбоя не подтвердилось у брокера.</summary>
    public const string Failed = "Failed";

    /// <summary>Исчерпаны попытки перевыставления (<c>Execution:MaxAttempts</c>), остаток снят.</summary>
    public const string Expired = "Expired";

    /// <summary>Заявка (может быть) у брокера и ещё может исполняться.</summary>
    public static bool IsActive(string status) => status is New or Submitted or PartiallyFilled;
}

/// <summary>Коды причин в <c>trade_orders.error_code</c> (без секретов; текст — в <c>error_message</c>).</summary>
public static class TradeOrderErrorCodes
{
    /// <summary>Снята для перевыставления по новой цене — строка продолжается следующей попыткой.</summary>
    public const string Reprice = "reprice";
    public const string SessionClose = "session_close";
    public const string UserCancel = "user_cancel";
    public const string Halted = "halted";
    public const string UnknownState = "unknown_state";
    public const string Preflight = "preflight";
    public const string BrokerRejected = "broker_rejected";
    public const string Expired = "expired";

    /// <summary>Цена не легла на сетку Quotation (точнее 10^-9) — заявка НЕ отправлялась (ошибка до вызова брокера).</summary>
    public const string PriceGrid = "price_grid";

    /// <summary>Снята перед удалением/заменой торгового токена (<c>?cancelActive=true</c>).</summary>
    public const string TokenChange = "token_change";
}

/// <summary>Единица цены заявки: облигации T-Invest — ПУНКТЫ (процент номинала), остальное — валюта цены за 1 штуку.</summary>
public static class OrderPriceUnits
{
    public const string Points = "points";
    public const string Currency = "currency";
}

/// <summary>
/// Заявка исполнителя (<c>trade_orders</c>) — одна попытка отправки строки плана. <b>Единицы:</b> <see cref="LimitPrice"/> — в единицах брокера
/// (<see cref="PriceUnit"/>: облигации — пункты, иначе валюта за штуку); лоты — целые; <see cref="AvgFillPrice"/> и <see cref="ExecutedCommission"/> — в
/// <see cref="Currency"/> (средняя цена ОДНОЙ штуки и комиссия всей заявки, как отдаёт брокер).
/// </summary>
public sealed class TradeOrder
{
    public ulong Id { get; set; }
    public ulong PlanId { get; set; }
    public int LineNo { get; set; }
    public int Attempt { get; set; }

    /// <summary>UUID (36 символов), создаётся и записывается ДО отправки, уходит брокеру как <c>order_id</c> — идемпотентность.</summary>
    public string ClientOrderId { get; set; } = string.Empty;

    public string? BrokerOrderId { get; set; }

    /// <summary>См. <see cref="TradeOrderStatuses"/>.</summary>
    public string Status { get; set; } = TradeOrderStatuses.New;

    public decimal LimitPrice { get; set; }
    public string PriceUnit { get; set; } = OrderPriceUnits.Currency;
    public long LotsRequested { get; set; }
    public long LotsFilled { get; set; }
    public decimal? AvgFillPrice { get; set; }
    public decimal? ExecutedCommission { get; set; }
    public string Currency { get; set; } = string.Empty;
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// Когда ушло ЕДИНСТВЕННОЕ уведомление «заявка не снята автоматически — снимите её в терминале брокера» (снять нельзя: нет токена, чужой счёт, брокер
    /// отказал). null — не отправлялось. Пока заявка активна у нас, исполнитель продолжает сверку каждый тик, но повторно не уведомляет.
    /// </summary>
    public DateTime? HaltAlertedAt { get; set; }

    /// <summary>Сколько раз итоговая сверка комиссии строки отложена (брокер ещё не прислал комиссию: 0 сразу после исполнения). Ведётся на последней заявке строки.</summary>
    public int FeeRechecks { get; set; }

    /// <summary>Когда строка (по последней заявке) прошла итоговую сверку комиссии и уведомление об исполнении. null — ещё нет.</summary>
    public DateTime? FeeCheckedAt { get; set; }
}

/// <summary>
/// Ворота исполнения (задача 65) — чистая функция. Живой режим = ВСЕ четыре условия: глобальный <c>Execution__Enabled</c>, режим пользователя <c>live</c>,
/// торговый токен <c>tinvest_trade</c> в статусе ok ПОСЛЕ сетевой проверки, нет паузы <c>/stop</c>. Любое не выполнено — dry-run, причины перечисляются.
/// </summary>
public static class ExecutionGate
{
    public static ExecutionGateResult Evaluate(bool globalEnabled, string? userMode, bool tradeCredentialOk, bool paused)
    {
        var reasons = new List<string>();
        if (!globalEnabled) reasons.Add("Исполнение выключено на сервере (Execution__Enabled=false)");
        if (ExecutionModes.Normalize(userMode) != ExecutionModes.Live) reasons.Add("Режим наблюдения (observe)");
        if (!tradeCredentialOk) reasons.Add("Нет проверенного торгового токена T-Invest");
        if (paused) reasons.Add("Пауза (/stop)");
        return new ExecutionGateResult(reasons.Count == 0, globalEnabled, ExecutionModes.Normalize(userMode), tradeCredentialOk, paused, reasons);
    }
}

/// <summary>Итог <see cref="ExecutionGate.Evaluate"/>. <see cref="Live"/> = true только если <see cref="Reasons"/> пуст.</summary>
public sealed record ExecutionGateResult(
    bool Live, bool GlobalEnabled, string Mode, bool TradeCredentialOk, bool Paused, IReadOnlyList<string> Reasons)
{
    public bool DryRun => !Live;
}

namespace Bonds.Infrastructure.Execution;

/// <summary>Счёт, видимый торговым токеном.</summary>
public sealed record TInvestTradeAccount(string Id, bool FullAccess, bool Open);

/// <summary>
/// Состояние заявки у брокера. <see cref="Status"/> — значение <c>TradeOrderStatuses</c> (Submitted/PartiallyFilled/Filled/Cancelled/Rejected); лоты — целые;
/// <see cref="AvgFillPrice"/> — средняя цена исполнения ОДНОЙ штуки в <see cref="Currency"/> (MoneyValue брокера), <see cref="ExecutedCommission"/> — комиссия заявки.
/// </summary>
public sealed record GatewayOrderState(
    string BrokerOrderId,
    string? ClientOrderId,
    string Status,
    long LotsRequested,
    long LotsExecuted,
    decimal? AvgFillPrice,
    decimal? ExecutedCommission,
    string? Currency,
    string? Message);

/// <summary>Статус торгов бумагой (<c>GetTradingStatus</c>).</summary>
public sealed record GatewayTradingStatus(bool NormalTrading, bool LimitOrderAvailable, bool ApiTradeAvailable);

/// <summary>Стакан глубины 1 в единицах брокера (облигации — пункты, остальное — валюта за штуку) и момент стакана (UTC).</summary>
public sealed record GatewayOrderBook(decimal? Bid, decimal? Ask, DateTime? TimestampUtc);

/// <summary>
/// Позиции счёта (<c>GetPositions</c>): <see cref="MoneyAvailable"/> — свободные деньги по валютам (ISO в ВЕРХНЕМ регистре), БЕЗ заблокированных под уже
/// отправленные заявки (их брокер держит в <c>blocked</c>); <see cref="SecuritiesAvailablePieces"/> — незаблокированный остаток бумаг по instrument uid, ШТУКИ.
/// </summary>
public sealed record GatewayPositions(IReadOnlyDictionary<string, decimal> MoneyAvailable, IReadOnlyDictionary<string, decimal> SecuritiesAvailablePieces);

/// <summary>
/// Лимитная заявка. <see cref="Price"/> — в единицах <see cref="PriceUnit"/> (<c>points</c> для облигаций — процент номинала, <c>currency</c> — валюта за штуку);
/// <see cref="Lots"/> — целые лоты; <see cref="ClientOrderId"/> — UUID, уже записанный в <c>trade_orders</c>, уходит брокеру как <c>order_id</c>.
/// </summary>
public sealed record GatewayLimitOrder(string AccountId, string InstrumentUid, string Side, long Lots, decimal Price, string PriceUnit, string ClientOrderId);

public enum GatewayErrorKind
{
    /// <summary>Брокер определённо отказал (неверные параметры, нет прав, бумага недоступна, ворота закрыты) — заявки нет.</summary>
    Rejected,

    /// <summary>Исход неизвестен (сеть, таймаут, перегрузка): заявка МОЖЕТ существовать — только сверка, повторной отправки нет.</summary>
    Transient,
}

/// <summary>Ошибка шлюза. Сообщение — без токена (только код gRPC и текст брокера, обрезанный).</summary>
public sealed class TInvestGatewayException : Exception
{
    public TInvestGatewayException(GatewayErrorKind kind, string code, string message) : base(message)
    {
        Kind = kind;
        Code = code;
    }

    public GatewayErrorKind Kind { get; }
    public string Code { get; }
}

/// <summary>
/// <b>ЕДИНСТВЕННАЯ точка доступа к торговым сервисам T-Invest</b> (задача 65; закреплено <c>NoOrderPlacementArchitectureTests</c>): только реализация этого
/// интерфейса вызывает торговый сервис заявок SDK. Торговый токен (<c>tinvest_trade</c>) читается ТОЛЬКО внутри реализации (и в валидаторе — через
/// <see cref="ProbeAccountsAsync"/>), наружу не выходит и не логируется. Read-only токен здесь не используется (он остаётся у синка).
/// <see cref="PostLimitOrderAsync"/> сам перепроверяет ворота исполнения пользователя и отказывает (<see cref="GatewayErrorKind.Rejected"/>), если они закрыты.
/// </summary>
public interface ITInvestOrderGateway
{
    /// <summary>Счета, видимые ЯВНЫМ (ещё не сохранённым) токеном — только для валидатора <c>tinvest_trade</c>.</summary>
    Task<IReadOnlyList<TInvestTradeAccount>> ProbeAccountsAsync(string token, CancellationToken ct = default);

    /// <summary>Счета, видимые сохранённым торговым токеном пользователя.</summary>
    Task<IReadOnlyList<TInvestTradeAccount>> GetAccountsAsync(ulong userId, CancellationToken ct = default);

    /// <summary>Лимитная заявка (<c>OrderType.Limit</c>) с <c>order_id = ClientOrderId</c> (идемпотентность на стороне брокера).</summary>
    Task<GatewayOrderState> PostLimitOrderAsync(ulong userId, GatewayLimitOrder order, CancellationToken ct = default);

    /// <summary><c>GetOrderState</c> по НАШЕМУ <c>client_order_id</c> (<c>OrderIdType.Request</c>). null — брокер такой заявки не знает.</summary>
    Task<GatewayOrderState?> GetOrderStateAsync(ulong userId, string accountId, string clientOrderId, CancellationToken ct = default);

    /// <summary>Активные заявки счёта (<c>GetOrders</c>) — сверка после рестарта.</summary>
    Task<IReadOnlyList<GatewayOrderState>> GetActiveOrdersAsync(ulong userId, string accountId, CancellationToken ct = default);

    /// <summary><c>CancelOrder</c> по НАШЕМУ <c>client_order_id</c> (<c>OrderIdType.Request</c>).</summary>
    Task CancelOrderAsync(ulong userId, string accountId, string clientOrderId, CancellationToken ct = default);

    Task<GatewayTradingStatus> GetTradingStatusAsync(ulong userId, string instrumentUid, CancellationToken ct = default);

    Task<GatewayOrderBook> GetOrderBookAsync(ulong userId, string instrumentUid, CancellationToken ct = default);

    Task<GatewayPositions> GetPositionsAsync(ulong userId, string accountId, CancellationToken ct = default);
}

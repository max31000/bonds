using Bonds.Infrastructure.Connectors.Freedom.Models;

namespace Bonds.Infrastructure.Execution;

/// <summary>Счёт, который видит торговый ключ Freedom: номер (<c>trader_systems_id</c>) и тариф.</summary>
public sealed record FreedomTradeAccountInfo(string AccountNumber, int TariffId);

/// <summary>
/// Лимитная заявка Freedom. <see cref="Ticker"/> — только <c>.US</c>/<c>.EU</c>; <see cref="Quantity"/> — ШТУКИ (у Freedom лот = 1); <see cref="LimitPrice"/> — в ВАЛЮТЕ
/// бумаги за 1 штуку, уже на сетке <c>min_step</c>; <see cref="UserOrderId"/> — числовой id, уже записанный в <c>trade_orders.client_order_id</c>.
/// Тип заявки и <c>action_id</c> вызывающий НЕ задаёт: шлюз выводит <c>action_id</c> из <see cref="Side"/> по белому списку {1, 3}.
/// </summary>
public sealed record FreedomLimitOrder(string Ticker, string Side, long Quantity, decimal LimitPrice, string UserOrderId);

/// <summary>Котировка: лучшие bid/ask и шаг цены — в валюте <see cref="Currency"/> за штуку.</summary>
public sealed record FreedomMarketQuote(decimal? Bid, decimal? Ask, decimal? MinStep, string? Currency);

/// <summary>Деньги по валютам (ISO в верхнем регистре) и остатки бумаг по тикеру (верхний регистр), ШТУКИ.</summary>
public sealed record FreedomAccountSnapshot(IReadOnlyDictionary<string, decimal> Cash, IReadOnlyDictionary<string, decimal> PiecesByTicker);

/// <summary>Ошибка шлюза Freedom. Сообщение — без ключей и подписи (текст брокера проходит через маскирование).</summary>
public sealed class FreedomGatewayException : ExecutionGatewayException
{
    public FreedomGatewayException(GatewayErrorKind kind, string code, string message) : base(kind, code, message)
    {
    }
}

/// <summary>
/// <b>ЕДИНСТВЕННАЯ точка размещения и снятия заявок Freedom</b> (задача 66; закреплено <c>NoOrderPlacementArchitectureTests</c> — команды заявок Tradernet есть только в
/// <c>Execution/FreedomOrderGateway.cs</c>). Торговый ключ (<c>freedom_trade</c>) читается ТОЛЬКО внутри реализации, наружу не выходит и не логируется. Ключ на чтение
/// (<c>freedom</c>) здесь не используется — он у синка.
/// <see cref="PostLimitOrderAsync"/> ДО сети перепроверяет ворота Freedom, белый список <c>action_id</c> {1, 3} (маржинальные 2/4 невозможны), тикер (<c>.ITS</c> и всё,
/// кроме <c>.US</c>/<c>.EU</c>, — отказ) и корректность заявки.
/// </summary>
public interface IFreedomOrderGateway
{
    /// <summary>Счёт и тариф по сохранённому торговому ключу (сверка с ключом на чтение на каждом тике).</summary>
    Task<FreedomTradeAccountInfo> GetAccountInfoAsync(ulong userId, CancellationToken ct = default);

    /// <summary>Лимитная дневная заявка с <c>user_order_id</c>. Определённый отказ — <see cref="GatewayErrorKind.Rejected"/>; исход неизвестен — <see cref="GatewayErrorKind.Transient"/>.</summary>
    Task<GatewayOrderState> PostLimitOrderAsync(ulong userId, FreedomLimitOrder order, CancellationToken ct = default);

    /// <summary>Заявки счёта: <paramref name="activeOnly"/> — только активные; иначе — все, что брокер отдаёт (включая исполненные/снятые за текущий период).</summary>
    Task<IReadOnlyList<GatewayOrderState>> GetOrdersAsync(ulong userId, bool activeOnly, CancellationToken ct = default);

    /// <summary>Снятие по id заявки БРОКЕРА.</summary>
    Task CancelOrderAsync(ulong userId, string brokerOrderId, CancellationToken ct = default);

    Task<FreedomMarketQuote> GetQuoteAsync(ulong userId, string ticker, CancellationToken ct = default);

    Task<FreedomAccountSnapshot> GetAccountSnapshotAsync(ulong userId, CancellationToken ct = default);

    /// <summary>Сделки за период (связь сделка → заявка, цена и количество исполнения).</summary>
    Task<IReadOnlyList<FreedomTrade>> GetTradesAsync(ulong userId, DateOnly from, DateOnly to, CancellationToken ct = default);

    /// <summary>Последние движения денег (строки <c>commission_for_trades</c> — фактическая комиссия и маршрут).</summary>
    Task<IReadOnlyList<FreedomCashFlow>> GetCashFlowsAsync(ulong userId, int take, CancellationToken ct = default);
}

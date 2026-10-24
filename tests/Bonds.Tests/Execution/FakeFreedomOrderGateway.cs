using Bonds.Core.Execution;
using Bonds.Infrastructure.Connectors.Freedom.Models;
using Bonds.Infrastructure.Execution;

namespace Bonds.Tests.Execution;

/// <summary>
/// Двойник <see cref="IFreedomOrderGateway"/> без сети (общий для юнит- и интеграционных тестов). Хранит заявки по id брокера, котировки, деньги и бумаги задаёт тест,
/// сделки и движения денег — для сверки комиссии. Ворот НЕ проверяет — это делают исполнитель и реальный шлюз.
/// </summary>
public sealed class FakeFreedomOrderGateway : IFreedomOrderGateway
{
    private readonly object _lock = new();
    private long _nextId = 900_000_001;

    public FreedomTradeAccountInfo Account { get; set; } = new("100001", 1431);
    public Exception? AccountError { get; set; }
    public Dictionary<string, FreedomMarketQuote> Quotes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, decimal> Cash { get; } = new(StringComparer.OrdinalIgnoreCase) { ["USD"] = 10_000m };
    public Dictionary<string, decimal> Pieces { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<FreedomTrade> Trades { get; } = [];
    public List<FreedomCashFlow> CashFlows { get; } = [];

    public List<(ulong UserId, FreedomLimitOrder Order)> Posted { get; } = [];
    public List<string> Cancelled { get; } = [];

    /// <summary>Заявки у «брокера» по id брокера.</summary>
    public Dictionary<string, GatewayOrderState> Orders { get; } = new(StringComparer.Ordinal);

    /// <summary>Перехват отправки: бросить (например, сеть) или вернуть состояние. null — «принята, активна».</summary>
    public Func<FreedomLimitOrder, GatewayOrderState>? OnPost { get; set; }

    /// <summary>Отправка «доходит» до брокера, но ответ теряется (исключение Transient ПОСЛЕ регистрации заявки).</summary>
    public bool LoseResponse { get; set; }

    /// <summary>Время размещения у «брокера» (UTC) — для сверки по реквизитам; по умолчанию — системное.</summary>
    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    public int PostCount
    {
        get { lock (_lock) return Posted.Count; }
    }

    public Task<FreedomTradeAccountInfo> GetAccountInfoAsync(ulong userId, CancellationToken ct = default) =>
        AccountError is { } e ? Task.FromException<FreedomTradeAccountInfo>(e) : Task.FromResult(Account);

    public Task<GatewayOrderState> PostLimitOrderAsync(ulong userId, FreedomLimitOrder order, CancellationToken ct = default)
    {
        lock (_lock)
        {
            Posted.Add((userId, order));
            var state = OnPost?.Invoke(order)
                ?? new GatewayOrderState((_nextId++).ToString(), order.UserOrderId, TradeOrderStatuses.Submitted, order.Quantity, 0, null, null, "USD", null);
            state = state with
            {
                Ticker = state.Ticker ?? order.Ticker.ToUpperInvariant(), Side = state.Side ?? order.Side, LimitPrice = state.LimitPrice ?? order.LimitPrice,
                PlacedAtUtc = state.PlacedAtUtc ?? Clock(),
            };

            // Как у живого брокера: в списке заявок (getNotifyOrderJson) user_order_id = null, эхо — только в ответе на размещение.
            Orders[state.BrokerOrderId] = state with { ClientOrderId = null };
            if (LoseResponse) throw new FreedomGatewayException(GatewayErrorKind.Transient, "timeout", "Freedom: таймаут");
            return Task.FromResult(state);
        }
    }

    public Task<IReadOnlyList<GatewayOrderState>> GetOrdersAsync(ulong userId, bool activeOnly, CancellationToken ct = default)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<GatewayOrderState>>(Orders.Values
                .Where(o => !activeOnly || TradeOrderStatuses.IsActive(o.Status))
                .ToList());
        }
    }

    public Task CancelOrderAsync(ulong userId, string brokerOrderId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            Cancelled.Add(brokerOrderId);
            if (Orders.TryGetValue(brokerOrderId, out var s) && TradeOrderStatuses.IsActive(s.Status))
                Orders[brokerOrderId] = s with { Status = TradeOrderStatuses.Cancelled };
            return Task.CompletedTask;
        }
    }

    /// <summary>Исполнение заявки брокером (штуки), с одной сделкой <paramref name="tradeId"/> и (если задано) строкой комиссии с маршрутом.</summary>
    public void Fill(string brokerOrderId, long qty, decimal price, string tradeId, decimal? commission, string market = "europe", string type = "funds")
    {
        lock (_lock)
        {
            var s = Orders[brokerOrderId];
            Orders[brokerOrderId] = s with { Status = qty >= s.LotsRequested ? TradeOrderStatuses.Filled : TradeOrderStatuses.PartiallyFilled, LotsExecuted = qty };
            Trades.Add(new FreedomTrade(tradeId, brokerOrderId, DateTime.UtcNow, "X", FreedomTradeSide.Buy, qty, price, qty * price, "USD", commission ?? 0m, "USD"));
            if (commission is { } fee) AddCommission(tradeId, fee, market, type);
        }
    }

    public void AddCommission(string tradeId, decimal fee, string market = "europe", string type = "funds") =>
        CashFlows.Add(new FreedomCashFlow(
            "cf-" + tradeId, "commission_for_trades", DateTime.UtcNow, "USD", -fee,
            $"(Trade {tradeId} buy X.EU ) Рынок: {market}, тип бумаги: {type}, валюта комиссии: USD, тариф Standard, валюта сделки: USD."));

    public Task<FreedomMarketQuote> GetQuoteAsync(ulong userId, string ticker, CancellationToken ct = default) =>
        Task.FromResult(Quotes.TryGetValue(ticker, out var q) ? q : new FreedomMarketQuote(null, null, null, null));

    public Task<FreedomAccountSnapshot> GetAccountSnapshotAsync(ulong userId, CancellationToken ct = default) =>
        Task.FromResult(new FreedomAccountSnapshot(
            new Dictionary<string, decimal>(Cash, StringComparer.OrdinalIgnoreCase), new Dictionary<string, decimal>(Pieces, StringComparer.OrdinalIgnoreCase)));

    public Task<IReadOnlyList<FreedomTrade>> GetTradesAsync(ulong userId, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        lock (_lock) return Task.FromResult<IReadOnlyList<FreedomTrade>>(Trades.ToList());
    }

    public Task<IReadOnlyList<FreedomCashFlow>> GetCashFlowsAsync(ulong userId, int take, CancellationToken ct = default)
    {
        lock (_lock) return Task.FromResult<IReadOnlyList<FreedomCashFlow>>(CashFlows.ToList());
    }
}

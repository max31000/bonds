using Bonds.Core.Execution;
using Bonds.Infrastructure.Execution;

namespace Bonds.Tests.Execution;

/// <summary>
/// Двойник <see cref="ITInvestOrderGateway"/> без сети (общий для юнит- и интеграционных тестов — подключается ссылкой на файл). Записывает отправленные/снятые
/// заявки, хранит их состояние по <c>client_order_id</c>; стакан, статус торгов и позиции задаются тестом. Ворот НЕ проверяет — это делают исполнитель и реальный шлюз.
/// </summary>
public sealed class FakeOrderGateway : ITInvestOrderGateway
{
    private readonly object _lock = new();

    public List<TInvestTradeAccount> Accounts { get; } = [new("ACC-1", true, true)];
    public Dictionary<string, GatewayOrderBook> Books { get; } = new(StringComparer.Ordinal);
    public GatewayTradingStatus TradingStatus { get; set; } = new(true, true, true);
    public Dictionary<string, decimal> Money { get; } = new(StringComparer.OrdinalIgnoreCase) { ["RUB"] = 1_000_000m };
    public Dictionary<string, decimal> Securities { get; } = new(StringComparer.Ordinal);

    public List<(ulong UserId, GatewayLimitOrder Order)> Posted { get; } = [];
    public List<string> Cancelled { get; } = [];
    public Dictionary<string, GatewayOrderState> States { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Перехват отправки: вернуть состояние или бросить. null — по умолчанию «принята, не исполнена».</summary>
    public Func<GatewayLimitOrder, GatewayOrderState>? OnPost { get; set; }

    /// <summary>Брокер «не знает» заявок (GetOrderState → null), даже отправленных.</summary>
    public bool ForgetOrders { get; set; }

    /// <summary>Снятие бросает это исключение (например, нет токена / чужой счёт / брокер отказал). Попытка всё равно записывается в <see cref="Cancelled"/>.</summary>
    public Exception? CancelError { get; set; }

    /// <summary>GetOrderState/GetOrders бросают это исключение.</summary>
    public Exception? StateError { get; set; }

    public int PostCount
    {
        get { lock (_lock) return Posted.Count; }
    }

    public static GatewayOrderState Accepted(GatewayLimitOrder o) =>
        new("BRK-" + o.ClientOrderId[..8], o.ClientOrderId, TradeOrderStatuses.Submitted, o.Lots, 0, null, null, "RUB", null);

    public void Fill(string clientOrderId, long lots, decimal avgPrice, decimal commission, bool full = true)
    {
        lock (_lock)
        {
            var s = States[clientOrderId];
            States[clientOrderId] = s with
            {
                Status = full ? TradeOrderStatuses.Filled : TradeOrderStatuses.PartiallyFilled, LotsExecuted = lots, AvgFillPrice = avgPrice, ExecutedCommission = commission,
            };
        }
    }

    public Task<IReadOnlyList<TInvestTradeAccount>> ProbeAccountsAsync(string token, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<TInvestTradeAccount>>(Accounts.ToList());

    public Task<IReadOnlyList<TInvestTradeAccount>> GetAccountsAsync(ulong userId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<TInvestTradeAccount>>(Accounts.ToList());

    public Task<GatewayOrderState> PostLimitOrderAsync(ulong userId, GatewayLimitOrder order, CancellationToken ct = default)
    {
        lock (_lock)
        {
            Posted.Add((userId, order));
            var state = OnPost?.Invoke(order) ?? Accepted(order);
            States[order.ClientOrderId] = state;
            return Task.FromResult(state);
        }
    }

    public Task<GatewayOrderState?> GetOrderStateAsync(ulong userId, string accountId, string clientOrderId, CancellationToken ct = default)
    {
        if (StateError is { } error) throw error;
        lock (_lock) return Task.FromResult(ForgetOrders ? null : States.GetValueOrDefault(clientOrderId));
    }

    public Task<IReadOnlyList<GatewayOrderState>> GetActiveOrdersAsync(ulong userId, string accountId, CancellationToken ct = default)
    {
        if (StateError is { } error) throw error;
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<GatewayOrderState>>(ForgetOrders
                ? []
                : States.Values.Where(s => s.Status is TradeOrderStatuses.Submitted or TradeOrderStatuses.PartiallyFilled).ToList());
        }
    }

    public Task CancelOrderAsync(ulong userId, string accountId, string clientOrderId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            Cancelled.Add(clientOrderId);
            if (CancelError is { } error) throw error;
            if (States.TryGetValue(clientOrderId, out var s) && s.Status is TradeOrderStatuses.Submitted or TradeOrderStatuses.PartiallyFilled)
                States[clientOrderId] = s with { Status = TradeOrderStatuses.Cancelled };
            return Task.CompletedTask;
        }
    }

    public Task<GatewayTradingStatus> GetTradingStatusAsync(ulong userId, string instrumentUid, CancellationToken ct = default) => Task.FromResult(TradingStatus);

    public Task<GatewayOrderBook> GetOrderBookAsync(ulong userId, string instrumentUid, CancellationToken ct = default) =>
        Task.FromResult(Books.TryGetValue(instrumentUid, out var b) ? b : new GatewayOrderBook(null, null, null));

    public Task<GatewayPositions> GetPositionsAsync(ulong userId, string accountId, CancellationToken ct = default) =>
        Task.FromResult(new GatewayPositions(
            new Dictionary<string, decimal>(Money, StringComparer.OrdinalIgnoreCase), new Dictionary<string, decimal>(Securities, StringComparer.Ordinal)));
}

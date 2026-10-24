using Bonds.Core.Execution;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Policy;
using Bonds.Infrastructure.Execution;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using BrokerIds = Bonds.Core.Models.Brokers;

namespace Bonds.Tests.Execution;

/// <summary>
/// Задача 66: сверка заявки Freedom, ответ на размещение которой потерян (<c>New</c> без id брокера). В <c>getNotifyOrderJson</c> брокер отдаёт <c>user_order_id = null</c>,
/// поэтому заявка опознаётся по реквизитам: тикер, сторона (<c>oper</c>), количество, лимит в пределах шаг/2, время размещения в окне ±2 мин от <c>created_at</c>;
/// уже привязанные к другой строке заявки исключаются; ноль или несколько кандидатов — не опознана (никогда не перевыставляется).
/// </summary>
public class FreedomReconcileTests
{
    private const ulong UserId = 7;
    private static readonly DateTime CreatedAt = new(2026, 10, 23, 14, 4, 15, DateTimeKind.Utc);

    private readonly FakeFreedomOrderGateway _gateway = new();
    private readonly InMemoryOrders _orders = new();

    private FreedomExecutionVenue Venue() => new(
        _gateway, Mock.Of<IExecutionGateResolver>(), Mock.Of<IAccountRepository>(), Mock.Of<IUserCredentialRepository>(), _orders, NullLogger.Instance);

    private static TradePlanLine Line(string ticker = "VUAA.EU", string side = PlanSides.Buy) => new()
    {
        LineNo = 1, Broker = BrokerIds.Freedom, InstrumentKey = ticker, Ticker = ticker, Side = side, QuantityLots = 3, LotSize = 1m, LimitPrice = 147.70m, Currency = "USD",
    };

    private TradeOrder Ours(long qty = 3, decimal price = 147.70m)
    {
        var o = new TradeOrder
        {
            PlanId = 1, LineNo = 1, Attempt = 1, ClientOrderId = "1234567890", Broker = BrokerIds.Freedom, Status = TradeOrderStatuses.New, LimitPrice = price,
            LotsRequested = qty, Currency = "USD", CreatedAt = CreatedAt, UpdatedAt = CreatedAt,
        };
        _orders.InsertAsync(o).GetAwaiter().GetResult();
        return o;
    }

    private void Broker(string id, string ticker = "VUAA.EU", string? side = PlanSides.Buy, long q = 3, decimal p = 147.70m, DateTime? at = null, string? userOrderId = null) =>
        _gateway.Orders[id] = new GatewayOrderState(
            id, userOrderId, TradeOrderStatuses.Submitted, q, 0, null, null, "USD", null, Ticker: ticker, Side: side, LimitPrice: p, PlacedAtUtc: at ?? CreatedAt.AddSeconds(1));

    private Task<GatewayOrderState?> Reconcile(TradeOrder order, TradePlanLine? line = null) =>
        Venue().StateAsync(new VenueSession { Broker = BrokerIds.Freedom, BrokerAccountId = "100001" }, UserId, order, line ?? Line(), true, CancellationToken.None);

    [Fact]
    public async Task ExactlyOneMatchingOrder_IsAdopted()
    {
        _gateway.Quotes["VUAA.EU"] = new FreedomMarketQuote(147.6m, 147.8m, 0.01m, "USD");
        var order = Ours();
        Broker("900000001", p: 147.704m); // в пределах шаг/2
        Broker("900000002", ticker: "SPYM.US");
        Broker("900000003", side: PlanSides.Sell);
        Broker("900000004", q: 2);
        Broker("900000005", p: 147.72m); // > шаг/2
        Broker("900000006", at: CreatedAt.AddMinutes(-3)); // старше окна — ручная заявка до нашей
        Broker("900000007", at: CreatedAt.AddMinutes(3)); // позже окна
        Broker("900000008", userOrderId: "api:1792763201"); // помечена другим user_order_id — не наша

        var found = await Reconcile(order);

        found.Should().NotBeNull();
        found!.BrokerOrderId.Should().Be("900000001");
    }

    [Fact]
    public async Task EchoedOwnUserOrderId_DoesNotExcludeCandidate()
    {
        var order = Ours();
        Broker("900000001", userOrderId: "api:1234567890");
        (await Reconcile(order))!.BrokerOrderId.Should().Be("900000001");
    }

    [Fact]
    public async Task TwoMatchingOrders_Ambiguous_NotAdopted()
    {
        var order = Ours();
        Broker("900000001");
        Broker("900000002", at: CreatedAt.AddSeconds(30));
        (await Reconcile(order)).Should().BeNull("несколько кандидатов — состояние неизвестно, повторной отправки нет");
    }

    [Fact]
    public async Task NoMatchingOrder_NotFound()
    {
        var order = Ours();
        Broker("900000001", q: 1);
        (await Reconcile(order)).Should().BeNull();
    }

    [Fact]
    public async Task CandidateLinkedToAnotherRow_IsExcluded()
    {
        var order = Ours();
        var other = new TradeOrder
        {
            PlanId = 2, LineNo = 1, Attempt = 1, ClientOrderId = "1999999999", BrokerOrderId = "900000001", Broker = BrokerIds.Freedom, Status = TradeOrderStatuses.Submitted,
            LotsRequested = 3, LimitPrice = 147.70m, Currency = "USD", CreatedAt = CreatedAt, UpdatedAt = CreatedAt,
        };
        await _orders.InsertAsync(other);
        Broker("900000001");
        Broker("900000002", at: CreatedAt.AddSeconds(5));

        var found = await Reconcile(order);

        found.Should().NotBeNull("900000001 уже принадлежит другой строке — остаётся один кандидат");
        found!.BrokerOrderId.Should().Be("900000002");
    }

    [Fact]
    public async Task NoQuoteStep_PriceMustMatchExactly()
    {
        var order = Ours();
        Broker("900000001", p: 147.701m);
        (await Reconcile(order)).Should().BeNull();
        Broker("900000001", p: 147.70m);
        (await Reconcile(order))!.BrokerOrderId.Should().Be("900000001");
    }

    [Fact]
    public async Task BrokerOrderWithoutTime_IsNotCandidate()
    {
        var order = Ours();
        _gateway.Orders["900000001"] = new GatewayOrderState(
            "900000001", null, TradeOrderStatuses.Submitted, 3, 0, null, null, "USD", null, Ticker: "VUAA.EU", Side: PlanSides.Buy, LimitPrice: 147.70m);
        (await Reconcile(order)).Should().BeNull();
    }

    [Fact]
    public async Task WithoutLine_CannotReconcile()
    {
        var order = Ours();
        Broker("900000001");
        (await Venue().StateAsync(new VenueSession { Broker = BrokerIds.Freedom, BrokerAccountId = "100001" }, UserId, order, null, true, CancellationToken.None))
            .Should().BeNull();
    }

    [Fact]
    public async Task KnownBrokerId_FoundById_IgnoringAttributes()
    {
        var order = Ours();
        order.BrokerOrderId = "900000009";
        Broker("900000009", q: 99, at: CreatedAt.AddHours(-5));
        (await Reconcile(order))!.BrokerOrderId.Should().Be("900000009");
    }

    [Fact]
    public void Candidates_PureFunction_WindowIsTwoMinutesBothSides()
    {
        var order = new TradeOrder { ClientOrderId = "1234567890", LotsRequested = 3, LimitPrice = 147.70m, CreatedAt = CreatedAt };
        GatewayOrderState At(DateTime t) => new("1", null, TradeOrderStatuses.Submitted, 3, 0, null, null, "USD", null, Ticker: "VUAA.EU", Side: PlanSides.Buy, LimitPrice: 147.70m, PlacedAtUtc: t);

        FreedomExecutionVenue.Candidates([At(CreatedAt.AddMinutes(-2))], order, "VUAA.EU", PlanSides.Buy, 0.01m).Should().HaveCount(1);
        FreedomExecutionVenue.Candidates([At(CreatedAt.AddMinutes(2))], order, "VUAA.EU", PlanSides.Buy, 0.01m).Should().HaveCount(1);
        FreedomExecutionVenue.Candidates([At(CreatedAt.AddMinutes(-2).AddSeconds(-1))], order, "VUAA.EU", PlanSides.Buy, 0.01m).Should().BeEmpty();
        FreedomExecutionVenue.Candidates([At(CreatedAt.AddMinutes(2).AddSeconds(1))], order, "VUAA.EU", PlanSides.Buy, 0.01m).Should().BeEmpty();
    }
}

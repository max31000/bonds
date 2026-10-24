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
/// Задача 66: сверка заявки Freedom, ответ на размещение которой потерян (<c>New</c> без id брокера). Сначала — ТОЧНОЕ совпадение <c>userOrderId</c> (<c>N</c> или
/// <c>api:N</c>); иначе — по реквизитам (тикер, сторона, количество, лимит в пределах шаг/2, время размещения в окне [−30 с, +2 мин] от <c>created_at</c>) ТОЛЬКО среди
/// заявок с пустым <c>userOrderId</c> или в форме API (ручные <c>i-6:ios:…</c> и заявки с другим НАШИМ номером — никогда), с пометкой <c>MatchedByAttributes</c>;
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
            PlanId = 1, LineNo = 1, Attempt = 1, ClientOrderId = "712345678", Broker = BrokerIds.Freedom, Status = TradeOrderStatuses.New, LimitPrice = price,
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
    public async Task ExactUserOrderId_WinsOverAttributes_NoAlertFlag()
    {
        var order = Ours();
        Broker("900000001"); // совпадает по реквизитам, но номера нет
        Broker("900000002", q: 99, p: 1m, at: CreatedAt.AddHours(-3), userOrderId: "api:712345678"); // наш номер — реквизиты не важны

        var found = await Reconcile(order);

        found!.BrokerOrderId.Should().Be("900000002");
        found.MatchedByAttributes.Should().BeFalse();
    }

    [Theory]
    [InlineData("712345678")]
    [InlineData("api:712345678")]
    public async Task ExactUserOrderId_BothForms(string echoed)
    {
        var order = Ours();
        Broker("900000001", userOrderId: echoed);
        (await Reconcile(order))!.BrokerOrderId.Should().Be("900000001");
    }

    [Fact]
    public async Task ExactlyOneAttributeMatch_IsAdopted_FlaggedForAlert()
    {
        _gateway.Quotes["VUAA.EU"] = new FreedomMarketQuote(147.6m, 147.8m, 0.01m, "USD");
        var order = Ours();
        Broker("900000001", p: 147.704m, userOrderId: "api:1792763201"); // в пределах шаг/2; авто-id брокера (наш номер не принят) — может быть нашей
        Broker("900000002", ticker: "SPYM.US");
        Broker("900000003", side: PlanSides.Sell);
        Broker("900000004", q: 2);
        Broker("900000005", p: 147.72m); // > шаг/2
        Broker("900000006", at: CreatedAt.AddSeconds(-31)); // раньше окна (−30 с)
        Broker("900000007", at: CreatedAt.AddMinutes(2).AddSeconds(1)); // позже окна (+2 мин)
        Broker("900000008", userOrderId: "i-6:ios:terminal_exchange_currency"); // ручная из приложения — никогда
        Broker("900000009", userOrderId: "web:terminal"); // ручная из терминала — никогда
        Broker("900000010", userOrderId: "api:798765432"); // другой НАШ номер — другая строка trade_orders

        var found = await Reconcile(order);

        found.Should().NotBeNull();
        found!.BrokerOrderId.Should().Be("900000001");
        found.MatchedByAttributes.Should().BeTrue("исполнитель уведомит, что заявка найдена по реквизитам");
    }

    [Fact]
    public async Task ManualTerminalOrder_WithMatchingAttributes_IsNeverAdopted()
    {
        var order = Ours();
        Broker("900000001", userOrderId: "i-6:ios:terminal_exchange_currency");
        (await Reconcile(order)).Should().BeNull("ручная заявка пользователя не становится заявкой сервиса");
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
            PlanId = 2, LineNo = 1, Attempt = 1, ClientOrderId = "798765432", BrokerOrderId = "900000001", Broker = BrokerIds.Freedom, Status = TradeOrderStatuses.Submitted,
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
    public void Candidates_PureFunction_WindowIsMinus30SecondsPlus2Minutes()
    {
        var order = new TradeOrder { ClientOrderId = "712345678", LotsRequested = 3, LimitPrice = 147.70m, CreatedAt = CreatedAt };
        GatewayOrderState At(DateTime t) => new("1", null, TradeOrderStatuses.Submitted, 3, 0, null, null, "USD", null, Ticker: "VUAA.EU", Side: PlanSides.Buy, LimitPrice: 147.70m, PlacedAtUtc: t);

        FreedomExecutionVenue.Candidates([At(CreatedAt.AddSeconds(-30))], order, "VUAA.EU", PlanSides.Buy, 0.01m).Should().HaveCount(1);
        FreedomExecutionVenue.Candidates([At(CreatedAt.AddMinutes(2))], order, "VUAA.EU", PlanSides.Buy, 0.01m).Should().HaveCount(1);
        FreedomExecutionVenue.Candidates([At(CreatedAt.AddSeconds(-31))], order, "VUAA.EU", PlanSides.Buy, 0.01m).Should().BeEmpty("до записи строки — ручная заявка до нашей");
        FreedomExecutionVenue.Candidates([At(CreatedAt.AddMinutes(-2))], order, "VUAA.EU", PlanSides.Buy, 0.01m).Should().BeEmpty();
        FreedomExecutionVenue.Candidates([At(CreatedAt.AddMinutes(2).AddSeconds(1))], order, "VUAA.EU", PlanSides.Buy, 0.01m).Should().BeEmpty();
    }
}

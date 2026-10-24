using Bonds.Core.Credentials;
using Bonds.Core.Execution;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using BrokerIds = Bonds.Core.Models.Brokers;
using Bonds.Core.Policy;
using Bonds.Core.Telegram;
using Bonds.Infrastructure.Execution;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Bonds.Tests.Execution;

/// <summary>
/// Задача 66: <see cref="PlanExecutionEngine"/> со строками Freedom — ворота Freedom (все 16 комбинаций), только ручные планы, .ITS и маржа невозможны, цена по сетке
/// <c>min_step</c> и дрейф 0.2% от утверждённого лимита, идемпотентность по <c>user_order_id</c> (ответ потерян → сверка, без повторной отправки), часы торгов
/// .US/.EU и снятие перед закрытием, сверка комиссии по фактическому маршруту из выписки, смешанный план с воротами по брокеру, смена счёта торгового ключа.
/// Время — будни относительно «сегодня».
/// </summary>
public class FreedomPlanExecutionTests
{
    private const ulong UserId = 7;
    private const ulong TInvestAccountId = 11;
    private const ulong FreedomAccountId = 21;
    private const string FreedomNumber = "100001";

    private static DateTime NextWeekdayUtc(int hourUtc, int minute = 0)
    {
        var d = DateTime.UtcNow.Date.AddDays(1);
        while (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) d = d.AddDays(1);
        return DateTime.SpecifyKind(d.AddHours(hourUtc).AddMinutes(minute), DateTimeKind.Utc);
    }

    private static readonly DateTime Noon = NextWeekdayUtc(9); // 12:00 МСК — открыт .EU (11:00–18:30), закрыт .US (17:30–23:00)
    private static readonly DateTime UsEvening = NextWeekdayUtc(17); // 20:00 МСК — открыт .US

    private readonly InMemoryPlans _plans = new();
    private readonly InMemoryOrders _orders = new();
    private readonly FakeOrderGateway _tinvest = new();
    private readonly FakeFreedomOrderGateway _freedom = new();
    private readonly Notifier _notifier = new();
    private readonly Gate _gate = new();
    private readonly Mock<IUserCredentialRepository> _credentialRecords = new();
    private readonly ExecutionOptions _options = new() { Enabled = true };

    private readonly List<InstrumentTradingMeta> _meta =
    [
        new() { Broker = BrokerIds.Freedom, InstrumentKey = "VUAA.EU", Ticker = "VUAA.EU", Lot = 1, MinPriceIncrement = 0.01m, Currency = "USD", ApiTradeAvailable = true, BuyAvailable = true, SellAvailable = true },
        new() { Broker = BrokerIds.Freedom, InstrumentKey = "SPYM.US", Ticker = "SPYM.US", Lot = 1, MinPriceIncrement = 0.01m, Currency = "USD", ApiTradeAvailable = true, BuyAvailable = true, SellAvailable = true, ItsTwin = true },
        new() { Broker = BrokerIds.Freedom, InstrumentKey = "SPYM.ITS", Ticker = "SPYM.ITS", Lot = 1, MinPriceIncrement = 0.01m, Currency = "USD", ApiTradeAvailable = false, BuyAvailable = false, SellAvailable = false },
        new() { Broker = BrokerIds.TInvest, InstrumentKey = "TMON", Uid = "uid-tmon", Ticker = "TMON@", ClassCode = "TQTF", Lot = 1, MinPriceIncrement = 0.01m, Currency = "RUB", ApiTradeAvailable = true, BuyAvailable = true, SellAvailable = true },
    ];

    private PlanExecutionEngine Engine()
    {
        var meta = new Mock<IInstrumentTradingMetaRepository>();
        meta.Setup(m => m.GetByKeysAsync(It.IsAny<string>(), It.IsAny<IReadOnlyCollection<string>>()))
            .ReturnsAsync((string broker, IReadOnlyCollection<string> keys) =>
                _meta.Where(x => x.Broker == broker && keys.Contains(x.InstrumentKey, StringComparer.OrdinalIgnoreCase)).ToList());
        var accounts = new Mock<IAccountRepository>();
        accounts.Setup(a => a.GetByUserIdAndBrokerAsync(UserId, BrokerIds.TInvest))
            .ReturnsAsync([new Account { Id = TInvestAccountId, UserId = UserId, Broker = BrokerIds.TInvest, BrokerAccountId = "ACC-1" }]);
        accounts.Setup(a => a.GetByUserIdAndBrokerAsync(UserId, BrokerIds.Freedom))
            .ReturnsAsync([new Account { Id = FreedomAccountId, UserId = UserId, Broker = BrokerIds.Freedom, BrokerAccountId = FreedomNumber }]);
        var monitor = Mock.Of<IOptionsMonitor<ExecutionOptions>>(m => m.CurrentValue == _options);
        var baskets = new Mock<ICapitalBasketRepository>();
        baskets.Setup(b => b.GetByUserAsync(UserId, It.IsAny<string?>())).ReturnsAsync([]);
        return new PlanExecutionEngine(
            _plans, _orders, meta.Object, accounts.Object, _credentialRecords.Object, baskets.Object, _tinvest, _gate, _notifier, new NullPortfolioSyncTrigger(), monitor,
            Options.Create(new ExecutionLimitsOptions()), Options.Create(new PolicyOptions()), NullLogger<PlanExecutionEngine>.Instance, _freedom);
    }

    private void Quote(string ticker, decimal bid, decimal ask) => _freedom.Quotes[ticker] = new FreedomMarketQuote(bid, ask, 0.01m, "USD");

    private static TradePlanLine FreedomLine(
        int no = 1, string ticker = "VUAA.EU", string side = PlanSides.Buy, long qty = 3, decimal price = 147.70m, decimal fee = 1.73m) => new()
    {
        LineNo = no, Broker = BrokerIds.Freedom, AccountId = FreedomAccountId, InstrumentKey = ticker, Ticker = ticker, Side = side, QuantityLots = qty, LotSize = 1m,
        LimitPrice = price, EstAmount = price * qty, EstFee = fee, Currency = "USD", Reason = "тест",
    };

    private static TradePlanLine TInvestLine(int no) => new()
    {
        LineNo = no, Broker = BrokerIds.TInvest, AccountId = TInvestAccountId, InstrumentKey = "TMON", Ticker = "TMON@", Side = PlanSides.Buy, QuantityLots = 10, LotSize = 1m,
        LimitPrice = 166.57m, EstAmount = 1665.7m, EstFee = 0m, Currency = "RUB", Reason = "тест",
    };

    private TradePlan AddPlan(DateTime now, string status = PlanStatuses.Approved, string via = DecidedVia.Web, params TradePlanLine[] lines)
    {
        var plan = new TradePlan
        {
            UserId = UserId, Rule = PlanRules.P7FreedomTopUp, Trigger = "t", Status = status, DryRun = false, CreatedAt = now.AddMinutes(-1), ExpiresAt = now.AddHours(2),
            DecidedAt = now, DecidedVia = via, BotNonce = "nonce12345A", Lines = lines.Length == 0 ? [FreedomLine()] : lines.ToList(),
        };
        _plans.Add(plan);
        return plan;
    }

    private List<TradeOrder> OrdersOf(TradePlan plan) => _orders.All.Where(o => o.PlanId == plan.Id).OrderBy(o => o.LineNo).ThenBy(o => o.Attempt).ToList();

    // ─── Ворота ────────────────────────────────────────────────────────────────────────────

    public static IEnumerable<object[]> GateCombos() =>
        from env in new[] { false, true }
        from live in new[] { false, true }
        from key in new[] { false, true }
        from paused in new[] { false, true }
        select new object[] { env, live, key, paused };

    [Theory]
    [MemberData(nameof(GateCombos))]
    public async Task WithoutAllFourGates_NothingIsSent(bool env, bool live, bool key, bool paused)
    {
        _gate.Freedom = ExecutionGate.Evaluate(env, live ? ExecutionModes.Live : ExecutionModes.Observe, key, paused, ExecutionGate.NoFreedomTradeCredential);
        var plan = AddPlan(Noon);
        Quote("VUAA.EU", 147.65m, 147.75m);

        await Engine().RunTickAsync(Noon);

        var allOpen = env && live && key && !paused;
        _freedom.PostCount.Should().Be(allOpen ? 1 : 0);
        if (!allOpen)
        {
            _plans[plan.Id].Status.Should().Be(PlanStatuses.Failed);
            _orders.All.Should().BeEmpty("при закрытых воротах в trade_orders ничего не пишется");
        }
    }

    [Fact]
    public async Task NoFreedomTradeKey_ReasonNamesFreedomKey()
    {
        _gate.Freedom = ExecutionGate.Evaluate(true, ExecutionModes.Live, false, false, ExecutionGate.NoFreedomTradeCredential);
        AddPlan(Noon);

        await Engine().RunTickAsync(Noon);

        _notifier.Texts.Should().Contain(t => t.Contains("freedom_trade"));
    }

    [Fact]
    public async Task TradeKeyOfOtherAccount_Halts_AndMarksFreedomTradeInvalid()
    {
        _freedom.Account = new FreedomTradeAccountInfo("999999", 1431);
        var plan = AddPlan(Noon);
        Quote("VUAA.EU", 147.65m, 147.75m);

        await Engine().RunTickAsync(Noon);

        _freedom.PostCount.Should().Be(0);
        _plans[plan.Id].Status.Should().Be(PlanStatuses.Failed);
        _credentialRecords.Verify(r => r.UpdateStatusAsync(UserId, CredentialProviders.FreedomTrade, CredentialStatus.Invalid, It.IsAny<string?>()), Times.Once);
    }

    [Fact]
    public async Task TradeKeyCheckUnavailable_SkipsTick_NoOrder_NoHalt()
    {
        _freedom.AccountError = new FreedomGatewayException(GatewayErrorKind.Transient, "network", "сеть");
        var plan = AddPlan(Noon);
        Quote("VUAA.EU", 147.65m, 147.75m);

        await Engine().RunTickAsync(Noon);

        _freedom.PostCount.Should().Be(0);
        _plans[plan.Id].Status.Should().Be(PlanStatuses.Approved);
    }

    [Fact]
    public async Task AutoApprovedFreedomPlan_IsNeverExecuted()
    {
        var plan = AddPlan(Noon, PlanStatuses.AutoApproved, DecidedVia.Auto);
        Quote("VUAA.EU", 147.65m, 147.75m);

        await Engine().RunTickAsync(Noon);

        _freedom.PostCount.Should().Be(0);
        _plans[plan.Id].Status.Should().Be(PlanStatuses.Failed);
        _notifier.Texts.Should().Contain(t => t.Contains("ручного подтверждения"));
    }

    [Fact]
    public async Task SubmittingAutoPlanWithFreedomLine_LineRejectedBeforeNetwork()
    {
        // Защита в глубину: даже если авто-план как-то оказался Submitting, строка Freedom не уходит.
        var plan = AddPlan(Noon, PlanStatuses.Submitting, DecidedVia.Auto);
        Quote("VUAA.EU", 147.65m, 147.75m);

        await Engine().RunTickAsync(Noon);

        _freedom.PostCount.Should().Be(0);
        OrdersOf(plan).Should().ContainSingle().Which.ErrorCode.Should().Be(TradeOrderErrorCodes.Preflight);
    }

    // ─── Заявка ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ApprovedPlan_PostsLimitOrder_UserOrderIdPersistedBeforeSend()
    {
        var plan = AddPlan(Noon);
        Quote("VUAA.EU", 147.65m, 147.75m);
        TradeOrder? persistedAtSend = null;
        string? statusAtSend = null;
        _freedom.OnPost = o =>
        {
            persistedAtSend = _orders.All.SingleOrDefault(x => x.ClientOrderId == o.UserOrderId);
            statusAtSend = persistedAtSend?.Status;
            return new GatewayOrderState("900000001", o.UserOrderId, TradeOrderStatuses.Submitted, o.Quantity, 0, null, null, "USD", null);
        };

        var result = await Engine().RunTickAsync(Noon);

        result.OrdersPosted.Should().Be(1);
        var (_, posted) = _freedom.Posted.Single();
        posted.Ticker.Should().Be("VUAA.EU");
        posted.Side.Should().Be(PlanSides.Buy);
        posted.Quantity.Should().Be(3);
        posted.LimitPrice.Should().Be(147.70m);
        FreedomOrderRules.IsValidUserOrderId(posted.UserOrderId).Should().BeTrue();
        persistedAtSend.Should().NotBeNull("строка trade_orders с user_order_id записана ДО отправки");
        statusAtSend.Should().Be(TradeOrderStatuses.New);
        persistedAtSend.Broker.Should().Be(BrokerIds.Freedom);
        var order = OrdersOf(plan).Single();
        order.BrokerOrderId.Should().Be("900000001");
        order.Status.Should().Be(TradeOrderStatuses.Submitted);
        order.PriceUnit.Should().Be(OrderPriceUnits.Currency);
        order.Currency.Should().Be("USD");
        _plans[plan.Id].Status.Should().Be(PlanStatuses.Submitting);
    }

    [Fact]
    public async Task LimitPrice_RoundedToMinStep_BuyDown()
    {
        AddPlan(Noon, lines: FreedomLine(price: 147.709m));
        Quote("VUAA.EU", 147.65m, 147.75m);

        await Engine().RunTickAsync(Noon);

        _freedom.Posted.Single().Order.LimitPrice.Should().Be(147.70m);
    }

    [Fact]
    public async Task MidDriftAbove02Percent_NotSent()
    {
        var plan = AddPlan(Noon);
        Quote("VUAA.EU", 148.10m, 148.20m); // mid 148.15 — +0.30% от 147.70

        await Engine().RunTickAsync(Noon);

        _freedom.PostCount.Should().Be(0);
        OrdersOf(plan).Single().ErrorCode.Should().Be(PreflightChecks.PriceDrift);
    }

    [Fact]
    public async Task MidDriftWithin02Percent_Sent()
    {
        AddPlan(Noon);
        Quote("VUAA.EU", 147.85m, 147.95m); // mid 147.90 — +0.135%

        await Engine().RunTickAsync(Noon);

        _freedom.PostCount.Should().Be(1);
    }

    [Fact]
    public async Task ItsTicker_RejectedBeforeAnyNetworkCall()
    {
        var plan = AddPlan(UsEvening, lines: FreedomLine(ticker: "SPYM.ITS", price: 54m, fee: 0.27m, qty: 1));
        Quote("SPYM.ITS", 53.9m, 54.1m);

        await Engine().RunTickAsync(UsEvening);

        _freedom.PostCount.Should().Be(0);
        _plans[plan.Id].Status.Should().Be(PlanStatuses.Failed);
        _notifier.Texts.Should().Contain(t => t.Contains("ITS"));
        _orders.All.Should().BeEmpty();
    }

    [Fact]
    public async Task SubmittingPlanWithItsLine_LineFailedBeforeNetwork()
    {
        // Защита в глубину: строка .ITS в уже исполняемом плане закрывается до любых запросов к брокеру.
        var plan = AddPlan(UsEvening, PlanStatuses.Submitting, lines: [FreedomLine(1, ticker: "SPYM.US", price: 54m, qty: 1, fee: 0.27m)]);
        plan.Lines[0].Ticker = "SPYM.ITS";
        Quote("SPYM.US", 53.95m, 54.05m);

        await Engine().RunTickAsync(UsEvening);

        _freedom.PostCount.Should().Be(0);
        var order = OrdersOf(plan).Single();
        order.ErrorCode.Should().Be(TradeOrderErrorCodes.Preflight);
        order.ErrorMessage.Should().Contain("ITS");
    }

    [Fact]
    public async Task InsufficientCash_NotSent()
    {
        _freedom.Cash["USD"] = 100m;
        var plan = AddPlan(Noon);
        Quote("VUAA.EU", 147.65m, 147.75m);

        await Engine().RunTickAsync(Noon);

        _freedom.PostCount.Should().Be(0);
        OrdersOf(plan).Single().ErrorCode.Should().Be(PreflightChecks.InsufficientFunds);
    }

    [Fact]
    public async Task SellWithoutPieces_NotSent_WithPieces_SentAsAction3Side()
    {
        var plan = AddPlan(Noon, lines: FreedomLine(side: PlanSides.Sell));
        Quote("VUAA.EU", 147.65m, 147.75m);
        await Engine().RunTickAsync(Noon);
        _freedom.PostCount.Should().Be(0);
        OrdersOf(plan).Single().ErrorCode.Should().Be(PreflightChecks.InsufficientPosition);

        _freedom.Pieces["VUAA.EU"] = 8m;
        AddPlan(Noon, lines: FreedomLine(side: PlanSides.Sell));
        await Engine().RunTickAsync(Noon);
        _freedom.Posted.Single().Order.Side.Should().Be(PlanSides.Sell);
    }

    // ─── Идемпотентность по user_order_id ─────────────────────────────────────────────────

    [Fact]
    public async Task ResponseLost_OrderReconciledByUserOrderId_NeverResubmitted()
    {
        var plan = AddPlan(Noon);
        Quote("VUAA.EU", 147.65m, 147.75m);
        _freedom.LoseResponse = true;

        await Engine().RunTickAsync(Noon);

        _freedom.PostCount.Should().Be(1);
        var order = OrdersOf(plan).Single();
        order.Status.Should().Be(TradeOrderStatuses.New, "исход неизвестен — только сверка");
        order.BrokerOrderId.Should().BeNull();

        // «Рестарт»: новый движок, тот же репозиторий. Заявка находится у брокера по user_order_id.
        _freedom.LoseResponse = false;
        await Engine().RunTickAsync(Noon.AddSeconds(20));

        _freedom.PostCount.Should().Be(1, "повторной отправки нет");
        order = OrdersOf(plan).Single();
        order.Status.Should().Be(TradeOrderStatuses.Submitted);
        order.BrokerOrderId.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ResponseLost_UnknownAtBroker_FailedAfterTimeout_NeverResubmitted()
    {
        var plan = AddPlan(Noon);
        Quote("VUAA.EU", 147.65m, 147.75m);
        _freedom.OnPost = _ => throw new FreedomGatewayException(GatewayErrorKind.Transient, "timeout", "таймаут");

        await Engine().RunTickAsync(Noon);
        await Engine().RunTickAsync(Noon.AddMinutes(1));
        _freedom.PostCount.Should().Be(1);
        OrdersOf(plan).Single().Status.Should().Be(TradeOrderStatuses.New);

        await Engine().RunTickAsync(Noon.Add(_options.UnconfirmedOrderTimeout).AddMinutes(1));

        _freedom.PostCount.Should().Be(1, "неизвестную заявку не отправляем повторно никогда");
        var order = OrdersOf(plan).Single();
        order.Status.Should().Be(TradeOrderStatuses.Failed);
        order.ErrorCode.Should().Be(TradeOrderErrorCodes.UnknownState);
        _plans[plan.Id].Status.Should().Be(PlanStatuses.Failed);
    }

    [Fact]
    public async Task BrokerRejects_LineFailsWithBrokerText_NoRetry()
    {
        var plan = AddPlan(Noon);
        Quote("VUAA.EU", 147.65m, 147.75m);
        _freedom.OnPost = _ => throw new FreedomGatewayException(GatewayErrorKind.Rejected, "broker_rejected", "Freedom API putTradeOrder: The instrument is not available for trading.");

        await Engine().RunTickAsync(Noon);
        await Engine().RunTickAsync(Noon.AddMinutes(10));

        _freedom.PostCount.Should().Be(1);
        var order = OrdersOf(plan).Single();
        order.Status.Should().Be(TradeOrderStatuses.Rejected);
        order.ErrorCode.Should().Be(TradeOrderErrorCodes.BrokerRejected);
        _plans[plan.Id].Status.Should().Be(PlanStatuses.Failed);
        _notifier.Texts.Should().Contain(t => t.Contains("not available for trading"));
    }

    // ─── Часы торгов ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UsTicker_OutsideUsSession_PlanWaits()
    {
        var plan = AddPlan(Noon, lines: FreedomLine(ticker: "SPYM.US", price: 54m, qty: 1, fee: 0.27m));
        plan.ExpiresAt = UsEvening.AddHours(1);
        Quote("SPYM.US", 53.95m, 54.05m);

        await Engine().RunTickAsync(Noon); // 12:00 МСК — .US закрыт

        _freedom.PostCount.Should().Be(0);
        _plans[plan.Id].Status.Should().Be(PlanStatuses.Approved);

        await Engine().RunTickAsync(UsEvening); // 20:00 МСК
        _freedom.PostCount.Should().Be(1);
    }

    [Fact]
    public async Task UsOrder_CancelledTenMinutesBeforeClose()
    {
        var plan = AddPlan(UsEvening, lines: FreedomLine(ticker: "SPYM.US", price: 54m, qty: 1, fee: 0.27m));
        plan.ExpiresAt = UsEvening.AddHours(4);
        Quote("SPYM.US", 53.95m, 54.05m);
        await Engine().RunTickAsync(UsEvening);
        var brokerId = OrdersOf(plan).Single().BrokerOrderId!;

        var beforeClose = UsEvening.Date.AddHours(19).AddMinutes(51); // 22:51 МСК — меньше 10 мин до 23:00
        await Engine().RunTickAsync(beforeClose);

        _freedom.Cancelled.Should().Contain(brokerId);
        var order = OrdersOf(plan).Single();
        order.Status.Should().Be(TradeOrderStatuses.Cancelled);
        order.ErrorCode.Should().Be(TradeOrderErrorCodes.SessionClose);
        _plans[plan.Id].Status.Should().Be(PlanStatuses.Failed);
        _freedom.PostCount.Should().Be(1);
    }

    [Fact]
    public async Task EuOrder_RepricedWithinDriftAfterTimeout()
    {
        var plan = AddPlan(Noon);
        Quote("VUAA.EU", 147.65m, 147.80m);
        await Engine().RunTickAsync(Noon);

        await Engine().RunTickAsync(Noon.Add(_options.RepriceAfter).AddSeconds(1));

        _freedom.PostCount.Should().Be(2);
        var second = _freedom.Posted[1].Order;
        second.LimitPrice.Should().BeGreaterThan(147.70m);
        second.LimitPrice.Should().BeLessThanOrEqualTo(Math.Floor(147.70m * 1.002m * 100m) / 100m, "перевыставление не выходит за 0.2% от утверждённого лимита");
        second.UserOrderId.Should().NotBe(_freedom.Posted[0].Order.UserOrderId);
        OrdersOf(plan)[0].ErrorCode.Should().Be(TradeOrderErrorCodes.Reprice);
    }

    // ─── Комиссия ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Fill_FeeMatchesModelByActualRoute_PlanDone_NoMismatch()
    {
        var plan = AddPlan(Noon);
        Quote("VUAA.EU", 147.65m, 147.75m);
        await Engine().RunTickAsync(Noon);
        var brokerId = OrdersOf(plan).Single().BrokerOrderId!;

        // Europe, фонды, Standard: 0.12% × 443.10 + $1.2 = $1.73.
        _freedom.Fill(brokerId, 3, 147.70m, "853000001", 1.73m, "europe", "funds");
        await Engine().RunTickAsync(Noon.AddSeconds(20));

        var order = OrdersOf(plan).Single();
        order.Status.Should().Be(TradeOrderStatuses.Filled);
        order.ExecutedCommission.Should().Be(1.73m);
        order.AvgFillPrice.Should().Be(147.70m);
        _plans[plan.Id].Status.Should().Be(PlanStatuses.Done);
        _notifier.Texts.Should().NotContain(t => t.Contains("отличается от оценки"));
    }

    [Fact]
    public async Task Fill_RoutedViaIts_ModelUsesActualRoute_NotPlanEstimate()
    {
        // План оценил комиссию по Europe ($1.25), брокер провёл через ITS-фонды (0.5%): факт $0.27 совпадает с моделью по ФАКТИЧЕСКОМУ маршруту — расхождения нет.
        var plan = AddPlan(UsEvening, lines: FreedomLine(ticker: "SPYM.US", price: 54m, qty: 1, fee: 1.25m));
        Quote("SPYM.US", 53.95m, 54.05m);
        await Engine().RunTickAsync(UsEvening);
        _freedom.Fill(OrdersOf(plan).Single().BrokerOrderId!, 1, 54m, "853000002", 0.27m, "kazakhstan_its", "funds");

        await Engine().RunTickAsync(UsEvening.AddSeconds(20));

        _plans[plan.Id].Status.Should().Be(PlanStatuses.Done);
        OrdersOf(plan).Single().ExecutedCommission.Should().Be(0.27m);
        _notifier.Texts.Should().NotContain(t => t.Contains("отличается от оценки"));
    }

    [Fact]
    public async Task Fill_FeeAboveModel_MismatchNotifiedWithRoute()
    {
        var plan = AddPlan(Noon);
        Quote("VUAA.EU", 147.65m, 147.75m);
        await Engine().RunTickAsync(Noon);
        _freedom.Fill(OrdersOf(plan).Single().BrokerOrderId!, 3, 147.70m, "853000003", 2.50m, "europe", "funds");

        await Engine().RunTickAsync(Noon.AddSeconds(20));

        _notifier.Texts.Should().Contain(t => t.Contains("отличается от оценки") && t.Contains("73 USD") && t.Contains("europe"));
    }

    [Fact]
    public async Task Fill_CommissionNotYetInStatement_Deferred_ThenReconciled()
    {
        var plan = AddPlan(Noon);
        Quote("VUAA.EU", 147.65m, 147.75m);
        await Engine().RunTickAsync(Noon);
        var brokerId = OrdersOf(plan).Single().BrokerOrderId!;
        _freedom.Fill(brokerId, 3, 147.70m, "853000004", commission: null);

        await Engine().RunTickAsync(Noon.AddSeconds(20));
        await Engine().RunTickAsync(Noon.AddSeconds(40));

        _plans[plan.Id].Status.Should().Be(PlanStatuses.Submitting, "план закрывается только после сверки комиссии");
        OrdersOf(plan).Single().FeeRechecks.Should().Be(2);

        _freedom.AddCommission("853000004", 1.73m);
        await Engine().RunTickAsync(Noon.AddSeconds(60));

        _plans[plan.Id].Status.Should().Be(PlanStatuses.Done);
        OrdersOf(plan).Single().ExecutedCommission.Should().Be(1.73m);
        _notifier.Texts.Should().Contain(t => t.Contains("исполнено 3 лот"));
    }

    // ─── Смешанный план: ворота по брокеру ────────────────────────────────────────────────

    [Fact]
    public async Task MixedPlan_TInvestGateClosed_FreedomLineStillExecutes()
    {
        _gate.TInvest = ExecutionGate.Evaluate(true, ExecutionModes.Live, false, false); // нет торгового токена T-Invest
        var plan = AddPlan(Noon, lines: [TInvestLine(1), FreedomLine(2)]);
        _tinvest.Books["uid-tmon"] = new GatewayOrderBook(166.50m, 166.60m, Noon.AddSeconds(-3));
        Quote("VUAA.EU", 147.65m, 147.75m);

        await Engine().RunTickAsync(Noon);

        _tinvest.PostCount.Should().Be(0);
        _freedom.PostCount.Should().Be(1);
        var orders = OrdersOf(plan);
        orders.Single(o => o.LineNo == 1).ErrorCode.Should().Be(TradeOrderErrorCodes.Halted);
        orders.Single(o => o.LineNo == 2).Broker.Should().Be(BrokerIds.Freedom);
    }

    [Fact]
    public async Task MixedPlan_FreedomGateClosed_TInvestLineStillExecutes()
    {
        _gate.Freedom = ExecutionGate.Evaluate(true, ExecutionModes.Live, false, false, ExecutionGate.NoFreedomTradeCredential);
        var plan = AddPlan(Noon, lines: [TInvestLine(1), FreedomLine(2)]);
        _tinvest.Books["uid-tmon"] = new GatewayOrderBook(166.50m, 166.60m, Noon.AddSeconds(-3));
        Quote("VUAA.EU", 147.65m, 147.75m);

        await Engine().RunTickAsync(Noon);

        _tinvest.PostCount.Should().Be(1);
        _freedom.PostCount.Should().Be(0);
        _tinvest.Fill(_tinvest.Posted[0].Order.ClientOrderId, 10, 166.57m, 0m);
        await Engine().RunTickAsync(Noon.AddSeconds(20));

        OrdersOf(plan).Single(o => o.LineNo == 2).ErrorCode.Should().Be(TradeOrderErrorCodes.Halted);
        _freedom.PostCount.Should().Be(0);
        _plans[plan.Id].Status.Should().Be(PlanStatuses.PartiallyDone);
    }

    [Fact]
    public async Task MixedPlan_BothGatesClosed_PlanFailsLikeBefore()
    {
        _gate.TInvest = ExecutionGate.Evaluate(false, ExecutionModes.Live, true, false);
        _gate.Freedom = ExecutionGate.Evaluate(false, ExecutionModes.Live, true, false, ExecutionGate.NoFreedomTradeCredential);
        var plan = AddPlan(Noon, lines: [TInvestLine(1), FreedomLine(2)]);

        await Engine().RunTickAsync(Noon);

        _tinvest.PostCount.Should().Be(0);
        _freedom.PostCount.Should().Be(0);
        _plans[plan.Id].Status.Should().Be(PlanStatuses.Failed);
    }

    [Fact]
    public async Task GateClosesWhileOrderActive_FreedomOrderCancelled()
    {
        var plan = AddPlan(Noon);
        Quote("VUAA.EU", 147.65m, 147.75m);
        await Engine().RunTickAsync(Noon);
        var brokerId = OrdersOf(plan).Single().BrokerOrderId!;

        _gate.Freedom = ExecutionGate.Evaluate(true, ExecutionModes.Live, true, true, ExecutionGate.NoFreedomTradeCredential); // /stop
        await Engine().RunTickAsync(Noon.AddSeconds(20));

        _freedom.Cancelled.Should().Contain(brokerId);
        OrdersOf(plan).Single().ErrorCode.Should().Be(TradeOrderErrorCodes.Halted);
        _plans[plan.Id].Status.Should().Be(PlanStatuses.Failed);
    }

    // ─── Двойники ──────────────────────────────────────────────────────────────────────────

    private sealed class Gate : IExecutionGateResolver
    {
        public ExecutionGateResult TInvest { get; set; } = ExecutionGate.Evaluate(true, ExecutionModes.Live, true, false);
        public ExecutionGateResult Freedom { get; set; } = ExecutionGate.Evaluate(true, ExecutionModes.Live, true, false, ExecutionGate.NoFreedomTradeCredential);

        public Task<ExecutionGateResult> ResolveAsync(ulong userId) => Task.FromResult(TInvest);

        public Task<ExecutionGateResult> ResolveForBrokerAsync(ulong userId, string broker) =>
            Task.FromResult(broker == BrokerIds.Freedom ? Freedom : broker == BrokerIds.TInvest ? TInvest : ExecutionGate.Evaluate(false, null, false, false));
    }

    private sealed class Notifier : IExecutionNotifier
    {
        public List<BotMessage> Messages { get; } = [];
        public IEnumerable<string> Texts => Messages.Select(m => m.Html);

        public Task NotifyAsync(ulong userId, BotMessage message, CancellationToken ct = default)
        {
            Messages.Add(message);
            return Task.CompletedTask;
        }
    }
}

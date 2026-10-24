using Bonds.Core.Capital;
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
/// Задача 65: <see cref="PlanExecutionEngine"/> с двойником шлюза и in-memory репозиториями — идемпотентность после «рестарта», перевыставление в пределах дрейфа,
/// продажи раньше покупок, отмена перед закрытием, сверка комиссии, ворота и сверка торгового токена. Время — будний день относительно «сегодня», 12:00 МСК.
/// </summary>
public class PlanExecutionEngineTests
{
    private const ulong UserId = 7;
    private const ulong AccountId = 11;
    private const string BrokerAccount = "ACC-1";
    private const string FundUid = "uid-tmon";
    private const string BondUid = "uid-ofz";

    private static readonly DateTime Noon = NextWeekdayUtc(9); // 12:00 МСК

    private static DateTime NextWeekdayUtc(int hourUtc)
    {
        var d = DateTime.UtcNow.Date.AddDays(1);
        while (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) d = d.AddDays(1);
        return DateTime.SpecifyKind(d.AddHours(hourUtc), DateTimeKind.Utc);
    }

    private readonly InMemoryPlans _plans = new();
    private readonly InMemoryOrders _orders = new();
    private readonly FakeOrderGateway _gateway = new();
    private readonly RecordingNotifier _notifier = new();
    private readonly FakeGate _gate = new();
    private readonly Mock<IUserCredentialRepository> _credentialRecords = new();
    private readonly CountingSync _sync = new();
    private readonly ExecutionOptions _options = new() { Enabled = true };
    private readonly ExecutionLimitsOptions _limits = new();

    /// <summary>Корзины пользователя: «Ликвидность» с авто-покупкой и TMON — белый список AutoApproved.</summary>
    private readonly List<CapitalBasket> _baskets =
    [
        new()
        {
            Id = 1, UserId = UserId, Code = "liquidity", Title = "Ликвидность", AutoBuy = true,
            Members = [new CapitalBasketMember { Id = 1, BasketId = 1, Broker = BrokerIds.TInvest, InstrumentKey = "TMON" }],
        },
    ];

    private readonly List<InstrumentTradingMeta> _meta =
    [
        new() { Broker = BrokerIds.TInvest, InstrumentKey = "TMON", Uid = FundUid, Ticker = "TMON@", ClassCode = "TQTF", Lot = 1, MinPriceIncrement = 0.01m, Currency = "RUB", ApiTradeAvailable = true, BuyAvailable = true, SellAvailable = true, PaysDistributions = false },
        new() { Broker = BrokerIds.TInvest, InstrumentKey = "RU000OFZ", Uid = BondUid, Ticker = "SU26238", ClassCode = "TQOB", Lot = 1, MinPriceIncrement = 0.1m, MinPriceIncrementPoints = 0.01m, Currency = "RUB", ApiTradeAvailable = true, BuyAvailable = true, SellAvailable = true },
    ];

    private PlanExecutionEngine Engine()
    {
        var meta = new Mock<IInstrumentTradingMetaRepository>();
        meta.Setup(m => m.GetByKeysAsync(BrokerIds.TInvest, It.IsAny<IReadOnlyCollection<string>>()))
            .ReturnsAsync((string _, IReadOnlyCollection<string> keys) => _meta.Where(x => keys.Contains(x.InstrumentKey, StringComparer.OrdinalIgnoreCase)).ToList());
        var accounts = new Mock<IAccountRepository>();
        accounts.Setup(a => a.GetByUserIdAndBrokerAsync(UserId, BrokerIds.TInvest))
            .ReturnsAsync([new Account { Id = AccountId, UserId = UserId, Broker = BrokerIds.TInvest, BrokerAccountId = BrokerAccount }]);
        var monitor = Mock.Of<IOptionsMonitor<ExecutionOptions>>(m => m.CurrentValue == _options);
        var baskets = new Mock<ICapitalBasketRepository>();
        baskets.Setup(b => b.GetByUserAsync(UserId, It.IsAny<string?>())).ReturnsAsync(() => _baskets);
        return new PlanExecutionEngine(
            _plans, _orders, meta.Object, accounts.Object, _credentialRecords.Object, baskets.Object, _gateway, _gate, _notifier, _sync, monitor,
            Options.Create(_limits), Options.Create(new PolicyOptions()), NullLogger<PlanExecutionEngine>.Instance);
    }

    private void Book(string uid, decimal bid, decimal ask, DateTime at) => _gateway.Books[uid] = new GatewayOrderBook(bid, ask, at.AddSeconds(-3));

    private static TradePlanLine FundBuy(int no = 1, long lots = 10, decimal price = 166.57m, decimal fee = 0m) => new()
    {
        LineNo = no, Broker = BrokerIds.TInvest, AccountId = AccountId, InstrumentKey = "TMON", Ticker = "TMON@", Side = PlanSides.Buy, QuantityLots = lots, LotSize = 1m,
        LimitPrice = price, EstAmount = price * lots, EstFee = fee, Currency = "RUB", Reason = "тест",
    };

    private static TradePlanLine BondSell(int no, long lots = 2, decimal points = 92.70m) => new()
    {
        LineNo = no, Broker = BrokerIds.TInvest, AccountId = AccountId, InstrumentKey = "RU000OFZ", Ticker = "SU26238", Side = PlanSides.Sell, QuantityLots = lots, LotSize = 1m,
        LimitPrice = points * 10m, LimitPricePoints = points, EstAmount = points * 10m * lots, EstFee = 0.37m * lots, Currency = "RUB", Reason = "тест",
    };

    private TradePlan AddPlan(string status = PlanStatuses.Approved, string via = DecidedVia.Web, params TradePlanLine[] lines)
    {
        var plan = new TradePlan
        {
            UserId = UserId, Rule = PlanRules.Manual, Trigger = "t", Status = status, DryRun = false, CreatedAt = Noon.AddMinutes(-1), ExpiresAt = Noon.AddMinutes(29),
            DecidedAt = Noon, DecidedVia = via, BotNonce = "nonce12345A", Lines = lines.Length == 0 ? [FundBuy()] : lines.ToList(),
        };
        _plans.Add(plan);
        return plan;
    }

    // ─── Ворота ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GateClosed_NothingPosted_ApprovedPlanFailsWithReason()
    {
        _gate.Result = ExecutionGate.Evaluate(true, ExecutionModes.Observe, true, false);
        var plan = AddPlan();
        Book(FundUid, 166.50m, 166.60m, Noon);

        await Engine().RunTickAsync(Noon);

        _gateway.PostCount.Should().Be(0);
        _plans[plan.Id].Status.Should().Be(PlanStatuses.Failed);
        _notifier.Texts.Should().Contain(t => t.Contains("Режим наблюдения"));
    }

    [Fact]
    public async Task TradeTokenSeesOtherAccount_Halts_AndMarksCredentialInvalid()
    {
        _gateway.Accounts.Clear();
        _gateway.Accounts.Add(new TInvestTradeAccount("ACC-OTHER", true, true));
        var plan = AddPlan();
        Book(FundUid, 166.50m, 166.60m, Noon);

        await Engine().RunTickAsync(Noon);

        _gateway.PostCount.Should().Be(0);
        _plans[plan.Id].Status.Should().Be(PlanStatuses.Failed);
        _credentialRecords.Verify(r => r.UpdateStatusAsync(UserId, CredentialProviders.TInvestTrade, CredentialStatus.Invalid, It.IsAny<string?>()), Times.Once);
    }

    [Fact]
    public async Task ExpiredApprovedPlan_IsNotExecuted()
    {
        var plan = AddPlan();
        Book(FundUid, 166.50m, 166.60m, Noon.AddHours(1));

        await Engine().RunTickAsync(Noon.AddHours(1));

        _gateway.PostCount.Should().Be(0);
        _plans[plan.Id].Status.Should().Be(PlanStatuses.Expired);
    }

    [Fact]
    public async Task PlanWithForeignLine_IsNotExecuted()
    {
        var line = FundBuy();
        line.Broker = BrokerIds.Freedom;
        var plan = AddPlan(lines: line);
        await Engine().RunTickAsync(Noon);
        _gateway.PostCount.Should().Be(0);
        _plans[plan.Id].Status.Should().Be(PlanStatuses.Failed);
    }

    // ─── Отправка и идемпотентность ────────────────────────────────────────────────────────

    [Fact]
    public async Task HappyPath_OrderPersistedBeforePost_ThenFilled_PlanDone_FeeChecked_SyncRequested()
    {
        var plan = AddPlan();
        Book(FundUid, 166.50m, 166.60m, Noon);
        _gateway.OnPost = o =>
        {
            // client_order_id уже в БД со статусом New в момент вызова брокера.
            _orders.All.Should().ContainSingle(x => x.ClientOrderId == o.ClientOrderId && x.Status == TradeOrderStatuses.New);
            return FakeOrderGateway.Accepted(o);
        };

        await Engine().RunTickAsync(Noon);

        _gateway.Posted.Should().ContainSingle();
        var posted = _gateway.Posted[0].Order;
        posted.Should().Match<GatewayLimitOrder>(o => o.AccountId == BrokerAccount && o.InstrumentUid == FundUid && o.Lots == 10 && o.Price == 166.57m && o.PriceUnit == OrderPriceUnits.Currency);
        Guid.TryParse(posted.ClientOrderId, out _).Should().BeTrue();
        _plans[plan.Id].Status.Should().Be(PlanStatuses.Submitting);
        _notifier.Messages.Should().Contain(m => m.Keyboard != null && m.Keyboard.Rows[0][0].Text == "Отменить");

        _gateway.Fill(posted.ClientOrderId, 10, 166.56m, 0m);
        await Engine().RunTickAsync(Noon.AddSeconds(20));

        _plans[plan.Id].Status.Should().Be(PlanStatuses.Done);
        _orders.All.Single().Should().Match<TradeOrder>(o => o.Status == TradeOrderStatuses.Filled && o.LotsFilled == 10 && o.AvgFillPrice == 166.56m);
        _notifier.Texts.Should().Contain(t => t.Contains("исполнено 10 лот")).And.NotContain(t => t.Contains("отличается от оценки"));
        _sync.Count.Should().Be(1);
        _gateway.PostCount.Should().Be(1);
    }

    [Fact]
    public async Task Restart_WithUnconfirmedOrder_ReconcilesByClientId_NeverResubmits()
    {
        var plan = AddPlan(PlanStatuses.Submitting);
        Book(FundUid, 166.50m, 166.60m, Noon);
        // «Падение» после записи заявки, до ответа брокера: строка New, брокер заявку принял.
        var clientId = Guid.NewGuid().ToString("D");
        await _orders.InsertAsync(new TradeOrder
        {
            PlanId = plan.Id, LineNo = 1, Attempt = 1, ClientOrderId = clientId, Status = TradeOrderStatuses.New, LimitPrice = 166.57m, PriceUnit = OrderPriceUnits.Currency,
            LotsRequested = 10, Currency = "RUB", CreatedAt = Noon.AddSeconds(-30), UpdatedAt = Noon.AddSeconds(-30),
        });
        _gateway.States[clientId] = new GatewayOrderState("BRK-1", clientId, TradeOrderStatuses.Submitted, 10, 0, null, null, "RUB", null);

        await Engine().RunTickAsync(Noon);

        _gateway.PostCount.Should().Be(0, "заявка с тем же client_order_id уже у брокера — повторной отправки нет");
        _orders.All.Single().Should().Match<TradeOrder>(o => o.Status == TradeOrderStatuses.Submitted && o.BrokerOrderId == "BRK-1");
    }

    [Fact]
    public async Task Restart_UnknownToBroker_WaitsThenFails_WithoutResubmitting()
    {
        var plan = AddPlan(PlanStatuses.Submitting);
        Book(FundUid, 166.50m, 166.60m, Noon);
        await _orders.InsertAsync(new TradeOrder
        {
            PlanId = plan.Id, LineNo = 1, Attempt = 1, ClientOrderId = Guid.NewGuid().ToString("D"), Status = TradeOrderStatuses.New, LimitPrice = 166.57m,
            PriceUnit = OrderPriceUnits.Currency, LotsRequested = 10, Currency = "RUB", CreatedAt = Noon.AddMinutes(-1), UpdatedAt = Noon.AddMinutes(-1),
        });

        await Engine().RunTickAsync(Noon);
        _orders.All.Single().Status.Should().Be(TradeOrderStatuses.New, "до таймаута — только ждём сверки");

        await Engine().RunTickAsync(Noon.AddMinutes(5));
        _orders.All.Single().Should().Match<TradeOrder>(o => o.Status == TradeOrderStatuses.Failed && o.ErrorCode == TradeOrderErrorCodes.UnknownState);
        _plans[plan.Id].Status.Should().Be(PlanStatuses.Failed);
        _gateway.PostCount.Should().Be(0);
    }

    [Fact]
    public async Task PostOutcomeUnknown_LeavesOrderNew_NextTickReconcilesInsteadOfPosting()
    {
        AddPlan();
        Book(FundUid, 166.50m, 166.60m, Noon);
        var calls = 0;
        _gateway.OnPost = o =>
        {
            calls++;
            throw new TInvestGatewayException(GatewayErrorKind.Transient, "DeadlineExceeded", "timeout");
        };

        await Engine().RunTickAsync(Noon);
        _orders.All.Single().Status.Should().Be(TradeOrderStatuses.New);

        await Engine().RunTickAsync(Noon.AddSeconds(20));
        await Engine().RunTickAsync(Noon.AddSeconds(40));

        calls.Should().Be(1, "исход неизвестен — только сверка, повторной отправки нет");
        _orders.All.Should().ContainSingle();
    }

    [Fact]
    public async Task BrokerRejects_LineRejected_PlanFailed_Notified()
    {
        var plan = AddPlan();
        Book(FundUid, 166.50m, 166.60m, Noon);
        _gateway.OnPost = _ => throw new TInvestGatewayException(GatewayErrorKind.Rejected, "InvalidArgument", "T-Invest PostOrder: InvalidArgument 30079");

        await Engine().RunTickAsync(Noon);

        _orders.All.Single().Should().Match<TradeOrder>(o => o.Status == TradeOrderStatuses.Rejected && o.ErrorCode == TradeOrderErrorCodes.BrokerRejected);
        _plans[plan.Id].Status.Should().Be(PlanStatuses.Failed);
        _notifier.Texts.Should().Contain(t => t.Contains("не исполнено"));
    }

    [Fact]
    public async Task PreflightFailure_StaleQuote_NoPost_LineFailed()
    {
        var plan = AddPlan();
        _gateway.Books[FundUid] = new GatewayOrderBook(166.50m, 166.60m, Noon.AddMinutes(-10));

        await Engine().RunTickAsync(Noon);

        _gateway.PostCount.Should().Be(0);
        _orders.All.Single().Should().Match<TradeOrder>(o => o.Status == TradeOrderStatuses.Failed && o.ErrorCode == PreflightChecks.StaleQuote);
        _plans[plan.Id].Status.Should().Be(PlanStatuses.Failed);
    }

    [Fact]
    public async Task PreflightFailure_NotEnoughMoney_NoPost()
    {
        AddPlan();
        Book(FundUid, 166.50m, 166.60m, Noon);
        _gateway.Money["RUB"] = 100m;
        await Engine().RunTickAsync(Noon);
        _gateway.PostCount.Should().Be(0);
        _orders.All.Single().ErrorCode.Should().Be(PreflightChecks.InsufficientFunds);
    }

    /// <summary>Исполненный сегодня авто-план: оценка 70 × 300 = 21 000 ₽, факт — 70 × <paramref name="avgFill"/>.</summary>
    private void AddExecutedAutoPlanToday(decimal avgFill)
    {
        var done = AddPlan(PlanStatuses.Done, DecidedVia.Auto, FundBuy(price: 300m, lots: 70));
        done.DecidedAt = Noon.AddHours(-1);
        _orders.All.Add(new TradeOrder
        {
            Id = 900, PlanId = done.Id, LineNo = 1, Attempt = 1, ClientOrderId = Guid.NewGuid().ToString("D"), Status = TradeOrderStatuses.Filled, LimitPrice = 300m,
            PriceUnit = OrderPriceUnits.Currency, LotsRequested = 70, LotsFilled = 70, AvgFillPrice = avgFill, Currency = "RUB", CreatedAt = Noon.AddHours(-1), UpdatedAt = Noon.AddHours(-1),
        });
    }

    [Fact]
    public async Task AutoApproved_DayLimit_CountsActualFilledAmounts_NotEstimates()
    {
        // По оценке другой план — 21 000 (+ этот 6 000 = 27 000 ≤ 50 000), но по ФАКТУ исполнено 70 × 642.86 ≈ 45 000 → 45 000 + 6 000 > 50 000 → отказ.
        AddExecutedAutoPlanToday(avgFill: 642.86m);
        AddPlan(PlanStatuses.AutoApproved, DecidedVia.Auto, FundBuy(price: 600m));
        Book(FundUid, 599.9m, 600.1m, Noon);

        await Engine().RunTickAsync(Noon);

        _gateway.PostCount.Should().Be(0);
        _orders.All.Single(o => o.Id != 900).ErrorCode.Should().Be(PreflightChecks.AutoDayLimit);
    }

    [Fact]
    public async Task AutoApproved_DayLimit_ActualBelowLimit_Posts()
    {
        AddExecutedAutoPlanToday(avgFill: 300m); // факт 21 000 + 6 000 ≤ 50 000
        AddPlan(PlanStatuses.AutoApproved, DecidedVia.Auto, FundBuy(price: 600m));
        Book(FundUid, 599.9m, 600.1m, Noon);

        await Engine().RunTickAsync(Noon);

        _gateway.PostCount.Should().Be(1);
    }

    [Fact]
    public async Task AutoApproved_PerOrderCap_RecheckedAtExecution_WithCurrentLimits()
    {
        // При построении лимит был выше; сейчас 25 000 → строка 10 × 3 000 = 30 000 больше лимита, план не стартует.
        AddPlan(PlanStatuses.AutoApproved, DecidedVia.Auto, FundBuy(price: 3_000m));
        Book(FundUid, 2_999.9m, 3_000.1m, Noon);

        await Engine().RunTickAsync(Noon);

        _gateway.PostCount.Should().Be(0);
        _plans[_plans.LastId].Status.Should().Be(PlanStatuses.Failed);
        _notifier.Texts.Should().Contain(t => t.Contains("больше лимита авто"));
    }

    [Fact]
    public async Task AutoApproved_PerOrderCap_AtOrderPrice_Preflight()
    {
        // Оценка 24 950 ≤ 25 000 проходит при старте, но цена заявки выше утверждённой (перевыставление) → сумма заявки > 25 000 → не отправляется.
        _limits.AutoPerOrderRub = 24_960m;
        AddPlan(PlanStatuses.AutoApproved, DecidedVia.Auto, FundBuy(price: 2_495m));
        Book(FundUid, 2_495.5m, 2_496.5m, Noon); // mid 2 496 — дрейф 0.04%; первая попытка по лимиту 2 495 → 24 950 ≤ 24 960
        await Engine().RunTickAsync(Noon);
        _gateway.PostCount.Should().Be(1);

        _limits.AutoPerOrderRub = 24_955m; // лимит снизили — следующая попытка (цена ближе к ask) уже больше лимита
        Book(FundUid, 2_495.5m, 2_496.5m, Noon.AddMinutes(5));
        await Engine().RunTickAsync(Noon.AddMinutes(5));

        _gateway.PostCount.Should().Be(1);
        _orders.All.OrderBy(o => o.Attempt).Last().ErrorCode.Should().Be(PreflightChecks.AutoOrderLimit);
    }

    [Fact]
    public async Task AutoApproved_NotInAutoBuyBasket_NotExecuted()
    {
        _baskets[0].AutoBuy = false; // владелец выключил авто-покупку после построения плана
        var plan = AddPlan(PlanStatuses.AutoApproved, DecidedVia.Auto, FundBuy());
        Book(FundUid, 166.50m, 166.60m, Noon);

        await Engine().RunTickAsync(Noon);

        _gateway.PostCount.Should().Be(0);
        _plans[plan.Id].Status.Should().Be(PlanStatuses.Failed);
        _notifier.Texts.Should().Contain(t => t.Contains("не входит в корзину с авто-покупкой"));
    }

    [Theory]
    [InlineData(DecidedVia.Auto)]
    [InlineData(null)]
    public async Task T67_ForgedAutoApproved_InNonLiquidityBasket_RejectedByExecutor(string? via)
    {
        // Статус AutoApproved «подделан» в БД (с decided_via = auto или без него), корзина не «liquidity», хоть и с auto_buy: исполнитель проверяет белый список сам.
        _baskets[0].Code = "ru_equity";
        var plan = AddPlan(PlanStatuses.AutoApproved, via!, FundBuy());
        Book(FundUid, 166.50m, 166.60m, Noon);

        await Engine().RunTickAsync(Noon);

        _gateway.PostCount.Should().Be(0);
        _plans[plan.Id].Status.Should().Be(PlanStatuses.Failed);
        _notifier.Texts.Should().Contain(t => t.Contains("авто-исполнение не разрешено"));
    }

    [Fact]
    public async Task T67_AutoApproved_FundPayingDistributions_RejectedByExecutor()
    {
        _meta[0].PaysDistributions = true;
        var plan = AddPlan(PlanStatuses.AutoApproved, DecidedVia.Auto, FundBuy());
        Book(FundUid, 166.50m, 166.60m, Noon);

        await Engine().RunTickAsync(Noon);

        _gateway.PostCount.Should().Be(0);
        _plans[plan.Id].Status.Should().Be(PlanStatuses.Failed);
        _notifier.Texts.Should().Contain(t => t.Contains("выплат"));
    }

    [Fact]
    public async Task AutoApproved_InWhitelist_Executed()
    {
        AddPlan(PlanStatuses.AutoApproved, DecidedVia.Auto, FundBuy());
        Book(FundUid, 166.50m, 166.60m, Noon);
        await Engine().RunTickAsync(Noon);
        _gateway.PostCount.Should().Be(1);
    }

    [Fact]
    public async Task PausedBetweenTicks_ActiveOrderCancelled_NothingNewPosted()
    {
        var plan = AddPlan(lines: [FundBuy(1), FundBuy(2)]);
        Book(FundUid, 166.50m, 166.60m, Noon);
        await Engine().RunTickAsync(Noon);
        _gateway.PostCount.Should().Be(1);

        _gate.Result = ExecutionGate.Evaluate(true, ExecutionModes.Live, true, paused: true);
        await Engine().RunTickAsync(Noon.AddSeconds(20));

        _gateway.PostCount.Should().Be(1);
        _gateway.Cancelled.Should().ContainSingle();
        _plans[plan.Id].Status.Should().Be(PlanStatuses.Failed);
        _orders.All.Where(o => o.LineNo == 2).Should().ContainSingle(o => o.Status == TradeOrderStatuses.Failed && o.ErrorCode == TradeOrderErrorCodes.Halted);
    }

    // ─── Сопровождение ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Reprice_AfterTimeout_WithinDrift_UpToMaxAttempts_ThenExpired()
    {
        var plan = AddPlan();
        // Дрейф фондов по умолчанию 0.2% (дизайн §5): ask далеко (167.20) — цель ограничена 166.57 × 1.002 → 166.90; mid 166.85 в пределах 0.2%.
        Book(FundUid, 166.50m, 167.20m, Noon);
        var engine = Engine();

        await engine.RunTickAsync(Noon);
        await Engine().RunTickAsync(Noon.AddMinutes(4)); // ещё рано
        _gateway.PostCount.Should().Be(1);

        Book(FundUid, 166.50m, 167.20m, Noon.AddMinutes(5));
        await Engine().RunTickAsync(Noon.AddMinutes(5));
        Book(FundUid, 166.50m, 167.20m, Noon.AddMinutes(10));
        await Engine().RunTickAsync(Noon.AddMinutes(10));
        Book(FundUid, 166.50m, 167.20m, Noon.AddMinutes(15));
        await Engine().RunTickAsync(Noon.AddMinutes(15));

        var prices = _gateway.Posted.Select(p => p.Order.Price).ToList();
        prices.Should().HaveCount(3, "MaxAttempts = 3");
        prices.Should().BeInAscendingOrder();
        prices.Last().Should().Be(166.90m);
        prices.Should().OnlyContain(p => OrderPricing.DriftFraction(p, 166.57m) <= 0.002m);
        _gateway.Cancelled.Should().HaveCount(3);
        _orders.All.OrderBy(o => o.Attempt).Select(o => o.Status).Should().Equal(TradeOrderStatuses.Cancelled, TradeOrderStatuses.Cancelled, TradeOrderStatuses.Expired);
        _plans[plan.Id].Status.Should().Be(PlanStatuses.Failed);
    }

    [Fact]
    public async Task PartialFill_RepriceSendsOnlyRemainingLots_PlanDone()
    {
        var plan = AddPlan();
        Book(FundUid, 166.50m, 166.60m, Noon);
        await Engine().RunTickAsync(Noon);
        var first = _gateway.Posted[0].Order.ClientOrderId;
        _gateway.Fill(first, 4, 166.57m, 0m, full: false);

        Book(FundUid, 166.50m, 166.60m, Noon.AddMinutes(5));
        await Engine().RunTickAsync(Noon.AddMinutes(5));

        _gateway.Posted.Should().HaveCount(2);
        _gateway.Posted[1].Order.Lots.Should().Be(6);
        _gateway.Fill(_gateway.Posted[1].Order.ClientOrderId, 6, 166.58m, 0m);
        await Engine().RunTickAsync(Noon.AddMinutes(5).AddSeconds(20));

        _plans[plan.Id].Status.Should().Be(PlanStatuses.Done);
    }

    [Fact]
    public async Task SellsBeforeBuys_BuyWaitsForSell()
    {
        var plan = AddPlan(lines: [FundBuy(1), BondSell(2)]);
        Book(FundUid, 166.50m, 166.60m, Noon);
        Book(BondUid, 92.65m, 92.75m, Noon);
        _gateway.Securities[BondUid] = 5m;

        await Engine().RunTickAsync(Noon);

        _gateway.Posted.Should().ContainSingle();
        var sell = _gateway.Posted[0].Order;
        sell.Should().Match<GatewayLimitOrder>(o => o.Side == PlanSides.Sell && o.InstrumentUid == BondUid && o.Price == 92.70m && o.PriceUnit == OrderPriceUnits.Points);

        await Engine().RunTickAsync(Noon.AddSeconds(20));
        _gateway.Posted.Should().ContainSingle("покупка ждёт завершения продажи");

        _gateway.Fill(sell.ClientOrderId, 2, 927.0m, 0.74m);
        Book(FundUid, 166.50m, 166.60m, Noon.AddSeconds(40));
        await Engine().RunTickAsync(Noon.AddSeconds(40));

        _gateway.Posted.Should().HaveCount(2);
        _gateway.Posted[1].Order.Side.Should().Be(PlanSides.Buy);
        _plans[plan.Id].Status.Should().Be(PlanStatuses.Submitting);
    }

    [Fact]
    public async Task BeforeSessionClose_ActiveOrderCancelled_NoNewOrders()
    {
        // 18:31 МСК = 15:31 UTC: до закрытия Мосбиржи (18:40) меньше 10 минут.
        var late = Noon.Date.AddHours(15).AddMinutes(31);
        var plan = AddPlan(lines: [FundBuy(1), FundBuy(2)]);
        plan.ExpiresAt = late.AddMinutes(9);
        Book(FundUid, 166.50m, 166.60m, Noon);
        await Engine().RunTickAsync(Noon);
        _gateway.PostCount.Should().Be(1);

        Book(FundUid, 166.50m, 166.60m, late);
        await Engine().RunTickAsync(late);

        _gateway.Cancelled.Should().ContainSingle();
        _gateway.PostCount.Should().Be(1, "в окне закрытия новые заявки не отправляются");
        _orders.All.Where(o => o.LineNo == 1).Should().ContainSingle(o => o.Status == TradeOrderStatuses.Cancelled && o.ErrorCode == TradeOrderErrorCodes.SessionClose);
        _orders.All.Where(o => o.LineNo == 2).Should().ContainSingle(o => o.ErrorCode == PreflightChecks.SessionClosing || o.ErrorCode == TradeOrderErrorCodes.SessionClose);
        _plans[plan.Id].Status.Should().Be(PlanStatuses.Failed);
    }

    [Fact]
    public async Task FeeMismatch_OverOneKopeck_Notified()
    {
        AddPlan(lines: FundBuy(fee: 0m));
        Book(FundUid, 166.50m, 166.60m, Noon);
        await Engine().RunTickAsync(Noon);
        _gateway.Fill(_gateway.Posted[0].Order.ClientOrderId, 10, 166.57m, 0.67m);

        await Engine().RunTickAsync(Noon.AddSeconds(20));

        _notifier.Texts.Should().Contain(t => t.Contains("отличается от оценки"));
    }

    [Fact]
    public async Task CancelRequested_ActiveOrderCancelled_PlanClosed()
    {
        var plan = AddPlan();
        Book(FundUid, 166.50m, 166.60m, Noon);
        await Engine().RunTickAsync(Noon);

        (await _plans.RequestCancelAsync(UserId, plan.Id)).Should().BeTrue();
        await Engine().RunTickAsync(Noon.AddSeconds(20));

        _gateway.Cancelled.Should().ContainSingle();
        _orders.All.Single().Should().Match<TradeOrder>(o => o.Status == TradeOrderStatuses.Cancelled && o.ErrorCode == TradeOrderErrorCodes.UserCancel);
        _plans[plan.Id].Status.Should().Be(PlanStatuses.Failed);
    }

    // ─── Снять нельзя (M1): одно уведомление, план остаётся Submitting, попытки каждый тик ──

    private async Task<TradePlan> PlanWithLiveOrderAsync()
    {
        var plan = AddPlan();
        Book(FundUid, 166.50m, 166.60m, Noon);
        await Engine().RunTickAsync(Noon);
        _gateway.PostCount.Should().Be(1);
        _notifier.Messages.Clear();
        return plan;
    }

    [Fact]
    public async Task Halt_TokenDeleted_CancelImpossible_AlertsOnce_KeepsSubmitting_RetriesEveryTick()
    {
        var plan = await PlanWithLiveOrderAsync();
        _gate.Result = ExecutionGate.Evaluate(true, ExecutionModes.Live, tradeCredentialOk: false, paused: false);
        _gateway.CancelError = new TInvestGatewayException(GatewayErrorKind.Rejected, "no_trade_token", "нет токена");
        _gateway.StateError = new TInvestGatewayException(GatewayErrorKind.Rejected, "no_trade_token", "нет токена");

        await Engine().RunTickAsync(Noon.AddSeconds(20));
        await Engine().RunTickAsync(Noon.AddSeconds(40));

        _notifier.Texts.Where(t => t.Contains("не снята автоматически")).Should().ContainSingle()
            .Which.Should().Contain("TMON@").And.Contain("×10").And.Contain("снимите её в терминале брокера");
        _gateway.Cancelled.Should().HaveCount(2, "сверка и попытка снять — каждый тик");
        _plans[plan.Id].Status.Should().Be(PlanStatuses.Submitting);
        _orders.All.Single().Should().Match<TradeOrder>(o => o.Status == TradeOrderStatuses.Submitted && o.HaltAlertedAt != null);
        _gateway.PostCount.Should().Be(1);
    }

    [Fact]
    public async Task Halt_TokenOnForeignAccount_CancelImpossible_AlertsOnce()
    {
        var plan = await PlanWithLiveOrderAsync();
        _gateway.Accounts.Clear();
        _gateway.Accounts.Add(new TInvestTradeAccount("ACC-OTHER", true, true));
        _gateway.CancelError = new TInvestGatewayException(GatewayErrorKind.Rejected, "PermissionDenied", "нет доступа к счёту");
        _gateway.StateError = new TInvestGatewayException(GatewayErrorKind.Rejected, "PermissionDenied", "нет доступа к счёту");

        await Engine().RunTickAsync(Noon.AddSeconds(20));
        await Engine().RunTickAsync(Noon.AddSeconds(40));

        _notifier.Texts.Count(t => t.Contains("не снята автоматически")).Should().Be(1);
        _plans[plan.Id].Status.Should().Be(PlanStatuses.Submitting);
    }

    [Fact]
    public async Task Halt_BrokerRefusesCancel_OrderStillActive_AlertsOnce_ThenRecoversWhenCancelled()
    {
        var plan = await PlanWithLiveOrderAsync();
        _gate.Result = ExecutionGate.Evaluate(true, ExecutionModes.Live, true, paused: true);
        _gateway.CancelError = new TInvestGatewayException(GatewayErrorKind.Rejected, "FailedPrecondition", "нельзя снять");

        await Engine().RunTickAsync(Noon.AddSeconds(20));
        await Engine().RunTickAsync(Noon.AddSeconds(40));
        _notifier.Texts.Count(t => t.Contains("не снята автоматически")).Should().Be(1);
        _plans[plan.Id].Status.Should().Be(PlanStatuses.Submitting);

        // Пользователь снял заявку в терминале — следующий тик видит Cancelled и закрывает план.
        var id = _gateway.Posted[0].Order.ClientOrderId;
        _gateway.States[id] = _gateway.States[id] with { Status = TradeOrderStatuses.Cancelled };
        await Engine().RunTickAsync(Noon.AddMinutes(1));

        _plans[plan.Id].Status.Should().Be(PlanStatuses.Failed);
        _notifier.Texts.Count(t => t.Contains("не снята автоматически")).Should().Be(1);
    }

    // ─── Вне часов торгов (MINOR 2) ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ApprovedLivePlan_OutsideTradingHours_WaitsApproved_StartsWhenSessionOpens()
    {
        var evening = Noon.Date.AddHours(17); // 20:00 МСК — Мосбиржа закрыта
        var nextMorning = Noon.Date.AddDays(Noon.DayOfWeek == DayOfWeek.Friday ? 3 : 1).AddHours(7).AddMinutes(5); // 10:05 МСК следующего буднего дня
        var plan = AddPlan();
        plan.ExpiresAt = nextMorning.AddHours(8);

        await Engine().RunTickAsync(evening);
        _plans[plan.Id].Status.Should().Be(PlanStatuses.Approved, "ждёт открытия сессии, а не падает «рынок закрыт»");
        _gateway.PostCount.Should().Be(0);
        _orders.All.Should().BeEmpty();

        Book(FundUid, 166.50m, 166.60m, nextMorning);
        await Engine().RunTickAsync(nextMorning);
        _plans[plan.Id].Status.Should().Be(PlanStatuses.Submitting);
        _gateway.PostCount.Should().Be(1);
    }

    [Fact]
    public async Task ApprovedLivePlan_OutsideTradingHours_ExpiresWhenDeadlinePasses()
    {
        var evening = Noon.Date.AddHours(17);
        var plan = AddPlan();
        plan.ExpiresAt = evening.AddHours(1);

        await Engine().RunTickAsync(evening);
        await Engine().RunTickAsync(evening.AddHours(2));

        _plans[plan.Id].Status.Should().Be(PlanStatuses.Expired);
        _gateway.PostCount.Should().Be(0);
    }

    // ─── Сверка комиссии с задержкой (MINOR 7) ──────────────────────────────────────────────

    [Fact]
    public async Task FeeZeroRightAfterFill_Deferred_ThenRealFeeArrives_NoFalseAlert()
    {
        var plan = AddPlan(lines: FundBuy(fee: 0.50m));
        Book(FundUid, 166.50m, 166.60m, Noon);
        await Engine().RunTickAsync(Noon);
        var id = _gateway.Posted[0].Order.ClientOrderId;
        _gateway.Fill(id, 10, 166.57m, 0m);

        await Engine().RunTickAsync(Noon.AddSeconds(20));
        _notifier.Texts.Should().NotContain(t => t.Contains("отличается от оценки")).And.NotContain(t => t.Contains("исполнено 10 лот"));
        _plans[plan.Id].Status.Should().Be(PlanStatuses.Submitting, "план закрывается после сверки комиссии");

        _gateway.Fill(id, 10, 166.57m, 0.50m); // комиссия пришла
        await Engine().RunTickAsync(Noon.AddSeconds(40));

        _notifier.Texts.Should().ContainSingle(t => t.Contains("исполнено 10 лот")).And.NotContain(t => t.Contains("отличается от оценки"));
        _plans[plan.Id].Status.Should().Be(PlanStatuses.Done);
    }

    [Fact]
    public async Task FeeStaysZero_AfterMaxRechecks_AlertsOnce_PlanDone()
    {
        var plan = AddPlan(lines: FundBuy(fee: 0.50m));
        Book(FundUid, 166.50m, 166.60m, Noon);
        await Engine().RunTickAsync(Noon);
        _gateway.Fill(_gateway.Posted[0].Order.ClientOrderId, 10, 166.57m, 0m);

        for (var i = 1; i <= _options.MaxFeeRechecks + 1; i++) await Engine().RunTickAsync(Noon.AddSeconds(20 * i));

        _notifier.Texts.Count(t => t.Contains("отличается от оценки")).Should().Be(1);
        _plans[plan.Id].Status.Should().Be(PlanStatuses.Done);
        _orders.All.Single().FeeRechecks.Should().Be(_options.MaxFeeRechecks);
    }

    [Fact]
    public async Task AvgFillPrice_FromOrderStateTotal_DividedByPieces()
    {
        AddPlan();
        Book(FundUid, 166.50m, 166.60m, Noon);
        await Engine().RunTickAsync(Noon);
        var id = _gateway.Posted[0].Order.ClientOrderId;
        // GetOrderState: executed_order_price — ИТОГО (1 665.60 за 10 шт.), а цена штуки брокером не дана.
        _gateway.States[id] = _gateway.States[id] with { Status = TradeOrderStatuses.Filled, LotsExecuted = 10, AvgFillPrice = null, ExecutedAmount = 1_665.60m, ExecutedCommission = 0m };

        await Engine().RunTickAsync(Noon.AddSeconds(20));

        _orders.All.Single().AvgFillPrice.Should().Be(166.56m);
    }

    // ─── Цена не на сетке Quotation (NIT) ───────────────────────────────────────────────────

    [Fact]
    public async Task PriceGridError_BeforePost_OrderFailed_NotUnknown()
    {
        var plan = AddPlan();
        Book(FundUid, 166.50m, 166.60m, Noon);
        _gateway.OnPost = _ => throw new TInvestGatewayException(GatewayErrorKind.Rejected, TradeOrderErrorCodes.PriceGrid, "Цена заявки не на сетке шага");

        await Engine().RunTickAsync(Noon);

        _orders.All.Single().Should().Match<TradeOrder>(o => o.Status == TradeOrderStatuses.Failed && o.ErrorCode == TradeOrderErrorCodes.PriceGrid);
        _plans[plan.Id].Status.Should().Be(PlanStatuses.Failed);
    }

    // ─── Двойники ──────────────────────────────────────────────────────────────────────────

    private sealed class FakeGate : IExecutionGateResolver
    {
        public ExecutionGateResult Result { get; set; } = ExecutionGate.Evaluate(true, ExecutionModes.Live, true, false);
        public Task<ExecutionGateResult> ResolveAsync(ulong userId) => Task.FromResult(Result);
    }

    private sealed class RecordingNotifier : IExecutionNotifier
    {
        public List<BotMessage> Messages { get; } = [];
        public IEnumerable<string> Texts => Messages.Select(m => m.Html);

        public Task NotifyAsync(ulong userId, BotMessage message, CancellationToken ct = default)
        {
            Messages.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class CountingSync : IPortfolioSyncTrigger
    {
        public int Count { get; private set; }
        public void Request() => Count++;
    }
}

/// <summary>In-memory <see cref="ITradeOrderRepository"/> с проверкой UNIQUE client_order_id / (plan, line, attempt), как в MySQL.</summary>
public sealed class InMemoryOrders : ITradeOrderRepository
{
    private ulong _next = 1;
    public List<TradeOrder> All { get; } = [];

    public Task<ulong> InsertAsync(TradeOrder order)
    {
        if (All.Any(o => o.ClientOrderId == order.ClientOrderId || (o.PlanId == order.PlanId && o.LineNo == order.LineNo && o.Attempt == order.Attempt)))
            throw new InvalidOperationException("duplicate key");
        order.Id = _next++;
        All.Add(order);
        return Task.FromResult(order.Id);
    }

    public Task UpdateAsync(TradeOrder order) => Task.CompletedTask; // объекты общие — изменения уже видны

    public Task<IReadOnlyList<TradeOrder>> ListByPlanAsync(ulong planId) =>
        Task.FromResult<IReadOnlyList<TradeOrder>>(All.Where(o => o.PlanId == planId).OrderBy(o => o.LineNo).ThenBy(o => o.Attempt).ToList());

    public Task<IReadOnlyList<TradeOrder>> ListByPlansAsync(IReadOnlyCollection<ulong> planIds) =>
        Task.FromResult<IReadOnlyList<TradeOrder>>(All.Where(o => planIds.Contains(o.PlanId)).ToList());

    /// <summary>Один пользователь в тестах — все активные.</summary>
    public Task<IReadOnlyList<TradeOrder>> ListActiveByUserAsync(ulong userId) =>
        Task.FromResult<IReadOnlyList<TradeOrder>>(All.Where(o => TradeOrderStatuses.IsActive(o.Status)).ToList());

    public Task<IReadOnlyList<TradeOrder>> ListActiveByUserAsync(ulong userId, string broker) =>
        Task.FromResult<IReadOnlyList<TradeOrder>>(All.Where(o => TradeOrderStatuses.IsActive(o.Status) && o.Broker == broker).ToList());

    public Task<bool> IsBrokerOrderLinkedAsync(string broker, string brokerOrderId, ulong exceptOrderId) =>
        Task.FromResult(All.Any(o => o.Broker == broker && o.BrokerOrderId == brokerOrderId && o.Id != exceptOrderId));
}

/// <summary>In-memory <see cref="ITradePlanRepository"/> — только методы, нужные исполнителю.</summary>
public sealed class InMemoryPlans : ITradePlanRepository
{
    private ulong _next = 100;
    private readonly Dictionary<ulong, TradePlan> _plans = new();

    public decimal AutoUsedToday { get; set; }

    public TradePlan this[ulong id] => _plans[id];

    public ulong LastId => _next - 1;

    public void Add(TradePlan plan)
    {
        plan.Id = _next++;
        foreach (var l in plan.Lines) l.PlanId = plan.Id;
        _plans[plan.Id] = plan;
    }

    private static bool Executable(TradePlan p) =>
        p.Status == PlanStatuses.Submitting || (p.Status is PlanStatuses.Approved or PlanStatuses.AutoApproved && !p.DryRun);

    private static TradePlan Copy(TradePlan p) => new()
    {
        Id = p.Id, UserId = p.UserId, Rule = p.Rule, Trigger = p.Trigger, Status = p.Status, DryRun = p.DryRun, CreatedAt = p.CreatedAt, ExpiresAt = p.ExpiresAt,
        DecidedAt = p.DecidedAt, DecidedVia = p.DecidedVia, BotNonce = p.BotNonce, CancelRequested = p.CancelRequested, SummaryJson = p.SummaryJson, Lines = p.Lines,
    };

    public Task<IReadOnlyList<ulong>> ListUserIdsWithExecutablePlansAsync() =>
        Task.FromResult<IReadOnlyList<ulong>>(_plans.Values.Where(Executable).Select(p => p.UserId).Distinct().ToList());

    public Task<IReadOnlyList<TradePlan>> ListExecutableAsync(ulong userId) =>
        Task.FromResult<IReadOnlyList<TradePlan>>(_plans.Values.Where(p => p.UserId == userId && Executable(p)).OrderBy(p => p.Id).Select(Copy).ToList());

    public Task<bool> SetExecutionStatusAsync(ulong userId, ulong id, IReadOnlyCollection<string> fromStatuses, string toStatus, string? summaryJson)
    {
        if (!_plans.TryGetValue(id, out var p) || p.UserId != userId || !fromStatuses.Contains(p.Status)) return Task.FromResult(false);
        p.Status = toStatus;
        if (summaryJson is not null) p.SummaryJson = summaryJson;
        return Task.FromResult(true);
    }

    public Task<bool> RequestCancelAsync(ulong userId, ulong id)
    {
        if (!_plans.TryGetValue(id, out var p) || p.UserId != userId || !Executable(p)) return Task.FromResult(false);
        p.CancelRequested = true;
        return Task.FromResult(true);
    }

    public Task<decimal> SumAutoApprovedRubSinceAsync(ulong userId, DateTime sinceUtc, ulong? excludePlanId = null) => Task.FromResult(AutoUsedToday);

    public Task<IReadOnlyList<TradePlan>> ListAutoDayPlansAsync(ulong userId, DateTime sinceUtc) =>
        Task.FromResult<IReadOnlyList<TradePlan>>(_plans.Values
            .Where(p => p.UserId == userId && p.DecidedAt >= sinceUtc
                        && (p.Status is PlanStatuses.Submitting or PlanStatuses.PartiallyDone or PlanStatuses.Done or PlanStatuses.Failed
                            || (p.Status == PlanStatuses.AutoApproved && !p.DryRun)))
            .OrderBy(p => p.Id).Select(Copy).ToList());

    public Task<bool> ExtendExpiryAsync(ulong userId, ulong id, DateTime expiresAtUtc)
    {
        if (!_plans.TryGetValue(id, out var p) || p.UserId != userId || p.Status != PlanStatuses.Approved) return Task.FromResult(false);
        if (expiresAtUtc > p.ExpiresAt) p.ExpiresAt = expiresAtUtc;
        return Task.FromResult(true);
    }

    public Task<TradePlan?> GetAsync(ulong userId, ulong id) => Task.FromResult(_plans.TryGetValue(id, out var p) && p.UserId == userId ? Copy(p) : null);

    public Task<ulong> InsertAsync(TradePlan plan) => throw new NotSupportedException();
    public Task<IReadOnlyList<TradePlan>> ListAsync(ulong userId, string? status, int limit) => throw new NotSupportedException();
    public Task<bool> ExistsAsync(ulong userId, string rule, string trigger, bool openOnly, DateTime nowUtc, DateTime? createdSince = null) => throw new NotSupportedException();
    public Task<bool> HasOpenRuleAsync(ulong userId, string rule, DateTime nowUtc) => throw new NotSupportedException();
    public Task<decimal> SumReservedBuysAsync(ulong userId, string broker, string currency, DateTime nowUtc, int withdrawHoldTtl = 3) => throw new NotSupportedException();
    public Task<CashHoldState> GetCashHoldAsync(ulong userId, string broker, string currency, DateTime nowUtc, int withdrawHoldTtl) => throw new NotSupportedException();
    public Task<int> ReleaseCashHoldsAsync(ulong userId, DateTime nowUtc) => throw new NotSupportedException();
    public Task<bool> RotateNonceAsync(ulong userId, ulong id, string expectedNonce, string newNonce) => throw new NotSupportedException();
    public Task<bool> TransitionAsync(ulong userId, ulong id, IReadOnlyCollection<string> fromStatuses, string toStatus, string? decidedVia, DateTime decidedAtUtc, string? summaryJson) =>
        throw new NotSupportedException();
    public Task<int> ExpirePendingAsync(DateTime nowUtc) => throw new NotSupportedException();
}

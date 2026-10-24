using Bonds.Core.Capital;
using Bonds.Core.Credentials;
using Bonds.Core.Execution;
using Bonds.Core.Policy;
using Bonds.Infrastructure.Execution;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tinkoff.InvestApi.V1;
using Xunit;
using BrokerIds = Bonds.Core.Models.Brokers;

namespace Bonds.Tests.Execution;

/// <summary>
/// Доработки задачи 65 по ревью: кэш gRPC-клиентов (M2), семантика цены исполнения T-Invest (MINOR 7), цена вне сетки до отправки (NIT), суточный лимит по факту и
/// белый список авто (MINOR 6), ближайшая сессия для продления срока (MINOR 2), дрейф по дизайну (MINOR 5).
/// </summary>
public class ExecutionReviewFixesTests
{
    // ─── M2: кэш клиентов ──────────────────────────────────────────────────────────────────

    private static (TInvestOrderGateway Gateway, Mock<IUserCredentialStore> Store) Gateway(TInvestTradeClientCache cache, Func<string> token)
    {
        var store = new Mock<IUserCredentialStore>();
        store.Setup(s => s.GetSecretResultAsync(1, CredentialProviders.TInvestTrade))
            .ReturnsAsync(() => new CredentialSecretResult(CredentialReadOutcome.Ok, new Dictionary<string, string> { ["token"] = token() }));
        var gate = new Mock<IExecutionGateResolver>();
        gate.Setup(g => g.ResolveAsync(1)).ReturnsAsync(ExecutionGate.Evaluate(true, ExecutionModes.Live, true, false));
        return (new TInvestOrderGateway(store.Object, gate.Object, new ConfigurationBuilder().Build(), cache, NullLogger<TInvestOrderGateway>.Instance), store);
    }

    [Fact]
    public async Task ClientCache_TwoTicks_SameToken_ReuseOneClient_TokenChange_CreatesNew()
    {
        var cache = new TInvestTradeClientCache();
        var token = "t.first-token";

        // Два тика = два scope = два экземпляра шлюза (scoped) с общим singleton-кэшем.
        var tick1 = await Gateway(cache, () => token).Gateway.ClientForAsync(1);
        var (g2, store2) = Gateway(cache, () => token);
        var tick2 = await g2.ClientForAsync(1);
        (await g2.ClientForAsync(1)).Should().BeSameAs(tick2, "внутри scope токен читается один раз");
        store2.Verify(s => s.GetSecretResultAsync(1, CredentialProviders.TInvestTrade), Times.Once);

        tick2.Should().BeSameAs(tick1, "канал не создаётся на каждый тик");
        cache.Count.Should().Be(1);

        token = "t.second-token";
        var tick3 = await Gateway(cache, () => token).Gateway.ClientForAsync(1);
        tick3.Should().NotBeSameAs(tick1, "токен сменился — новый клиент");
        cache.Count.Should().Be(1, "старый клиент заменён, а не накоплен");
        (await Gateway(cache, () => token).Gateway.ClientForAsync(1)).Should().BeSameAs(tick3);
    }

    [Fact]
    public async Task ClientCache_TokenDeleted_Invalidated()
    {
        var cache = new TInvestTradeClientCache();
        await Gateway(cache, () => "t.token").Gateway.ClientForAsync(1);
        cache.Count.Should().Be(1);

        var store = new Mock<IUserCredentialStore>();
        store.Setup(s => s.GetSecretResultAsync(1, CredentialProviders.TInvestTrade)).ReturnsAsync(new CredentialSecretResult(CredentialReadOutcome.Missing, null));
        var gateway = new TInvestOrderGateway(store.Object, Mock.Of<IExecutionGateResolver>(), new ConfigurationBuilder().Build(), cache, NullLogger<TInvestOrderGateway>.Instance);

        await FluentActions.Awaiting(() => gateway.ClientForAsync(1)).Should().ThrowAsync<TInvestGatewayException>();
        cache.Count.Should().Be(0);
    }

    // ─── NIT: цена вне сетки Quotation — отказ ДО сети и без чтения токена ──────────────────

    [Fact]
    public async Task PostLimitOrder_PriceNotOnQuotationGrid_RejectedPriceGrid_WithoutTouchingToken()
    {
        var store = new Mock<IUserCredentialStore>(MockBehavior.Strict);
        var gate = new Mock<IExecutionGateResolver>();
        gate.Setup(g => g.ResolveAsync(1)).ReturnsAsync(ExecutionGate.Evaluate(true, ExecutionModes.Live, true, false));
        var gateway = new TInvestOrderGateway(store.Object, gate.Object, new ConfigurationBuilder().Build(), new TInvestTradeClientCache(), NullLogger<TInvestOrderGateway>.Instance);

        var act = () => gateway.PostLimitOrderAsync(1, new GatewayLimitOrder("ACC", "uid", PlanSides.Buy, 1, 100.0000000001m, OrderPriceUnits.Currency, Guid.NewGuid().ToString("D")));

        (await act.Should().ThrowAsync<TInvestGatewayException>()).Which.Should()
            .Match<TInvestGatewayException>(e => e.Kind == GatewayErrorKind.Rejected && e.Code == TradeOrderErrorCodes.PriceGrid);
        store.VerifyNoOtherCalls();
    }

    // ─── MINOR 7: семантика цены исполнения ─────────────────────────────────────────────────

    private static MoneyValue Rub(decimal v) => new() { Currency = "rub", Units = (long)decimal.Truncate(v), Nano = (int)((v - decimal.Truncate(v)) * 1_000_000_000m) };

    [Fact]
    public void OrderState_ExecutedOrderPrice_IsTotal_MappedToExecutedAmount_StagesGivePerPiece()
    {
        var state = new OrderState
        {
            OrderId = "BRK-1", OrderRequestId = "req", ExecutionReportStatus = OrderExecutionReportStatus.ExecutionReportStatusFill, LotsRequested = 10, LotsExecuted = 10,
            ExecutedOrderPrice = Rub(1_665.60m), InitialOrderPrice = Rub(1_665.70m), ExecutedCommission = Rub(0.5m),
        };
        state.Stages.Add(new OrderStage { Price = Rub(166.50m), Quantity = 4 });
        state.Stages.Add(new OrderStage { Price = Rub(166.60m), Quantity = 6 });

        var mapped = TInvestOrderGateway.Map(state);

        mapped.ExecutedAmount.Should().Be(1_665.60m, "OrderState.executed_order_price — произведение средней цены на количество (итого)");
        mapped.AvgFillPrice.Should().Be(166.56m, "запасная цена штуки — средневзвешенная по стадиям");
        mapped.ExecutedCommission.Should().Be(0.5m);
        FillPrice.PerPiece(mapped.ExecutedAmount, mapped.LotsExecuted, 1m, mapped.AvgFillPrice).Should().Be(166.56m);
    }

    [Fact]
    public void PostOrderResponse_ExecutedOrderPrice_IsPerInstrument()
    {
        var response = new PostOrderResponse
        {
            OrderId = "BRK-2", ExecutionReportStatus = OrderExecutionReportStatus.ExecutionReportStatusFill, LotsRequested = 2, LotsExecuted = 2,
            ExecutedOrderPrice = Rub(927.0m), InitialOrderPrice = Rub(1_854.0m),
        };

        var mapped = TInvestOrderGateway.MapPosted(response, "req");

        mapped.AvgFillPrice.Should().Be(927.0m);
        mapped.ExecutedAmount.Should().BeNull();
    }

    [Theory]
    [InlineData(1_665.60, 10, 1, null, 166.56)] // итог / штуки
    [InlineData(1_665.60, 1, 10, null, 166.56)] // лот 10 штук
    [InlineData(null, 10, 1, 166.57, 166.57)] // итога нет — цена штуки брокера
    [InlineData(0.0, 10, 1, 166.57, 166.57)]
    public void FillPrice_PerPiece(double? total, long lots, double lotSize, double? fallback, double expected) =>
        FillPrice.PerPiece((decimal?)total, lots, (decimal)lotSize, (decimal?)fallback).Should().Be((decimal)expected);

    [Fact]
    public void FillPrice_NoExecution_Null() => FillPrice.PerPiece(100m, 0, 1m, 100m).Should().BeNull();

    // ─── MINOR 6: суточный лимит по факту, белый список ─────────────────────────────────────

    private static TradePlanLine Buy(int no, long lots, decimal price, string key = "TMON", string currency = "RUB") => new()
    {
        LineNo = no, Broker = BrokerIds.TInvest, InstrumentKey = key, Ticker = key + "@", Side = PlanSides.Buy, QuantityLots = lots, LotSize = 1m, LimitPrice = price,
        EstAmount = price * lots, Currency = currency, Reason = "t",
    };

    private static TradeOrder Order(ulong planId, int line, int attempt, string status, long lots, long filled, decimal? avg, string? code = null) => new()
    {
        PlanId = planId, LineNo = line, Attempt = attempt, ClientOrderId = Guid.NewGuid().ToString("D"), Status = status, LotsRequested = lots, LotsFilled = filled,
        AvgFillPrice = avg, ErrorCode = code, Currency = "RUB",
    };

    [Fact]
    public void AutoDayUsage_FilledByFact_InFlightByEstimate_ExcludesPlan()
    {
        var done = new TradePlan { Id = 1, Status = PlanStatuses.Done, Lines = [Buy(1, 10, 100m)] }; // оценка 1 000, факт 10 × 110 = 1 100
        var partial = new TradePlan { Id = 2, Status = PlanStatuses.PartiallyDone, Lines = [Buy(1, 10, 100m)] }; // факт 4 × 100, остаток не считается (план завершён)
        var running = new TradePlan { Id = 3, Status = PlanStatuses.Submitting, Lines = [Buy(1, 10, 100m), Buy(2, 5, 200m)] }; // 3 × 105 + 7/10 × 1 000 + 1 000 (не начата)
        var waiting = new TradePlan { Id = 4, Status = PlanStatuses.AutoApproved, Lines = [Buy(1, 2, 500m)] }; // 1 000 — оценка
        var usd = new TradePlan { Id = 5, Status = PlanStatuses.Done, Lines = [Buy(1, 1, 100m, currency: "USD")] }; // не рубли
        var self = new TradePlan { Id = 6, Status = PlanStatuses.Submitting, Lines = [Buy(1, 1, 9_999m)] };
        var orders = new List<TradeOrder>
        {
            Order(1, 1, 1, TradeOrderStatuses.Filled, 10, 10, 110m),
            Order(2, 1, 1, TradeOrderStatuses.Expired, 10, 4, 100m, TradeOrderErrorCodes.Expired),
            Order(3, 1, 1, TradeOrderStatuses.PartiallyFilled, 10, 3, 105m),
            Order(5, 1, 1, TradeOrderStatuses.Filled, 1, 1, 100m),
        };

        var spent = AutoDayUsage.SpentRub([done, partial, running, waiting, usd, self], orders, excludePlanId: 6);

        spent.Should().Be(1_100m + 400m + (315m + 700m + 1_000m) + 1_000m);
    }

    private static List<CapitalBasket> Baskets(bool autoBuy, string member = "TMON") =>
    [
        new()
        {
            Code = "liquidity", AutoBuy = autoBuy,
            Members = [new CapitalBasketMember { Broker = BrokerIds.TInvest, InstrumentKey = member, MatchRule = BasketMatchRules.Instrument }],
        },
    ];

    [Fact]
    public void AutoWhitelist_AllowsOnlyAutoBuyBasketFundBuysUnderCap()
    {
        var ok = new TradePlan { Lines = [Buy(1, 10, 166m)] };
        AutoExecutionWhitelist.Check(ok, Baskets(true), 25_000m).Should().BeNull();
        AutoExecutionWhitelist.Check(ok, Baskets(false), 25_000m).Should().Contain("не входит в корзину");
        AutoExecutionWhitelist.Check(ok, Baskets(true, "TSAV"), 25_000m).Should().Contain("не входит в корзину");
        AutoExecutionWhitelist.Check(new TradePlan { Lines = [Buy(1, 200, 166m)] }, Baskets(true), 25_000m).Should().Contain("больше лимита авто");

        var sell = Buy(1, 1, 166m);
        sell.Side = PlanSides.Sell;
        AutoExecutionWhitelist.Check(new TradePlan { Lines = [sell] }, Baskets(true), 25_000m).Should().Contain("только покупки");

        var bond = Buy(1, 1, 927m);
        bond.LimitPricePoints = 92.7m;
        AutoExecutionWhitelist.Check(new TradePlan { Lines = [bond] }, Baskets(true), 25_000m).Should().Contain("облигации");
    }

    [Fact]
    public void Preflight_AutoPerOrderCap()
    {
        var input = new PreflightInput
        {
            Side = PlanSides.Buy, ApprovedLimit = 100m, OrderPrice = 100m, Lots = 1, LotSize = 1m, MaxDriftFraction = 0.002m, NowUtc = DateTime.UtcNow,
            MaxQuoteAge = TimeSpan.FromMinutes(2), Bid = 99.99m, Ask = 100.01m, QuoteTimeUtc = DateTime.UtcNow, NormalTrading = true, LimitOrderAvailable = true,
            ApiTradeAvailable = true, SideAvailable = true, AvailableMoney = 1_000_000m, RequiredMoney = 25_001m, SessionOpenForNewOrders = true,
            AutoPerOrderRub = 25_000m, OrderAmount = 25_001m, AutoUsedTodayRub = 0m, AutoPlanBuysRub = 25_001m, AutoPerDayRub = 50_000m,
        };
        PreflightChecks.Evaluate(input)!.Code.Should().Be(PreflightChecks.AutoOrderLimit);
        PreflightChecks.Evaluate(input with { OrderAmount = 25_000m }).Should().BeNull();
        PreflightChecks.Evaluate(input with { AutoPerOrderRub = null }).Should().BeNull("ручной план — без лимита по сумме");
    }

    // ─── MINOR 5: дрейф по дизайну ──────────────────────────────────────────────────────────

    [Fact]
    public void DriftDefaults_FollowDesign()
    {
        var o = new ExecutionOptions();
        o.DriftFor(isBond: false).Should().Be(0.002m);
        o.DriftFor(isBond: true).Should().Be(0.005m);
    }

    // ─── MINOR 2: ближайшая сессия ──────────────────────────────────────────────────────────

    private static DateTime NextWeekday(DayOfWeek day)
    {
        var d = DateTime.UtcNow.Date.AddDays(1);
        while (d.DayOfWeek != day) d = d.AddDays(1);
        return DateTime.SpecifyKind(d, DateTimeKind.Utc);
    }

    [Theory]
    [InlineData(DayOfWeek.Tuesday, 9, 0, DayOfWeek.Tuesday)] // 12:00 МСК — сегодня до 18:40
    [InlineData(DayOfWeek.Tuesday, 15, 35, DayOfWeek.Wednesday)] // 18:35 МСК — до закрытия меньше 10 минут → завтра
    [InlineData(DayOfWeek.Tuesday, 17, 0, DayOfWeek.Wednesday)] // 20:00 МСК — закрыто → завтра
    [InlineData(DayOfWeek.Friday, 17, 0, DayOfWeek.Monday)] // пятница вечером → понедельник
    [InlineData(DayOfWeek.Saturday, 9, 0, DayOfWeek.Monday)]
    public void NextSessionClose(DayOfWeek day, int hourUtc, int minute, DayOfWeek expectedDay)
    {
        var now = NextWeekday(day).AddHours(hourUtc).AddMinutes(minute);
        var close = TradingHours.NextSessionCloseUtc(PolicyMarket.Moex, now, new PolicyOptions(), TimeSpan.FromMinutes(10));
        close.Should().NotBeNull();
        close!.Value.DayOfWeek.Should().Be(expectedDay);
        TradingHours.ToMoscow(close.Value).TimeOfDay.Should().Be(new TimeSpan(18, 40, 0));
        close.Value.Should().BeAfter(now);
    }
}

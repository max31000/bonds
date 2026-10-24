using Bonds.Core.Execution;
using Bonds.Core.Models;
using Bonds.Core.Policy;
using Bonds.Infrastructure.Connectors.TInvest;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Execution;

/// <summary>Задача 65: чистые правила исполнителя — ворота, цены попыток, предполётные проверки, порядок строк, сверка комиссии, итог плана, конверсия Quotation.</summary>
public class ExecutionRulesTests
{
    public static IEnumerable<object[]> AllGateCombinations()
    {
        foreach (var enabled in new[] { false, true })
        foreach (var live in new[] { false, true })
        foreach (var token in new[] { false, true })
        foreach (var paused in new[] { false, true })
            yield return [enabled, live, token, paused];
    }

    [Theory]
    [MemberData(nameof(AllGateCombinations))]
    public void Gate_OnlyAllFourConditionsGiveLive(bool enabled, bool live, bool token, bool paused)
    {
        var gate = ExecutionGate.Evaluate(enabled, live ? ExecutionModes.Live : ExecutionModes.Observe, token, paused);

        var expected = enabled && live && token && !paused;
        gate.Live.Should().Be(expected);
        gate.DryRun.Should().Be(!expected);
        gate.Reasons.Should().HaveCount((enabled ? 0 : 1) + (live ? 0 : 1) + (token ? 0 : 1) + (paused ? 1 : 0));
    }

    [Fact]
    public void Gate_UnknownModeIsObserve()
    {
        ExecutionGate.Evaluate(true, "LIVE", true, false).Live.Should().BeFalse("регистр важен: в БД только 'live'");
        ExecutionGate.Evaluate(true, null, true, false).Live.Should().BeFalse();
        ExecutionModes.Normalize("garbage").Should().Be(ExecutionModes.Observe);
    }

    // ─── Цены попыток ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void FirstAttempt_IsApprovedLimitOnGrid()
    {
        OrderPricing.PriceForAttempt(PlanSides.Buy, 92.75m, 92.70m, 92.80m, 0.01m, 0.01m, 1, 3, null).Should().Be(92.75m);
        OrderPricing.PriceForAttempt(PlanSides.Buy, 92.755m, null, null, 0.01m, 0.01m, 1, 3, null).Should().Be(92.75m, "покупка округляется вниз");
        OrderPricing.PriceForAttempt(PlanSides.Sell, 92.755m, null, null, 0.01m, 0.01m, 1, 3, null).Should().Be(92.76m, "продажа — вверх");
    }

    [Fact]
    public void BuyReprice_MovesTowardAsk_WithinDriftFromApproved()
    {
        // Облигация: лимит 92.70 пункта, ask 92.90, шаг 0.01, дрейф 1% → предел 93.62 (ask ближе).
        var p2 = OrderPricing.PriceForAttempt(PlanSides.Buy, 92.70m, 92.60m, 92.90m, 0.01m, 0.01m, 2, 3, 92.70m);
        var p3 = OrderPricing.PriceForAttempt(PlanSides.Buy, 92.70m, 92.60m, 92.90m, 0.01m, 0.01m, 3, 3, p2);
        p2.Should().Be(92.80m, "половина пути к ask");
        p3.Should().Be(92.90m, "последняя попытка — по ask");

        // Ask далеко: цель ограничена пределом дрейфа от УТВЕРЖДЁННОГО лимита (фонд 166.57 ₽, 0.5% → ≤ 167.40).
        var capped = OrderPricing.PriceForAttempt(PlanSides.Buy, 166.57m, 166.00m, 180.00m, 0.01m, 0.005m, 3, 3, 166.57m);
        capped.Should().Be(167.40m);
        OrderPricing.DriftFraction(capped, 166.57m).Should().BeLessThanOrEqualTo(0.005m);
    }

    [Fact]
    public void SellReprice_MovesTowardBid_NeverBelowDriftFloor_NeverBack()
    {
        var p2 = OrderPricing.PriceForAttempt(PlanSides.Sell, 100.00m, 99.00m, 100.10m, 0.01m, 0.005m, 2, 3, 100.00m);
        p2.Should().Be(99.75m, "половина пути к пределу 99.50 (bid 99.00 дальше предела)");
        var p3 = OrderPricing.PriceForAttempt(PlanSides.Sell, 100.00m, 99.00m, 100.10m, 0.01m, 0.005m, 3, 3, p2);
        p3.Should().Be(99.50m);

        // Рынок вернулся вверх: продажа не повышает цену обратно относительно прошлой попытки.
        OrderPricing.PriceForAttempt(PlanSides.Sell, 100.00m, 100.05m, 100.10m, 0.01m, 0.005m, 3, 3, 99.80m).Should().Be(99.80m);
    }

    [Fact]
    public void RequiredMoney_ScalesByLotsAndOnlyUpByPrice()
    {
        var line = new TradePlanLine { Side = PlanSides.Buy, QuantityLots = 4, LimitPrice = 100m, EstAmount = 400m, EstFee = 4m };
        OrderPricing.RequiredMoney(line, 100m, 2).Should().Be(202m);
        OrderPricing.RequiredMoney(line, 101m, 4).Should().Be(404m * 1.01m);
        OrderPricing.RequiredMoney(line, 99m, 4).Should().Be(404m, "ниже лимита — без уменьшения (консервативно)");
    }

    [Fact]
    public void StepAndUnits_BondsInPoints_FundsInCurrency()
    {
        var bond = new TradePlanLine { LimitPrice = 927.57m, LimitPricePoints = 92.757m };
        var fund = new TradePlanLine { LimitPrice = 166.57m };
        var bondMeta = new InstrumentTradingMeta { MinPriceIncrement = 0.1m, MinPriceIncrementPoints = 0.01m };
        var usdBondMeta = new InstrumentTradingMeta { MinPriceIncrement = null, MinPriceIncrementPoints = 0.0001m };
        var fundMeta = new InstrumentTradingMeta { MinPriceIncrement = 0.01m };

        OrderPricing.ApprovedLimit(bond).Should().Be(92.757m);
        OrderPricing.PriceUnitOf(bond).Should().Be(OrderPriceUnits.Points);
        OrderPricing.StepOf(bond, bondMeta).Should().Be(0.01m);
        OrderPricing.StepOf(bond, usdBondMeta).Should().Be(0.0001m, "у облигации с USD-номиналом рублёвого шага нет — сетка в пунктах");
        OrderPricing.ApprovedLimit(fund).Should().Be(166.57m);
        OrderPricing.PriceUnitOf(fund).Should().Be(OrderPriceUnits.Currency);
        OrderPricing.StepOf(fund, fundMeta).Should().Be(0.01m);
        OrderPricing.StepOf(fund, new InstrumentTradingMeta()).Should().BeNull();
    }

    [Fact]
    public void ExecutionOrder_SellsBeforeBuys()
    {
        var lines = new[]
        {
            new TradePlanLine { LineNo = 1, Side = PlanSides.Buy },
            new TradePlanLine { LineNo = 2, Side = PlanSides.Sell },
            new TradePlanLine { LineNo = 3, Side = PlanSides.Buy },
            new TradePlanLine { LineNo = 4, Side = PlanSides.Sell },
        };
        OrderPricing.ExecutionOrder(lines).Select(l => l.LineNo).Should().Equal(2, 4, 1, 3);
    }

    // ─── Предполётные проверки ─────────────────────────────────────────────────────────────

    private static readonly DateTime Now = DateTime.UtcNow;

    private static PreflightInput Ok(string side = PlanSides.Buy) => new()
    {
        Side = side,
        ApprovedLimit = 100m,
        OrderPrice = 100m,
        Lots = 2,
        LotSize = 1m,
        MaxDriftFraction = 0.005m,
        NowUtc = Now,
        MaxQuoteAge = TimeSpan.FromMinutes(2),
        Bid = 99.9m,
        Ask = 100.1m,
        QuoteTimeUtc = Now.AddSeconds(-5),
        NormalTrading = true,
        LimitOrderAvailable = true,
        ApiTradeAvailable = true,
        SideAvailable = true,
        AvailableMoney = 1_000m,
        RequiredMoney = 200m,
        AvailablePieces = 10m,
        AutoUsedTodayRub = null,
        AutoPerDayRub = 50_000m,
        Paused = false,
        SessionOpenForNewOrders = true,
    };

    [Fact]
    public void Preflight_AllGood_Passes()
    {
        PreflightChecks.Evaluate(Ok()).Should().BeNull();
        PreflightChecks.Evaluate(Ok(PlanSides.Sell)).Should().BeNull();
    }

    public static IEnumerable<object[]> Failures() =>
    [
        ["fresh: stale timestamp", PreflightChecks.StaleQuote],
        ["fresh: no bid", PreflightChecks.StaleQuote],
        ["fresh: no timestamp", PreflightChecks.StaleQuote],
        ["drift: mid", PreflightChecks.PriceDrift],
        ["drift: order price", PreflightChecks.PriceDrift],
        ["money", PreflightChecks.InsufficientFunds],
        ["money unknown", PreflightChecks.InsufficientFunds],
        ["position", PreflightChecks.InsufficientPosition],
        ["status", PreflightChecks.NotNormalTrading],
        ["status unknown", PreflightChecks.NotNormalTrading],
        ["limit orders off", PreflightChecks.NotNormalTrading],
        ["api trade", PreflightChecks.ApiTradeUnavailable],
        ["side", PreflightChecks.ApiTradeUnavailable],
        ["auto day limit", PreflightChecks.AutoDayLimit],
        ["paused", PreflightChecks.Paused],
        ["session", PreflightChecks.SessionClosing],
    ];

    [Theory]
    [MemberData(nameof(Failures))]
    public void Preflight_EachCheck_FailsWithItsCode(string scenario, string code)
    {
        var input = scenario switch
        {
            "fresh: stale timestamp" => Ok() with { QuoteTimeUtc = Now.AddMinutes(-3) },
            "fresh: no bid" => Ok() with { Bid = null },
            "fresh: no timestamp" => Ok() with { QuoteTimeUtc = null },
            "drift: mid" => Ok() with { Bid = 100.6m, Ask = 100.8m },
            "drift: order price" => Ok() with { OrderPrice = 100.6m },
            "money" => Ok() with { AvailableMoney = 199.99m },
            "money unknown" => Ok() with { AvailableMoney = null },
            "position" => Ok(PlanSides.Sell) with { AvailablePieces = 1m },
            "status" => Ok() with { NormalTrading = false },
            "status unknown" => Ok() with { NormalTrading = null },
            "limit orders off" => Ok() with { LimitOrderAvailable = false },
            "api trade" => Ok() with { ApiTradeAvailable = false },
            "side" => Ok() with { SideAvailable = false },
            "auto day limit" => Ok() with { AutoUsedTodayRub = 45_000m, AutoPlanBuysRub = 6_000m },
            "paused" => Ok() with { Paused = true },
            "session" => Ok() with { SessionOpenForNewOrders = false },
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };

        PreflightChecks.Evaluate(input)!.Code.Should().Be(code, scenario);
    }

    [Fact]
    public void Preflight_AutoDayLimit_ExactlyAtLimitPasses_AndNonAutoIgnored()
    {
        PreflightChecks.Evaluate(Ok() with { AutoUsedTodayRub = 44_000m, AutoPlanBuysRub = 6_000m }).Should().BeNull();
        PreflightChecks.Evaluate(Ok() with { AutoUsedTodayRub = null, AutoPlanBuysRub = 999_999m }).Should().BeNull();
    }

    [Fact]
    public void Preflight_DriftWithinLimit_Passes_BondDriftUsesPoints()
    {
        // Облигация: лимит 92.70 п., mid 93.50 п. — 0.86% < 1%.
        PreflightChecks.Evaluate(Ok() with { ApprovedLimit = 92.70m, OrderPrice = 92.70m, Bid = 93.40m, Ask = 93.60m, MaxDriftFraction = 0.01m }).Should().BeNull();
        PreflightChecks.Evaluate(Ok() with { ApprovedLimit = 92.70m, OrderPrice = 92.70m, Bid = 93.70m, Ask = 93.80m, MaxDriftFraction = 0.01m })!.Code
            .Should().Be(PreflightChecks.PriceDrift);
    }

    // ─── Состояние строки, итоги, комиссия ─────────────────────────────────────────────────

    private static TradeOrder Order(int attempt, string status, long filled = 0, string? code = null) =>
        new() { Attempt = attempt, Status = status, LotsRequested = 10, LotsFilled = filled, ErrorCode = code };

    [Fact]
    public void LineState_TerminalRules()
    {
        var line = new TradePlanLine { QuantityLots = 10 };
        LineExecutionState.From(line, []).Should().Match<LineExecutionState>(s => !s.Terminal && s.Status == "Pending" && s.RemainingLots == 10);
        LineExecutionState.From(line, [Order(1, TradeOrderStatuses.Submitted)]).Terminal.Should().BeFalse();
        LineExecutionState.From(line, [Order(1, TradeOrderStatuses.Cancelled, 3, TradeOrderErrorCodes.Reprice)])
            .Should().Match<LineExecutionState>(s => !s.Terminal && s.RemainingLots == 7, "снята для перевыставления — строка продолжается");
        LineExecutionState.From(line, [Order(1, TradeOrderStatuses.Cancelled, 3, TradeOrderErrorCodes.SessionClose)]).Terminal.Should().BeTrue();
        LineExecutionState.From(line, [Order(1, TradeOrderStatuses.Failed, 0, PreflightChecks.StaleQuote)]).Terminal.Should().BeTrue();
        LineExecutionState.From(line, [Order(1, TradeOrderStatuses.Cancelled, 4, TradeOrderErrorCodes.Reprice), Order(2, TradeOrderStatuses.Filled, 6)])
            .Should().Match<LineExecutionState>(s => s.Terminal && s.Status == TradeOrderStatuses.Filled && s.FilledLots == 10);
    }

    [Fact]
    public void PlanOutcome_DonePartialFailed()
    {
        PlanExecutionOutcome.Resolve([(10, 10), (5, 5)]).Should().Be(PlanStatuses.Done);
        PlanExecutionOutcome.Resolve([(10, 10), (5, 0)]).Should().Be(PlanStatuses.PartiallyDone);
        PlanExecutionOutcome.Resolve([(10, 3)]).Should().Be(PlanStatuses.PartiallyDone);
        PlanExecutionOutcome.Resolve([(10, 0), (5, 0)]).Should().Be(PlanStatuses.Failed);
    }

    [Fact]
    public void FeeReconciliation_ProportionalToFilled_ThresholdOneKopeck()
    {
        var line = new TradePlanLine { QuantityLots = 10, EstFee = 4.00m };
        FeeReconciliation.ExpectedFee(line, 5).Should().Be(2.00m);
        FeeReconciliation.IsMismatch(line, 5, 2.01m, 0.01m).Should().BeFalse("ровно 1 копейка — в пределах");
        FeeReconciliation.IsMismatch(line, 5, 2.02m, 0.01m).Should().BeTrue();
        FeeReconciliation.IsMismatch(line, 10, 0m, 0.01m).Should().BeTrue("комиссия не списана, а модель ждала 4 ₽");
        FeeReconciliation.IsMismatch(new TradePlanLine { QuantityLots = 10, EstFee = 0m }, 10, 0m, 0.01m).Should().BeFalse("фонды УК — 0%");
    }

    // ─── Quotation ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("92.7568", 92, 756_800_000)]
    [InlineData("166.57", 166, 570_000_000)]
    [InlineData("0.0001", 0, 100_000)]
    [InlineData("-1.5", -1, -500_000_000)]
    [InlineData("1000", 1000, 0)]
    public void ToQuotation_SplitsUnitsAndNano_RoundTrips(string value, long units, int nano)
    {
        var d = decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        var q = TInvestNumeric.ToQuotation(d);
        q.Units.Should().Be(units);
        q.Nano.Should().Be(nano);
        q.ToDecimal().Should().Be(d);
    }

    [Fact]
    public void ToQuotation_TooPrecise_Throws() =>
        FluentActions.Invoking(() => TInvestNumeric.ToQuotation(1.0000000001m)).Should().Throw<ArgumentException>();
}

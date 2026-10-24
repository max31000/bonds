using Bonds.Core.Execution;
using Bonds.Core.Fees;
using Bonds.Core.Policy;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Execution;

/// <summary>Задача 66: чистые правила заявок Freedom — белый список action_id, тикеры, user_order_id, статусы Tradernet, разбор комиссии, сверка по маршруту.</summary>
public class FreedomExecutionRulesTests
{
    [Fact]
    public void ActionIdWhitelist_IsExactlyBuyAndSell_MarginIdsNeverAllowed()
    {
        FreedomOrderRules.AllowedActionIds.Should().BeEquivalentTo([1, 3]);
        FreedomOrderRules.IsAllowedActionId(2).Should().BeFalse("2 — покупка с маржой");
        FreedomOrderRules.IsAllowedActionId(4).Should().BeFalse("4 — продажа в шорт");
        FreedomOrderRules.ActionIdFor(PlanSides.Buy).Should().Be(1);
        FreedomOrderRules.ActionIdFor(PlanSides.Sell).Should().Be(3);
    }

    [Theory]
    [InlineData("")]
    [InlineData("buy")]
    [InlineData("Short")]
    [InlineData("MarginBuy")]
    public void ActionIdFor_UnknownSide_Throws(string side)
    {
        var act = () => FreedomOrderRules.ActionIdFor(side);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData("SPYM.US", true)]
    [InlineData("VUAA.EU", true)]
    [InlineData("spym.us", true)]
    [InlineData("SPYM.ITS", false)]
    [InlineData("SPYM", false)]
    [InlineData("KZTK.KZ", false)]
    [InlineData(".US", false)]
    [InlineData("SP YM.US", false)]
    [InlineData(null, false)]
    public void TradableTicker_OnlyUsAndEu(string? ticker, bool tradable)
    {
        FreedomOrderRules.IsTradableTicker(ticker).Should().Be(tradable);
        (FreedomOrderRules.TickerRejection(ticker) is null).Should().Be(tradable);
    }

    [Fact]
    public void ItsTicker_RejectionExplainsUsRouting()
    {
        FreedomOrderRules.IsItsTicker("SPYM.ITS").Should().BeTrue();
        FreedomOrderRules.TickerRejection("SPYM.ITS").Should().Contain("ITS").And.Contain(".US");
    }

    [Fact]
    public void UserOrderId_IsNumeric_InRange_AndUnique()
    {
        var ids = Enumerable.Range(0, 500).Select(_ => FreedomOrderRules.NewUserOrderId()).ToList();
        ids.Should().OnlyContain(id => FreedomOrderRules.IsValidUserOrderId(id));
        ids.Should().OnlyContain(id => id.Length == 10);
        ids.Distinct().Count().Should().BeGreaterThan(495);
        FreedomOrderRules.IsValidUserOrderId("api:1792764255").Should().BeFalse();
        FreedomOrderRules.IsValidUserOrderId("999999999").Should().BeFalse();
        FreedomOrderRules.IsValidUserOrderId("2147483647").Should().BeFalse();
        FreedomOrderRules.IsValidUserOrderId("-1234567890").Should().BeFalse();
    }

    [Theory]
    [InlineData("1234567890", "1234567890", true)]
    [InlineData("api:1234567890", "1234567890", true)]
    [InlineData(" 1234567890 ", "1234567890", true)]
    [InlineData("api:1792764255", "1234567890", false)]
    [InlineData("11234567890", "1234567890", false)]
    [InlineData("", "1234567890", false)]
    [InlineData(null, "1234567890", false)]
    public void UserOrderIdMatches_ExactOrPrefixed(string? broker, string ours, bool match) =>
        FreedomOrderRules.UserOrderIdMatches(broker, ours).Should().Be(match);

    [Theory]
    [InlineData(3, 5, 5, TradeOrderStatuses.Filled)]
    [InlineData(4, 5, 0, TradeOrderStatuses.Cancelled)]
    [InlineData(4, 5, 2, TradeOrderStatuses.Cancelled)]
    [InlineData(7, 5, 0, TradeOrderStatuses.Rejected)]
    [InlineData(2, 5, 2, TradeOrderStatuses.PartiallyFilled)]
    [InlineData(10, 5, 0, TradeOrderStatuses.Submitted)]
    [InlineData(1, 5, 0, TradeOrderStatuses.Submitted)]
    [InlineData(6, 5, 0, TradeOrderStatuses.Submitted)]
    [InlineData(99, 5, 0, TradeOrderStatuses.Submitted)]
    [InlineData(10, 5, 3, TradeOrderStatuses.PartiallyFilled)]
    [InlineData(99, 5, 5, TradeOrderStatuses.Filled)]
    public void StatusMap_IsConservative_UnknownCodesStayActive(int stat, long requested, long executed, string expected) =>
        FreedomOrderStatusMap.Resolve(stat, requested, executed).Should().Be(expected);

    [Fact]
    public void CommissionComment_ParsesTradeAndActualRoute()
    {
        // Строка из синтетической фикстуры getUserCashFlows.json.
        var c = FreedomCommissionParser.Parse(
            "(Trade 900000101 buy SPYM.US ) Рынок: kazakhstan_its, тип бумаги: funds, валюта комиссии: USD, тариф Standard, валюта сделки: USD.  Установлена минимальная комиссия в процентах от суммы сделки: 0.5. ");
        c.Should().NotBeNull();
        c!.TradeId.Should().Be("900000101");
        c.Side.Should().Be(PlanSides.Buy);
        c.Ticker.Should().Be("SPYM.US");
        c.Route.Should().Be(FreedomMarket.KazakhstanItsFunds);
    }

    [Theory]
    [InlineData("(Trade 1 sell VUAA.EU ) Рынок: europe, тип бумаги: funds", FreedomMarket.Europe)]
    [InlineData("(Trade 1 buy AAPL.US ) Рынок: usa, тип бумаги: stocks", FreedomMarket.Usa)]
    [InlineData("(Trade 1 buy AAPL.US ) Рынок: kazakhstan_its, тип бумаги: stocks", FreedomMarket.KazakhstanIts)]
    [InlineData("(Trade 1 buy AAPL.US ) Market: kazakhstan_its, security type: funds", FreedomMarket.KazakhstanItsFunds)]
    public void CommissionComment_RouteByMarketAndClass(string comment, FreedomMarket route) =>
        FreedomCommissionParser.Parse(comment)!.Route.Should().Be(route);

    [Fact]
    public void CommissionComment_UnknownMarketOrNoTrade()
    {
        FreedomCommissionParser.Parse("(Trade 1 buy X.US ) Рынок: mars")!.Route.Should().BeNull();
        FreedomCommissionParser.Parse("Дивиденды по бумаге").Should().BeNull();
        FreedomCommissionParser.Parse(null).Should().BeNull();
    }

    [Fact]
    public void FeeCheck_ItsFunds_Standard_HalfPercent()
    {
        // SPYM.US через ITS, фонды, Standard: 0.5% × $54 = $0.27 (как в фикстуре).
        var check = FreedomFeeReconciliation.Check(
            FreedomFeeModel.StandardTariffId, "USD", [new FreedomFilledTrade("900000101", 1m, 54m, 0.27m, FreedomMarket.KazakhstanItsFunds)]);
        check.Complete.Should().BeTrue();
        check.ActualFee.Should().Be(0.27m);
        check.ExpectedFee.Should().Be(0.27m);
        check.Route.Should().Be(FreedomMarket.KazakhstanItsFunds);
    }

    [Fact]
    public void FeeCheck_EuropeTwoTrades_PerOrderChargedOnce()
    {
        // VUAA.EU, Standard: 0.12% + $1.2 только в первой сделке заявки.
        var trades = new[]
        {
            new FreedomFilledTrade("1", 2m, 295.4m, 1.55m, FreedomMarket.Europe),
            new FreedomFilledTrade("2", 1m, 147.7m, 0.18m, FreedomMarket.Europe),
        };
        var check = FreedomFeeReconciliation.Check(FreedomFeeModel.StandardTariffId, "USD", trades);
        check.ExpectedFee.Should().Be(FreedomFeeModel.Fee(1431, FreedomMarket.Europe, 295.4m, 2m) + FreedomFeeModel.Fee(1431, FreedomMarket.Europe, 147.7m, 1m, includePerOrder: false));
        check.ExpectedFee.Should().Be(1.55m + 0.18m);
        check.ActualFee.Should().Be(1.73m);
    }

    [Fact]
    public void FeeCheck_IncompleteOrNonUsd_HasNoModel()
    {
        FreedomFeeReconciliation.Check(1431, "USD", [new FreedomFilledTrade("1", 1m, 10m, null, FreedomMarket.Usa)]).Complete.Should().BeFalse();
        FreedomFeeReconciliation.Check(1431, "USD", []).Complete.Should().BeFalse();
        FreedomFeeReconciliation.Check(1431, "EUR", [new FreedomFilledTrade("1", 1m, 10m, 1.2m, FreedomMarket.Europe)]).ExpectedFee.Should().BeNull();
        FreedomFeeReconciliation.Check(9999, "USD", [new FreedomFilledTrade("1", 1m, 10m, 1.2m, FreedomMarket.Europe)]).ExpectedFee.Should().BeNull();
    }

    [Fact]
    public void FreedomGate_UsesFreedomCredentialReason()
    {
        var gate = ExecutionGate.Evaluate(true, ExecutionModes.Live, false, false, ExecutionGate.NoFreedomTradeCredential);
        gate.Live.Should().BeFalse();
        gate.Reasons.Should().ContainSingle().Which.Should().Contain("freedom_trade");
        ExecutionGate.Evaluate(true, ExecutionModes.Live, false, false).Reasons.Should().ContainSingle().Which.Should().Contain("T-Invest");
    }
}

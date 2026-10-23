using Bonds.Core.Fees;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Fees;

/// <summary>Задача 59: эталоны модели комиссий Freedom (из выписки; суммы в USD, допуск ±0.01 — брокер округляет итог).</summary>
public class FreedomFeeModelTests
{
    private const int Investor = FreedomFeeModel.InvestorTariffId;
    private const int Standard = FreedomFeeModel.StandardTariffId;

    [Theory]
    [InlineData(Investor, "COPX.EU", false, 2, 70.12, 1.92)]
    [InlineData(Investor, "NDIA.EU", false, 6, 8.803, 1.53)]
    [InlineData(Investor, "NUKL.EU", false, 2, 57.9830, 1.80)]   // 49.95 EUR × 1.1607
    [InlineData(Investor, "SJPA.EU", false, 2, 81.3296, 2.03)]   // 60.51 GBP × 1.3441
    [InlineData(Investor, "LUV.US", true, 2, 42.90, 0.45)]
    [InlineData(Investor, "NVDA.US", true, 1, 191.03, 0.97)]
    [InlineData(Investor, "OTGLY.US", false, 9, 17.54, 2.10)]
    [InlineData(Standard, "VUAA.EU", false, 3, 147.70, 1.73)]
    public void Fee_MatchesStatement(int tariff, string ticker, bool itsTwin, int qty, double unitPriceUsd, double expected)
    {
        var amount = (decimal)unitPriceUsd * qty;
        var market = FreedomFeeModel.RouteFor(ticker, itsTwin, isFund: false);
        FreedomFeeModel.Fee(tariff, market, amount, qty).Should().BeApproximately((decimal)expected, 0.01m);
    }

    [Fact]
    public void Fee_StandardItsFunds_HalfPercent()
    {
        var market = FreedomFeeModel.RouteFor("SPYM.US", itsTwin: true, isFund: true);
        market.Should().Be(FreedomMarket.KazakhstanItsFunds);
        FreedomFeeModel.Fee(Standard, market, 89.41m, 1).Should().BeApproximately(0.45m, 0.01m);
    }

    [Fact]
    public void Fee_PerOrderChargedOnce_SecondFillOfSameOrderHasNone()
    {
        var first = FreedomFeeModel.Fee(Standard, FreedomMarket.Europe, 100m, 1, includePerOrder: true);
        var second = FreedomFeeModel.Fee(Standard, FreedomMarket.Europe, 100m, 1, includePerOrder: false);
        (first - second).Should().Be(1.2m);
    }

    [Fact]
    public void Fee_Otc_MinimumApplies()
    {
        FreedomFeeModel.Fee(Investor, FreedomMarket.Otc, 100m, 1).Should().Be(25m);
    }

    [Fact]
    public void Fee_UnknownTariff_Throws()
    {
        var act = () => FreedomFeeModel.Fee(9999, FreedomMarket.Usa, 100m, 1);
        act.Should().Throw<NotSupportedException>();
    }

    [Theory]
    [InlineData("VUAA.EU", false, false, FreedomMarket.Europe)]
    [InlineData("LUV.US", true, false, FreedomMarket.KazakhstanIts)]
    [InlineData("SPYM.US", true, true, FreedomMarket.KazakhstanItsFunds)]
    [InlineData("OTGLY.US", false, false, FreedomMarket.Usa)]
    [InlineData("XYZ.KZ", false, false, FreedomMarket.Kazakhstan)]
    public void RouteFor_ByTickerSuffix(string ticker, bool twin, bool fund, FreedomMarket expected) =>
        FreedomFeeModel.RouteFor(ticker, twin, fund).Should().Be(expected);

    [Fact]
    public void CheaperRoute_BreakEvenAround315()
    {
        FreedomFeeModel.BreakEvenAmountUsd.Should().BeApproximately(315.79m, 0.01m);
        FreedomFeeModel.CheaperRoute(300m).Should().Be(FreedomMarket.KazakhstanItsFunds);
        FreedomFeeModel.CheaperRoute(400m).Should().Be(FreedomMarket.Europe);
    }
}

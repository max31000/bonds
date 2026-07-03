using Bonds.Core.Analytics;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Analytics;

/// <summary>
/// Тесты «если продать сейчас» (plan/19 §A.4) — чистый расчёт выручки за вычетом комиссии и,
/// при наличии cost basis (plan/14), итогового P&amp;L с учётом купонов.
/// </summary>
public class IfSoldNowServiceTests
{
    [Fact]
    public void Calculate_WithoutCostBasis_ReturnsProceedsAndCommissionOnly_PnlUnavailable()
    {
        var result = IfSoldNowService.Calculate(marketValueRub: 100_000m, costBasis: null, commissionRate: 0.003m);

        result.MarketValueRub.Should().Be(100_000m);
        result.CommissionRub.Should().Be(300m);
        result.NetProceedsRub.Should().Be(99_700m);
        result.PnlAvailable.Should().BeFalse();
        result.RealizedPnlRub.Should().BeNull();
        result.RealizedPnlPercent.Should().BeNull();
        result.TotalReturnWithCouponsRub.Should().BeNull();
        result.Disclaimer.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Calculate_WithCostBasis_ComputesRealizedPnlAndTotalReturnWithCoupons()
    {
        var costBasis = new PositionCostBasis
        {
            AverageCostRub = 950m,
            InvestedRub = 95_000m,
            UnrealizedPnlRub = 5_000m,
            UnrealizedPnlPercent = 5_000m / 95_000m,
            CouponsReceivedRub = 3_000m,
            TotalReturnRub = 8_000m,
            TotalReturnPercent = 8_000m / 95_000m,
            HasUnknownLots = false,
        };

        var result = IfSoldNowService.Calculate(marketValueRub: 100_000m, costBasis: costBasis, commissionRate: 0.003m);

        result.CommissionRub.Should().Be(300m);
        result.NetProceedsRub.Should().Be(99_700m);
        result.PnlAvailable.Should().BeTrue();
        result.RealizedPnlRub.Should().Be(4_700m); // 99700 - 95000
        result.RealizedPnlPercent.Should().BeApproximately(4_700m / 95_000m, 1e-9m);
        result.CouponsReceivedRub.Should().Be(3_000m);
        result.TotalReturnWithCouponsRub.Should().Be(7_700m); // 4700 + 3000
    }

    [Fact]
    public void Calculate_CostBasisWithUnknownInvested_PnlUnavailable_ButDoesNotThrow()
    {
        // Журнал не покрывает остаток целиком, средняя цена входа не посчиталась (null InvestedRub) —
        // расчёт выручки/комиссии всё равно возвращается, P&L-поля — null, не 0 (spec §4.4).
        var costBasis = new PositionCostBasis
        {
            AverageCostRub = null,
            InvestedRub = null,
            UnrealizedPnlRub = null,
            UnrealizedPnlPercent = null,
            CouponsReceivedRub = 0m,
            TotalReturnRub = null,
            TotalReturnPercent = null,
            HasUnknownLots = true,
        };

        var result = IfSoldNowService.Calculate(marketValueRub: 50_000m, costBasis: costBasis);

        result.PnlAvailable.Should().BeFalse();
        result.RealizedPnlRub.Should().BeNull();
        result.NetProceedsRub.Should().Be(50_000m - 50_000m * SwitchAnalysisService.DefaultCommissionRate);
    }

    [Fact]
    public void Calculate_UsesDefaultCommissionRate_WhenNotOverridden()
    {
        var result = IfSoldNowService.Calculate(marketValueRub: 10_000m, costBasis: null);

        result.CommissionRate.Should().Be(SwitchAnalysisService.DefaultCommissionRate);
        result.CommissionRub.Should().Be(10_000m * SwitchAnalysisService.DefaultCommissionRate);
    }

    [Fact]
    public void Calculate_ZeroMarketValue_ReturnsZeroesWithoutThrowing()
    {
        var result = IfSoldNowService.Calculate(marketValueRub: 0m, costBasis: null);

        result.CommissionRub.Should().Be(0m);
        result.NetProceedsRub.Should().Be(0m);
    }
}

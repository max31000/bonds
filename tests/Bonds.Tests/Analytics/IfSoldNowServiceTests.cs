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

    // ─── Задача 24: разложение выручки на чистую стоимость + НКД − комиссию ────────────────────

    [Fact]
    public void Calculate_WithAccruedTotal_SplitsMarketValueIntoCleanPlusAccrued()
    {
        var result = IfSoldNowService.Calculate(
            marketValueRub: 100_000m, costBasis: null, commissionRate: 0.003m, accruedTotalRub: 1_200m);

        result.AccruedTotalRub.Should().Be(1_200m);
        result.CleanValueRub.Should().Be(98_800m); // 100000 - 1200
        // Сумма разложения должна сходиться: clean + accrued - commission = netProceeds.
        (result.CleanValueRub + result.AccruedTotalRub - result.CommissionRub).Should().Be(result.NetProceedsRub);
    }

    [Fact]
    public void Calculate_WithoutAccruedTotal_DefaultsToZero_CleanValueEqualsMarketValue()
    {
        var result = IfSoldNowService.Calculate(marketValueRub: 50_000m, costBasis: null, commissionRate: 0.003m);

        result.AccruedTotalRub.Should().Be(0m);
        result.CleanValueRub.Should().Be(50_000m);
    }

    // ─── Задача 25: оценка НДФЛ с продажи и итог после налога ──────────────────────────────────

    [Fact]
    public void Calculate_ProfitablePosition_ComputesTaxEstimateAndNetAfterTax()
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

        // NetProceedsRub = 99700, RealizedPnlRub = 4700, TaxableGain = 4700, Tax = 4700 * 0.13 = 611
        result.TaxEstimateRub.Should().Be(611m);
        result.TotalReturnWithCouponsRub.Should().Be(7_700m);
        result.NetAfterTaxRub.Should().Be(7_089m); // 7700 - 611
    }

    [Fact]
    public void Calculate_LossPosition_TaxEstimateIsZero_NotNegative()
    {
        var costBasis = new PositionCostBasis
        {
            AverageCostRub = 1_200m,
            InvestedRub = 120_000m,
            UnrealizedPnlRub = -20_000m,
            UnrealizedPnlPercent = -20_000m / 120_000m,
            CouponsReceivedRub = 0m,
            TotalReturnRub = -20_000m,
            TotalReturnPercent = -20_000m / 120_000m,
            HasUnknownLots = false,
        };

        var result = IfSoldNowService.Calculate(marketValueRub: 100_000m, costBasis: costBasis, commissionRate: 0.003m);

        result.TaxEstimateRub.Should().Be(0m);
        result.NetAfterTaxRub.Should().Be(result.TotalReturnWithCouponsRub);
    }

    [Fact]
    public void Calculate_HasUnknownLots_TaxEstimateAndNetAfterTaxAreNull_NotZero()
    {
        var costBasis = new PositionCostBasis
        {
            AverageCostRub = 950m,
            InvestedRub = 95_000m,
            UnrealizedPnlRub = 5_000m,
            UnrealizedPnlPercent = 5_000m / 95_000m,
            CouponsReceivedRub = 0m,
            TotalReturnRub = 5_000m,
            TotalReturnPercent = 5_000m / 95_000m,
            HasUnknownLots = true,
        };

        var result = IfSoldNowService.Calculate(marketValueRub: 100_000m, costBasis: costBasis, commissionRate: 0.003m);

        result.TaxEstimateRub.Should().BeNull("журнал операций неполон — налог нельзя оценить");
        result.NetAfterTaxRub.Should().BeNull();
    }

    [Fact]
    public void Calculate_WithoutCostBasis_TaxEstimateAndNetAfterTaxAreNull()
    {
        var result = IfSoldNowService.Calculate(marketValueRub: 100_000m, costBasis: null, commissionRate: 0.003m);

        result.TaxEstimateRub.Should().BeNull();
        result.NetAfterTaxRub.Should().BeNull();
    }
}

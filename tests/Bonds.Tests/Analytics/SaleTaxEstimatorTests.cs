using Bonds.Core.Analytics;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Analytics;

/// <summary>
/// Задача 25 часть A — оценка НДФЛ при продаже (average cost, без сальдирования/ЛДВ, плоские 13%,
/// см. doc-comment <see cref="SaleTaxEstimator"/>). Null — семантика "оценить нельзя", не 0.
/// </summary>
public class SaleTaxEstimatorTests
{
    [Fact]
    public void Estimate_ProfitableSale_Taxes13PercentOfGain()
    {
        var result = SaleTaxEstimator.Estimate(netProceedsRub: 120_000m, investedRub: 100_000m, hasUnknownLots: false);

        result.Should().NotBeNull();
        result!.TaxableGainRub.Should().Be(20_000m);
        result.TaxRub.Should().Be(2_600m); // 20000 * 0.13
        result.IsEstimate.Should().BeTrue();
    }

    [Fact]
    public void Estimate_LossSale_TaxIsZero_NotNegative()
    {
        var result = SaleTaxEstimator.Estimate(netProceedsRub: 80_000m, investedRub: 100_000m, hasUnknownLots: false);

        result.Should().NotBeNull();
        result!.TaxableGainRub.Should().Be(0m);
        result.TaxRub.Should().Be(0m);
    }

    [Fact]
    public void Estimate_BreakEvenSale_ReturnsZero_NotNull()
    {
        var result = SaleTaxEstimator.Estimate(netProceedsRub: 100_000m, investedRub: 100_000m, hasUnknownLots: false);

        result.Should().NotBeNull();
        result!.TaxableGainRub.Should().Be(0m);
        result.TaxRub.Should().Be(0m);
    }

    [Fact]
    public void Estimate_HasUnknownLots_ReturnsNull_NotZero()
    {
        var result = SaleTaxEstimator.Estimate(netProceedsRub: 120_000m, investedRub: 100_000m, hasUnknownLots: true);

        result.Should().BeNull("журнал операций неполон — налог нельзя оценить, это не то же самое, что налог 0");
    }

    [Fact]
    public void Estimate_InvestedRubIsNull_ReturnsNull_NotZero()
    {
        var result = SaleTaxEstimator.Estimate(netProceedsRub: 120_000m, investedRub: null, hasUnknownLots: false);

        result.Should().BeNull("cost basis недоступен — та же семантика 'оценить нельзя'");
    }

    [Fact]
    public void NdflRate_Is13Percent()
    {
        SaleTaxEstimator.NdflRate.Should().Be(0.13m);
    }
}

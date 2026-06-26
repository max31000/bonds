using Bonds.Core.Analytics;
using Bonds.Core.CashFlow;
using Bonds.Core.Models;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Analytics;

public class PortfolioTrajectoryServiceTests
{
    private static PortfolioHolding Holding(
        ulong positionId,
        decimal marketValue,
        decimal? ytmEffective = null) => new()
    {
        PositionId = positionId,
        InstrumentId = positionId,
        Quantity = 1,
        MarketValueRub = marketValue,
        CouponType = CouponType.Fixed,
        MaturityDate = new DateOnly(2030, 1, 1),
        HorizonDate = new DateOnly(2030, 1, 1),
        IsCalculatedToOffer = false,
        YtmEffective = ytmEffective,
        IsFloater = false,
        IsIndexed = false,
        IsEstimated = false,
        DataIncomplete = false,
    };

    private static MonthlyCashFlowSummary MonthSummary(DateOnly month, decimal netRub) => new()
    {
        Month = month,
        GrossRub = netRub,
        TaxRub = 0m,
        NetRub = netRub,
        CouponGrossRub = netRub,
        PrincipalGrossRub = 0m,
        HasEstimatedFlows = false,
    };

    [Fact]
    public void MonotonicWithNoFlows()
    {
        var holdings = new[] { Holding(1, 100_000m, 0.15m) };
        var result = PortfolioTrajectoryService.Compute(holdings, [], 36, 0.12m);

        result.WithReinvest.Should().HaveCount(36);
        result.WithoutReinvest.Should().HaveCount(36);

        foreach (var p in result.WithReinvest)
        {
            p.PortfolioValueRub.Should().Be(100_000m);
            p.CumulativeIncomeRub.Should().Be(0m);
        }
        foreach (var p in result.WithoutReinvest)
        {
            p.PortfolioValueRub.Should().Be(100_000m);
            p.CumulativeIncomeRub.Should().Be(0m);
        }
    }

    [Fact]
    public void WithReinvestGrowsFasterThanWithout()
    {
        var holdings = new[] { Holding(1, 100_000m, 0.15m) };

        // Build 12 monthly summaries with 1000 each, all in future months
        var today = DateOnly.FromDateTime(DateTime.Today);
        var summaries = Enumerable.Range(1, 12)
            .Select(i => MonthSummary(new DateOnly(today.Year, today.Month, 1).AddMonths(i), 1000m))
            .ToList();

        var result = PortfolioTrajectoryService.Compute(holdings, summaries, 12, 0.12m);

        result.WithReinvest[11].PortfolioValueRub
            .Should().BeGreaterThan(result.WithoutReinvest[11].PortfolioValueRub);
    }

    [Fact]
    public void DefaultReinvestRate_WeightedAverage()
    {
        var holdings = new[]
        {
            Holding(1, 100_000m, ytmEffective: 0.10m),
            Holding(2, 200_000m, ytmEffective: 0.20m),
        };

        var rate = PortfolioTrajectoryService.DefaultReinvestRate(holdings);

        // (0.10 * 100000 + 0.20 * 200000) / 300000 = 50000/300000 ≈ 0.1667
        rate.Should().BeApproximately(50_000m / 300_000m, 0.000001m);
    }

    [Fact]
    public void DefaultReinvestRate_FallbackWhenNoYtm()
    {
        var holdings = new[]
        {
            Holding(1, 100_000m, ytmEffective: null),
            Holding(2, 50_000m, ytmEffective: null),
        };

        var rate = PortfolioTrajectoryService.DefaultReinvestRate(holdings);

        rate.Should().Be(0.12m);
    }
}

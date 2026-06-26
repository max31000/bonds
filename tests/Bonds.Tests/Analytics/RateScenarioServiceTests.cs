using Bonds.Core.Analytics;
using Bonds.Core.Models;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Analytics;

public class RateScenarioServiceTests
{
    private static PortfolioHolding Holding(
        ulong positionId,
        decimal marketValue,
        decimal? modifiedDuration,
        decimal? convexity = null) => new()
    {
        PositionId = positionId,
        InstrumentId = positionId,
        Quantity = 1,
        MarketValueRub = marketValue,
        CouponType = CouponType.Fixed,
        MaturityDate = new DateOnly(2030, 1, 1),
        HorizonDate = new DateOnly(2030, 1, 1),
        IsCalculatedToOffer = false,
        ModifiedDuration = modifiedDuration,
        Convexity = convexity,
        IsFloater = false,
        IsIndexed = false,
        IsEstimated = false,
        DataIncomplete = false,
    };

    [Fact]
    public void DurationEffect_KnownValues()
    {
        var holdings = new[] { Holding(1, 100_000m, modifiedDuration: 5m, convexity: null) };

        var result = RateScenarioService.Compute(holdings, [100]);

        var point = result.Single();
        point.ShiftBp.Should().Be(100);
        point.DeltaRub.Should().Be(-5000m);
    }

    [Fact]
    public void ConvexityEffect_KnownValues()
    {
        var holdings = new[] { Holding(1, 100_000m, modifiedDuration: 5m, convexity: 50m) };

        var result = RateScenarioService.Compute(holdings, [100]);

        var point = result.Single();
        // duration: -5 * 0.01 = -0.05 (rate)
        // convexity: 0.5 * 50 * 0.01^2 = 0.0025 (rate)
        // combined: (-0.05 + 0.0025) * 100000 = -4750
        point.DeltaRub.Should().Be(-4750m);
    }

    [Fact]
    public void ZeroShift_ReturnsCurrentValue()
    {
        var holdings = new[] { Holding(1, 100_000m, modifiedDuration: 5m, convexity: 50m) };

        var result = RateScenarioService.Compute(holdings, [0]);

        var point = result.Single();
        point.DeltaRub.Should().Be(0m);
        point.DeltaPercent.Should().Be(0m);
    }

    [Fact]
    public void NoDuration_PositionsSkipped()
    {
        var holdings = new[]
        {
            Holding(1, 100_000m, modifiedDuration: null),
            Holding(2, 50_000m, modifiedDuration: 3m),
        };

        var result = RateScenarioService.Compute(holdings, [100]);

        var point = result.Single();
        // only holding 2 participates: -3 * 0.01 * 50000 = -1500
        point.DeltaRub.Should().Be(-1500m);
    }
}

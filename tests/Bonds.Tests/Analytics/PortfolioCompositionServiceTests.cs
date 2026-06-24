using Bonds.Core.Analytics;
using Bonds.Core.Models;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Analytics;

/// <summary>Тесты композиции портфеля (plan/06 B2, spec §9 «Композиция портфеля» — сумма долей = 100%).</summary>
public class PortfolioCompositionServiceTests
{
    private static PortfolioHolding Holding(
        ulong positionId, decimal marketValue, string issuer, string? sector = "Финансы",
        CouponType couponType = CouponType.Fixed, decimal? duration = 2m) => new()
    {
        PositionId = positionId,
        InstrumentId = positionId,
        Quantity = 1,
        MarketValueRub = marketValue,
        Issuer = issuer,
        Sector = sector,
        CouponType = couponType,
        MaturityDate = new DateOnly(2030, 1, 1),
        HorizonDate = new DateOnly(2030, 1, 1),
        IsCalculatedToOffer = false,
        ModifiedDuration = duration,
        YtmEffective = 0.15m,
        IsFloater = couponType == CouponType.Floating,
        IsIndexed = couponType == CouponType.Indexed,
        IsEstimated = false,
        DataIncomplete = false,
    };

    [Fact]
    public void Calculate_SharesSumTo100Percent_AcrossAllDimensions()
    {
        var holdings = new[]
        {
            Holding(1, 30000m, "Эмитент А", duration: 0.5m),
            Holding(2, 45000m, "Эмитент Б", duration: 4m),
            Holding(3, 25000m, "Эмитент А", couponType: CouponType.Floating, duration: null),
        };

        var composition = PortfolioCompositionService.Calculate(holdings);

        composition.TotalMarketValueRub.Should().Be(100000m);
        composition.ByIssuer.Sum(s => s.SharePercent).Should().Be(100m);
        composition.BySector.Sum(s => s.SharePercent).Should().Be(100m);
        composition.ByCouponType.Sum(s => s.SharePercent).Should().Be(100m);
        composition.ByDurationBucket.Sum(s => s.SharePercent).Should().Be(100m);
    }

    [Fact]
    public void Calculate_GroupsByIssuer_CombiningMultiplePositionsOfSameIssuer()
    {
        var holdings = new[]
        {
            Holding(1, 30000m, "Эмитент А"),
            Holding(2, 20000m, "Эмитент А"),
            Holding(3, 50000m, "Эмитент Б"),
        };

        var composition = PortfolioCompositionService.Calculate(holdings);

        var issuerA = composition.ByIssuer.Single(s => s.Key == "Эмитент А");
        issuerA.MarketValueRub.Should().Be(50000m);
        issuerA.SharePercent.Should().Be(50m);
    }

    [Fact]
    public void Calculate_UnknownDuration_GoesToSeparateBucket_NotDropped()
    {
        var holdings = new[]
        {
            Holding(1, 50000m, "А", duration: 2m),
            Holding(2, 50000m, "Б", couponType: CouponType.Floating, duration: null),
        };

        var composition = PortfolioCompositionService.Calculate(holdings);

        composition.ByDurationBucket.Should().Contain(s => s.Key == "Не определено");
        composition.ByDurationBucket.Sum(s => s.MarketValueRub).Should().Be(100000m, "ни одна позиция не потеряна");
    }

    [Fact]
    public void Calculate_DurationBuckets_AssignCorrectly()
    {
        var holdings = new[]
        {
            Holding(1, 10000m, "А", duration: 0.5m),  // 0-1
            Holding(2, 10000m, "Б", duration: 2m),    // 1-3
            Holding(3, 10000m, "В", duration: 4m),     // 3-5
            Holding(4, 10000m, "Г", duration: 6m),     // 5-7
            Holding(5, 10000m, "Д", duration: 9m),     // 7+
        };

        var composition = PortfolioCompositionService.Calculate(holdings);

        composition.ByDurationBucket.Select(s => s.Key).Should().Contain(
            new[] { "0–1 года", "1–3 года", "3–5 лет", "5–7 лет", "7+ лет" });
    }

    [Fact]
    public void Calculate_EmptyPortfolio_ReturnsZeroSharesWithoutThrowing()
    {
        var composition = PortfolioCompositionService.Calculate(Array.Empty<PortfolioHolding>());

        composition.TotalMarketValueRub.Should().Be(0m);
        composition.ByIssuer.Should().BeEmpty();
    }
}

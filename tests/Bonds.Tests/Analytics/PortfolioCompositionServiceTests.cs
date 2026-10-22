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

public class PortfolioCompositionWithFundsTests
{
    private static PortfolioHolding Bond(ulong id, decimal mv, string issuer, decimal? dur = 2m) => new()
    {
        PositionId = id, InstrumentId = id, Quantity = 1, MarketValueRub = mv, Issuer = issuer, Sector = "Финансы",
        CouponType = CouponType.Fixed, MaturityDate = new DateOnly(2030, 1, 1), HorizonDate = new DateOnly(2030, 1, 1),
        IsCalculatedToOffer = false, ModifiedDuration = dur, IsFloater = false, IsIndexed = false, IsEstimated = false, DataIncomplete = false,
    };

    private static readonly FundExposure[] Funds =
    [
        new(1, "TOFZ", "Облигации ОФЗ", 33_333m, 4.2m),
        new(2, "TPAY", "Пассивный доход", 11_111m, null),
    ];

    [Fact]
    public void SharesSumTo100_InEveryDimension_IncludingAssetClass()
    {
        var c = PortfolioCompositionService.CalculateWithFunds(
            [Bond(1, 30_000m, "А"), Bond(2, 25_555m, "Б", dur: 0.5m)], Funds);

        c.TotalMarketValueRub.Should().Be(99_999m);
        foreach (var dim in new[] { c.ByIssuer, c.BySector, c.ByCouponType, c.ByDurationBucket, c.ByAssetClass })
            dim.Sum(s => s.SharePercent).Should().Be(100m);
    }

    [Fact]
    public void FundLabels_AndAssetClass()
    {
        var c = PortfolioCompositionService.CalculateWithFunds([Bond(1, 55_555m, "А")], Funds);

        c.ByIssuer.Select(s => s.Key).Should().Contain(["Фонд: Облигации ОФЗ", "Фонд: Пассивный доход", "А"]);
        c.BySector.Should().Contain(s => s.Key == "Фонды облигаций" && s.MarketValueRub == 44_444m);
        c.ByCouponType.Should().Contain(s => s.Key == "Фонд");
        c.ByDurationBucket.Should().Contain(s => s.Key == "3–5 лет" && s.MarketValueRub == 33_333m);
        c.ByDurationBucket.Should().Contain(s => s.Key == "Не определено" && s.MarketValueRub == 11_111m);
        c.ByAssetClass.Single(s => s.Key == "Облигации").MarketValueRub.Should().Be(55_555m);
        c.ByAssetClass.Single(s => s.Key == "Фонды").MarketValueRub.Should().Be(44_444m);
    }

    [Fact]
    public void Calculate_WithoutFunds_Unchanged_AssetClassEmpty()
    {
        var c = PortfolioCompositionService.Calculate([Bond(1, 100m, "А")]);

        c.ByAssetClass.Should().BeEmpty();
        c.ByIssuer.Should().ContainSingle(s => s.Key == "А");
    }

    [Fact]
    public void NoFunds_EqualsCalculateOnBondDimensions()
    {
        var bonds = new[] { Bond(1, 30_000m, "А"), Bond(2, 70_000m, "Б") };
        var with = PortfolioCompositionService.CalculateWithFunds(bonds, []);
        var legacy = PortfolioCompositionService.Calculate(bonds);

        with.ByIssuer.Should().BeEquivalentTo(legacy.ByIssuer, o => o.WithStrictOrdering());
        with.ByDurationBucket.Should().BeEquivalentTo(legacy.ByDurationBucket, o => o.WithStrictOrdering());
        with.TotalMarketValueRub.Should().Be(legacy.TotalMarketValueRub);
    }
}

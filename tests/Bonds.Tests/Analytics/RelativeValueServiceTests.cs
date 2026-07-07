using Bonds.Core.Analytics;
using FluentAssertions;
using Xunit;
using static Bonds.Core.Analytics.RelativeValueService;

namespace Bonds.Tests.Analytics;

/// <summary>
/// Задача 30 часть A — тесты корзин (сектор × дюрация), медианы/перцентилей, fallback-цепочки
/// корзина → сектор → рынок и знака deviation. Границы дюрации переиспользуют
/// <see cref="DurationBucketClassifier"/> — те же, что композиция портфеля.
/// </summary>
public class RelativeValueServiceTests
{
    private static BasketMember Member(string secid, string sector, decimal duration, decimal gSpread) => new()
    {
        Secid = secid,
        Sector = sector,
        DurationYears = duration,
        GSpreadFraction = gSpread,
    };

    [Fact]
    public void BuildBasketStats_ComputesMedianP25P75_ForBasketWithFiveMembers()
    {
        // Корзина "Корпоративные" × "1–3 года" (все дюрации 2 года) — спреды 0.01..0.05 (доли).
        var members = new[]
        {
            Member("A", "Корпоративные", 2m, 0.01m),
            Member("B", "Корпоративные", 2m, 0.02m),
            Member("C", "Корпоративные", 2m, 0.03m),
            Member("D", "Корпоративные", 2m, 0.04m),
            Member("E", "Корпоративные", 2m, 0.05m),
        };

        var stats = BuildBasketStats(members);
        var key = new BasketKey { Sector = "Корпоративные", DurationBucket = "1–3 года" };

        stats.Should().ContainKey(key);
        stats[key].Count.Should().Be(5);
        stats[key].Median.Should().Be(0.03m);
        stats[key].P25.Should().Be(0.02m);
        stats[key].P75.Should().Be(0.04m);
    }

    [Fact]
    public void BuildBasketStats_ExcludesMembersWithoutGSpread()
    {
        var members = new[]
        {
            Member("A", "Корпоративные", 2m, 0.01m),
            new BasketMember { Secid = "B", Sector = "Корпоративные", DurationYears = 2m, GSpreadFraction = null },
        };

        var stats = BuildBasketStats(members);
        var key = new BasketKey { Sector = "Корпоративные", DurationBucket = "1–3 года" };

        stats[key].Count.Should().Be(1, "бумага без G-спреда не должна попасть в статистику корзины");
    }

    [Fact]
    public void BuildBasketStats_GroupsSeparatelyBySectorAndDurationBucket()
    {
        var members = new[]
        {
            Member("A", "Корпоративные", 0.5m, 0.01m), // 0-1 года
            Member("B", "Корпоративные", 4m, 0.02m),   // 3-5 лет
            Member("C", "Гособлигации", 0.5m, 0.005m), // другой сектор
        };

        var stats = BuildBasketStats(members);

        stats.Should().ContainKey(new BasketKey { Sector = "Корпоративные", DurationBucket = "0–1 года" });
        stats.Should().ContainKey(new BasketKey { Sector = "Корпоративные", DurationBucket = "3–5 лет" });
        stats.Should().ContainKey(new BasketKey { Sector = "Гособлигации", DurationBucket = "0–1 года" });
        stats.Should().HaveCount(3);
    }

    [Fact]
    public void ResolveBasket_NativeBasketWithFivePlusMembers_ReturnsHighConfidence()
    {
        var members = Enumerable.Range(0, 5).Select(i => Member($"S{i}", "Корпоративные", 2m, 0.01m * (i + 1))).ToList();
        var stats = BuildBasketStats(members);
        var key = new BasketKey { Sector = "Корпоративные", DurationBucket = "1–3 года" };

        var resolution = ResolveBasket(key, members, stats);

        resolution.Confidence.Should().Be(RelativeValueConfidence.High);
        resolution.EffectiveBasket.Should().Be(key);
        resolution.Stats.Count.Should().Be(5);
    }

    [Fact]
    public void ResolveBasket_FewerThanMinInNativeBasket_FallsBackToSectorWide_MediumConfidence()
    {
        // Родная корзина "1-3 года" содержит только 2 бумаги, но сектор целиком (разные дюрации) — 6.
        var members = new List<BasketMember>
        {
            Member("A", "Корпоративные", 2m, 0.01m),
            Member("B", "Корпоративные", 2m, 0.015m),
            Member("C", "Корпоративные", 0.5m, 0.02m),
            Member("D", "Корпоративные", 0.5m, 0.025m),
            Member("E", "Корпоративные", 6m, 0.03m),
            Member("F", "Корпоративные", 6m, 0.035m),
        };
        var stats = BuildBasketStats(members);
        var key = new BasketKey { Sector = "Корпоративные", DurationBucket = "1–3 года" };

        var resolution = ResolveBasket(key, members, stats);

        resolution.Confidence.Should().Be(RelativeValueConfidence.Medium);
        resolution.EffectiveBasket.DurationBucket.Should().Be(SectorWideBucketLabel);
        resolution.Stats.Count.Should().Be(6, "сектор целиком, независимо от дюрационного бакета");
    }

    [Fact]
    public void ResolveBasket_FewerThanMinEvenInSector_FallsBackToWholeMarket_LowConfidence()
    {
        var members = new List<BasketMember>
        {
            Member("A", "Корпоративные", 2m, 0.01m),
            Member("B", "Гособлигации", 3m, 0.005m),
            Member("C", "Гособлигации", 4m, 0.006m),
            Member("D", "Гособлигации", 5m, 0.007m),
            Member("E", "Гособлигации", 6m, 0.008m),
        };
        var stats = BuildBasketStats(members);
        var key = new BasketKey { Sector = "Корпоративные", DurationBucket = "1–3 года" };

        var resolution = ResolveBasket(key, members, stats);

        resolution.Confidence.Should().Be(RelativeValueConfidence.Low);
        resolution.EffectiveBasket.Sector.Should().Be(MarketWideLabel);
        resolution.Stats.Count.Should().Be(5, "весь рынок — все члены вселенной с G-спредом");
    }

    [Fact]
    public void Assess_PositiveDeviation_MeansSpreadAboveMedian_Cheap()
    {
        var members = Enumerable.Range(0, 5).Select(i => Member($"S{i}", "Корпоративные", 2m, 0.01m + i * 0.005m)).ToList();
        // Спреды: 0.010, 0.015, 0.020, 0.025, 0.030 — медиана 0.020.
        var stats = BuildBasketStats(members);

        var assessment = Assess("Корпоративные", 2m, bondSpread: 0.05m, members, stats);

        assessment.DeviationFraction.Should().Be(0.03m, "0.05 - медиана 0.02");
        assessment.DeviationFraction.Should().BePositive("спред намного выше медианы корзины — бумага 'дешёвая'");
    }

    [Fact]
    public void Assess_NegativeDeviation_MeansSpreadBelowMedian_Rich()
    {
        var members = Enumerable.Range(0, 5).Select(i => Member($"S{i}", "Корпоративные", 2m, 0.01m + i * 0.005m)).ToList();
        var stats = BuildBasketStats(members);

        var assessment = Assess("Корпоративные", 2m, bondSpread: 0.005m, members, stats);

        assessment.DeviationFraction.Should().BeNegative("спред намного ниже медианы корзины — бумага 'дорогая'");
    }

    [Fact]
    public void Assess_Percentile_HighestSpreadInBasket_Is100()
    {
        var members = Enumerable.Range(0, 5).Select(i => Member($"S{i}", "Корпоративные", 2m, 0.01m * (i + 1))).ToList();
        var stats = BuildBasketStats(members);

        var assessment = Assess("Корпоративные", 2m, bondSpread: 0.10m, members, stats);

        assessment.Percentile.Should().Be(100m, "спред выше всех 5 членов корзины");
    }

    [Fact]
    public void Assess_HiddenBondsMustNotBePassedIn_NotPartOfMembersList()
    {
        // Гигиенический фильтр применяется ДО вызова BuildBasketStats/Assess (план часть A.2) —
        // здесь проверяем, что при исключении "мусорной" бумаги из members медиана не искажается ею.
        var visibleMembers = new[]
        {
            Member("A", "Корпоративные", 2m, 0.01m),
            Member("B", "Корпоративные", 2m, 0.02m),
            Member("C", "Корпоративные", 2m, 0.03m),
            Member("D", "Корпоративные", 2m, 0.04m),
            Member("E", "Корпоративные", 2m, 0.05m),
        };
        var statsWithoutHidden = BuildBasketStats(visibleMembers);

        var membersIncludingHiddenJunk = new List<BasketMember>(visibleMembers)
        {
            Member("JUNK", "Корпоративные", 2m, 5.0m), // преддефолтный мусор — гигиенический фильтр скрыл бы её
        };
        var statsWithHiddenIncluded = BuildBasketStats(membersIncludingHiddenJunk);

        var key = new BasketKey { Sector = "Корпоративные", DurationBucket = "1–3 года" };
        statsWithoutHidden[key].Median.Should().Be(0.03m);
        statsWithHiddenIncluded[key].Median
            .Should().NotBe(0.03m, "если бы мусорная бумага попала в статистику, медиана (по 6 членам) сдвинулась бы");
    }

    [Fact]
    public void BuildBasketStats_NullSector_GroupsUnderUnknownSectorLabel_NotDropped()
    {
        var members = new[]
        {
            new BasketMember { Secid = "A", Sector = null, DurationYears = 2m, GSpreadFraction = 0.01m },
        };

        var stats = BuildBasketStats(members);

        stats.Should().ContainKey(new BasketKey { Sector = UnknownSector, DurationBucket = "1–3 года" });
    }
}

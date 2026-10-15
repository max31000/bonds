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

    // ─── Задача 44: IQR-порог вердикта, Confidence-гейт, бакет по Маколею ───────────────────────

    /// <summary>Корзина из 5 бумаг одного бакета (1–3 года) с заданными спредами (доли).</summary>
    private static (List<BasketMember> Members, Dictionary<BasketKey, BasketStats> Stats) BasketWithSpreads(params decimal[] spreads)
    {
        var members = spreads.Select((sp, i) => Member($"S{i}", "Корпоративные", 2m, sp)).ToList();
        return (members, BuildBasketStats(members));
    }

    [Fact]
    public void ThresholdFor_NarrowBasket_IqrTenBp_FloorsAtTwentyBp()
    {
        // Спреды 290/295/300/305/310 б.п.: P25 = 295, P75 = 305 → IQR = 10 б.п. → 0.25×10 = 2.5 б.п. < 20 → порог 20 б.п.
        var (_, stats) = BasketWithSpreads(0.0290m, 0.0295m, 0.0300m, 0.0305m, 0.0310m);
        var basket = stats.Values.Single();

        basket.P75.Should().Be(0.0305m);
        basket.P25.Should().Be(0.0295m);
        ThresholdFor(basket).Should().Be(0.0020m);
    }

    [Fact]
    public void ThresholdFor_WideBasket_IqrTwoHundredBp_IsFiftyBp()
    {
        // Спреды 0/100/200/300/400 б.п.: P25 = 100, P75 = 300 → IQR = 200 б.п. → 0.25×200 = 50 б.п. > 20.
        var (_, stats) = BasketWithSpreads(0.0000m, 0.0100m, 0.0200m, 0.0300m, 0.0400m);

        ThresholdFor(stats.Values.Single()).Should().Be(0.0050m);
    }

    [Theory]
    // Узкая корзина (медиана 0.0300, порог 20 б.п.): +25 б.п. → Cheap, +15 б.п. → Fair, −25 б.п. → Rich.
    [InlineData(0.0325, "Cheap")]
    [InlineData(0.0315, "Fair")]
    [InlineData(0.0275, "Rich")]
    [InlineData(0.0320, "Fair")] // ровно на пороге (+20 б.п.) — строгое неравенство, ещё Fair.
    public void Assess_NarrowBasket_UsesTwentyBpFloor(double bondSpread, string expected)
    {
        var (members, stats) = BasketWithSpreads(0.0290m, 0.0295m, 0.0300m, 0.0305m, 0.0310m);

        var assessment = Assess("Корпоративные", 2m, (decimal)bondSpread, members, stats);

        assessment.ThresholdFraction.Should().Be(0.0020m);
        assessment.Verdict.ToString().Should().Be(expected);
    }

    [Theory]
    // Широкая корзина (медиана 0.0200, порог 50 б.п.): +40 б.п. → Fair (при плоском 20 б.п. было бы Cheap),
    // +60 б.п. → Cheap, −60 б.п. → Rich, −50 б.п. (на пороге) → Fair.
    [InlineData(0.0240, "Fair")]
    [InlineData(0.0260, "Cheap")]
    [InlineData(0.0140, "Rich")]
    [InlineData(0.0150, "Fair")]
    public void Assess_WideBasket_ThresholdScalesWithIqr(double bondSpread, string expected)
    {
        var (members, stats) = BasketWithSpreads(0.0000m, 0.0100m, 0.0200m, 0.0300m, 0.0400m);

        var assessment = Assess("Корпоративные", 2m, (decimal)bondSpread, members, stats);

        assessment.ThresholdFraction.Should().Be(0.0050m);
        assessment.Verdict.ToString().Should().Be(expected);
    }

    [Fact]
    public void Assess_LowConfidenceWholeMarketFallback_VerdictIsInsufficientData_NotCheapOrRich()
    {
        // Родная корзина и сектор — по 1 бумаге (< MinBasketSize), fallback на весь рынок → Confidence Low.
        var members = new List<BasketMember>
        {
            Member("A", "Корпоративные", 2m, 0.0100m),
            Member("B", "Гособлигации", 3m, 0.0050m),
            Member("C", "Гособлигации", 4m, 0.0060m),
            Member("D", "Гособлигации", 5m, 0.0070m),
            Member("E", "Гособлигации", 6m, 0.0080m),
        };
        var stats = BuildBasketStats(members);

        var assessment = Assess("Корпоративные", 2m, bondSpread: 0.0900m, members, stats);

        assessment.Basket.Confidence.Should().Be(RelativeValueConfidence.Low);
        assessment.DeviationFraction.Should().BeGreaterThan(assessment.ThresholdFraction, "отклонение огромное — но вердикта нет");
        assessment.Verdict.Should().Be(RelativeValueVerdict.InsufficientData);
    }

    [Fact]
    public void Assess_MediumConfidenceSectorFallback_StillIssuesVerdict()
    {
        // Родная корзина "1–3 года" — 2 бумаги; сектор целиком — 6 → Medium; вердикт выдаётся.
        var members = new List<BasketMember>
        {
            Member("A", "Корпоративные", 2m, 0.0100m),
            Member("B", "Корпоративные", 2m, 0.0150m),
            Member("C", "Корпоративные", 0.5m, 0.0200m),
            Member("D", "Корпоративные", 0.5m, 0.0250m),
            Member("E", "Корпоративные", 6m, 0.0300m),
            Member("F", "Корпоративные", 6m, 0.0350m),
        };
        var stats = BuildBasketStats(members);

        var assessment = Assess("Корпоративные", 2m, bondSpread: 0.0900m, members, stats);

        assessment.Basket.Confidence.Should().Be(RelativeValueConfidence.Medium);
        assessment.Verdict.Should().Be(RelativeValueVerdict.Cheap);
    }

    [Fact]
    public void Assess_PositionOnBucketBoundary_MacaulayLandsInSameBasketAsEqualBankBond()
    {
        // Эталон границы: Маколей 3.1 / Modified 2.8 при границе корзин 3 года. Банк хранит Маколея
        // (MOEX DURATION), поэтому позиция должна быть в корзине "3–5 лет", где лежит равная ей бумага
        // банка (Маколей 3.1), а не в "1–3 года" (куда её отправляла модифицированная дюрация).
        var bank = new List<BasketMember>
        {
            Member("A", "Корпоративные", 3.1m, 0.0100m),
            Member("B", "Корпоративные", 3.2m, 0.0150m),
            Member("C", "Корпоративные", 3.5m, 0.0200m),
            Member("D", "Корпоративные", 4.0m, 0.0250m),
            Member("E", "Корпоративные", 4.5m, 0.0300m),
            Member("X", "Корпоративные", 2.0m, 0.0900m),
        };
        var stats = BuildBasketStats(bank);

        var byMacaulay = Assess("Корпоративные", 3.1m, 0.0200m, bank, stats);
        var byModified = Assess("Корпоративные", 2.8m, 0.0200m, bank, stats);

        byMacaulay.Basket.EffectiveBasket.DurationBucket.Should().Be("3–5 лет");
        byMacaulay.Basket.EffectiveBasket.DurationBucket
            .Should().Be(DurationBucketClassifier.Label(bank[0].DurationYears), "тот же бакет, что у равной бумаги банка");
        byModified.Basket.EffectiveBasket.DurationBucket.Should().NotBe("3–5 лет", "это и был дефект: modified у границы уводил в другую корзину");
    }
}

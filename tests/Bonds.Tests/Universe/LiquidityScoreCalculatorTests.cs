using Bonds.Core.Universe;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Universe;

/// <summary>Задача 26 часть C.2 — скор ликвидности банка облигаций по обороту/спреду/числу сделок.</summary>
public class LiquidityScoreCalculatorTests
{
    [Fact]
    public void Assess_HighTurnoverTightSpread_ReturnsHigh()
    {
        // Оборот > 5 млн, спред (100.15-99.85)/100 = 0.3% ровно на грани — берём чуть уже, чтобы точно попасть под порог.
        var result = LiquidityScoreCalculator.Assess(turnoverRub: 10_000_000m, bidPercent: 99.9m, offerPercent: 100.1m, numTrades: 50);

        result.Score.Should().Be(LiquidityScore.High);
        result.SlippageEstimateFraction.Should().NotBeNull();
    }

    [Fact]
    public void Assess_MediumTurnover_WideSpreadForHigh_ReturnsMedium()
    {
        // Оборот выше High-порога, но спред 1% — шире порога High (0.3%), но уже порога Medium
        // (1.5%) — должен упасть в Medium, а не в High.
        var result = LiquidityScoreCalculator.Assess(turnoverRub: 10_000_000m, bidPercent: 99.5m, offerPercent: 100.5m, numTrades: 50);

        result.Score.Should().Be(LiquidityScore.Medium);
    }

    [Fact]
    public void Assess_LowTurnoverBelowMedium_WithTrades_ReturnsLow()
    {
        var result = LiquidityScoreCalculator.Assess(turnoverRub: 50_000m, bidPercent: 95m, offerPercent: 105m, numTrades: 3);

        result.Score.Should().Be(LiquidityScore.Low);
    }

    [Fact]
    public void Assess_ZeroTurnoverNoTradesNoQuotes_ReturnsNone()
    {
        var result = LiquidityScoreCalculator.Assess(turnoverRub: null, bidPercent: null, offerPercent: null, numTrades: null);

        result.Score.Should().Be(LiquidityScore.None);
        result.SlippageEstimateFraction.Should().BeNull();
    }

    [Fact]
    public void Assess_MissingBidOrOffer_SlippageEstimateIsNull()
    {
        var result = LiquidityScoreCalculator.Assess(turnoverRub: 1_000_000m, bidPercent: 99m, offerPercent: null, numTrades: 5);

        result.SlippageEstimateFraction.Should().BeNull();
    }

    [Fact]
    public void Assess_SlippageEstimate_IsHalfOfSpreadFraction()
    {
        // bid=99, offer=101 -> mid=100, spread=(101-99)/100=0.02 -> slippage=0.01
        var result = LiquidityScoreCalculator.Assess(turnoverRub: 50_000m, bidPercent: 99m, offerPercent: 101m, numTrades: 1);

        result.SlippageEstimateFraction.Should().Be(0.01m);
    }

    [Fact]
    public void Assess_TurnoverZero_ButHasQuotes_IsNotNone()
    {
        // Оборота нет (не торговали сегодня), но котировки bid/offer выставлены — не "None", это Low.
        var result = LiquidityScoreCalculator.Assess(turnoverRub: 0m, bidPercent: 95m, offerPercent: 105m, numTrades: null);

        result.Score.Should().Be(LiquidityScore.Low);
    }

    // ─── Задача 44: стакан без данных ≠ Red; медиана оборота ────────────────────────────────────

    [Fact]
    public void Assess_HugeTurnover_NoBidOffer_IsMedium_NotLow()
    {
        // Оборот 300 млн ₽, стакана в снимке нет → раньше Low (→ Caution → Red), теперь Medium.
        var result = LiquidityScoreCalculator.Assess(turnoverRub: 300_000_000m, bidPercent: null, offerPercent: null, numTrades: null);

        result.Score.Should().Be(LiquidityScore.Medium);
        result.BookSpreadFraction.Should().BeNull("стакан не виден — спред неизвестен, не нулевой");
    }

    [Fact]
    public void Assess_High_RequiresKnownTightSpread()
    {
        LiquidityScoreCalculator.Assess(10_000_000m, null, null, 50).Score
            .Should().Be(LiquidityScore.Medium, "High требует ИЗВЕСТНОГО спреда < 0.3%");
        LiquidityScoreCalculator.Assess(10_000_000m, 99.9m, 100.1m, 50).Score.Should().Be(LiquidityScore.High);
    }

    [Fact]
    public void Assess_KnownWideSpread_StaysLow_EvenWithTurnover()
    {
        // bid=98, offer=100.2 → спред 2.2/99.1 ≈ 2.2% ≥ 1.5%: известно, что стакан тонкий → Low.
        var result = LiquidityScoreCalculator.Assess(50_000_000m, 98m, 100.2m, 20);

        result.Score.Should().Be(LiquidityScore.Low);
        result.BookSpreadFraction.Should().BeApproximately(0.0222m, 0.0001m);
    }

    [Fact]
    public void Assess_SmallTurnover_NoBook_IsLow()
    {
        // 100 тыс. ₽ < 300 тыс. порога Medium, стакана нет → Low (что-то торгуется).
        LiquidityScoreCalculator.Assess(100_000m, null, null, null).Score.Should().Be(LiquidityScore.Low);
    }

    [Fact]
    public void MedianTurnover_OddAndEvenCounts_SkipsNulls()
    {
        LiquidityScoreCalculator.MedianTurnover(new decimal?[] { 5m, null, 1m, 3m }).Should().Be(3m);
        LiquidityScoreCalculator.MedianTurnover(new decimal?[] { 4m, 2m, 10m, 6m }).Should().Be(5m, "чётное число — среднее двух центральных (4 и 6)");
        LiquidityScoreCalculator.MedianTurnover(new decimal?[] { null, null }).Should().BeNull();
    }

    [Fact]
    public void Assess_MedianTurnoverFromHistory_MorningAndEveningRunGiveSameScore()
    {
        // История за 5 дней: оборот 200–400 млн/день. Утром VALTODAY «на сейчас» = 40 тыс. ₽, вечером 300 млн.
        var median = LiquidityScoreCalculator.MedianTurnover(new decimal?[] { 250_000_000m, 300_000_000m, 400_000_000m, 200_000_000m, 320_000_000m });

        var morningByToday = LiquidityScoreCalculator.Assess(40_000m, 99.9m, 100.1m, 3).Score;
        var morningByMedian = LiquidityScoreCalculator.Assess(median, 99.9m, 100.1m, 3).Score;
        var eveningByMedian = LiquidityScoreCalculator.Assess(median, 99.9m, 100.1m, 400).Score;

        morningByToday.Should().Be(LiquidityScore.Low, "по сегодняшнему VALTODAY утром оценка занижена — это и был дефект");
        morningByMedian.Should().Be(LiquidityScore.High);
        eveningByMedian.Should().Be(morningByMedian);
    }

    [Fact]
    public void HalfSpreadFraction_IsOfferMinusBidOverOfferPlusBid_AndMatchesAssessSlippage()
    {
        // bid 99.8, offer 100.2: (100.2 − 99.8)/(100.2 + 99.8) = 0.4/200 = 0.002 (полуспред, 20 б.п.).
        LiquidityScoreCalculator.HalfSpreadFraction(99.8m, 100.2m).Should().BeApproximately(0.002m, 1e-12m);
        LiquidityScoreCalculator.Assess(1_000_000m, 99.8m, 100.2m, 5).SlippageEstimateFraction
            .Should().Be(LiquidityScoreCalculator.HalfSpreadFraction(99.8m, 100.2m), "единый источник формулы (задача 48)");
        LiquidityScoreCalculator.HalfSpreadFraction(null, 100.2m).Should().BeNull();
        LiquidityScoreCalculator.HalfSpreadFraction(0m, 100.2m).Should().BeNull();
    }
}

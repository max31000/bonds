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
}

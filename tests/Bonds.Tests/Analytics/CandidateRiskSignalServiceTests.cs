using Bonds.Core.Analytics;
using Bonds.Core.Universe;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Analytics;

/// <summary>
/// Задача 33 часть C.1 — эталонная матрица ликвидность×листинг → SignalLevel и пороги отклонения
/// спреда от медианы корзины (0.0020 = 20 б.п., согласовано с RV-порогом Fair-вердикта). Все
/// числа — доли (контракт репо).
/// </summary>
public class CandidateRiskSignalServiceTests
{
    // ─── Матрица ликвидность×листинг ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(LiquidityScore.High, null, SignalLevel.Good)]
    [InlineData(LiquidityScore.High, 1, SignalLevel.Good)]
    [InlineData(LiquidityScore.High, 2, SignalLevel.Good)]
    [InlineData(LiquidityScore.High, 3, SignalLevel.Neutral)]
    [InlineData(LiquidityScore.Medium, null, SignalLevel.Neutral)]
    [InlineData(LiquidityScore.Medium, 1, SignalLevel.Neutral)]
    [InlineData(LiquidityScore.Medium, 2, SignalLevel.Neutral)]
    [InlineData(LiquidityScore.Medium, 3, SignalLevel.Caution)]
    [InlineData(LiquidityScore.Low, null, SignalLevel.Caution)]
    [InlineData(LiquidityScore.Low, 1, SignalLevel.Caution)]
    [InlineData(LiquidityScore.Low, 3, SignalLevel.Caution)]
    [InlineData(LiquidityScore.None, null, SignalLevel.Neutral)]
    [InlineData(LiquidityScore.None, 1, SignalLevel.Neutral)]
    [InlineData(LiquidityScore.None, 3, SignalLevel.Neutral)]
    public void AssessLiquidity_MatchesMatrix(LiquidityScore score, int? listLevel, SignalLevel expected)
    {
        var (level, _) = CandidateRiskSignalService.AssessLiquidity(score, listLevel);

        level.Should().Be(expected);
    }

    [Fact]
    public void AssessLiquidity_High_ProducesDescriptiveLabelWithListLevel()
    {
        var (_, label) = CandidateRiskSignalService.AssessLiquidity(LiquidityScore.High, 1);

        label.Should().Be("Высокая ликвидность, листинг 1");
        label.Should().NotContainEquivalentOf("надёжно", "риск-сигнал не должен выдаваться за оценку надёжности эмитента");
    }

    [Fact]
    public void AssessLiquidity_Low_ProducesDescriptiveLabelWithListLevel3()
    {
        var (_, label) = CandidateRiskSignalService.AssessLiquidity(LiquidityScore.Low, 3);

        label.Should().Be("Низкий оборот, листинг 3");
    }

    [Fact]
    public void AssessLiquidity_NullListLevel_OmitsListingFromLabel()
    {
        var (_, label) = CandidateRiskSignalService.AssessLiquidity(LiquidityScore.Medium, null);

        label.Should().Be("Средняя ликвидность");
    }

    [Fact]
    public void AssessLiquidity_None_ProducesInsufficientDataLabel()
    {
        var (_, label) = CandidateRiskSignalService.AssessLiquidity(LiquidityScore.None, null);

        label.Should().Be("Недостаточно данных по ликвидности");
    }

    // ─── Спред vs медиана корзины ────────────────────────────────────────────────────────────

    [Fact]
    public void AssessSpread_AboveMedianByMoreThanThreshold_IsCaution()
    {
        // 0.05 - 0.03 = 0.0200 > 0.0020 порог.
        var level = CandidateRiskSignalService.AssessSpread(gSpreadFraction: 0.05m, basketMedianGSpreadFraction: 0.03m);

        level.Should().Be(SignalLevel.Caution);
    }

    [Fact]
    public void AssessSpread_BelowMedianByMoreThanThreshold_IsGood()
    {
        // 0.01 - 0.03 = -0.0200 < -0.0020 порог.
        var level = CandidateRiskSignalService.AssessSpread(gSpreadFraction: 0.01m, basketMedianGSpreadFraction: 0.03m);

        level.Should().Be(SignalLevel.Good);
    }

    [Fact]
    public void AssessSpread_WithinThreshold_IsNeutral()
    {
        // 0.0310 - 0.0300 = 0.0010 < 0.0020 порог (эталон — ровно половина порога).
        var level = CandidateRiskSignalService.AssessSpread(gSpreadFraction: 0.0310m, basketMedianGSpreadFraction: 0.0300m);

        level.Should().Be(SignalLevel.Neutral);
    }

    [Fact]
    public void AssessSpread_ExactlyAtThreshold_IsNeutral()
    {
        // Порог — строгое неравенство (план: "заметно выше/ниже"), ровно на пороге — ещё Neutral.
        var level = CandidateRiskSignalService.AssessSpread(gSpreadFraction: 0.0320m, basketMedianGSpreadFraction: 0.0300m);

        level.Should().Be(SignalLevel.Neutral);
    }

    [Fact]
    public void AssessSpread_NullGSpread_IsNeutral()
    {
        var level = CandidateRiskSignalService.AssessSpread(gSpreadFraction: null, basketMedianGSpreadFraction: 0.03m);

        level.Should().Be(SignalLevel.Neutral);
    }

    [Fact]
    public void AssessSpread_NullBasketMedian_IsNeutral()
    {
        var level = CandidateRiskSignalService.AssessSpread(gSpreadFraction: 0.05m, basketMedianGSpreadFraction: null);

        level.Should().Be(SignalLevel.Neutral);
    }

    // ─── Assess (сборка обоих сигналов) ─────────────────────────────────────────────────────

    [Fact]
    public void Assess_NullGSpread_ReturnsNeutralSpreadWithNullValues()
    {
        var signals = CandidateRiskSignalService.Assess(
            liquidityScore: LiquidityScore.High,
            listLevel: 1,
            gSpreadFraction: null,
            basketMedianGSpreadFraction: 0.03m);

        signals.Spread.Should().Be(SignalLevel.Neutral);
        signals.GSpreadFraction.Should().BeNull();
        signals.SpreadVsBasketMedianFraction.Should().BeNull();
    }

    [Fact]
    public void Assess_FullData_ComputesBothSignalsAndDeviation()
    {
        var signals = CandidateRiskSignalService.Assess(
            liquidityScore: LiquidityScore.High,
            listLevel: 1,
            gSpreadFraction: 0.05m,
            basketMedianGSpreadFraction: 0.03m);

        signals.Liquidity.Should().Be(SignalLevel.Good);
        signals.LiquidityLabel.Should().Be("Высокая ликвидность, листинг 1");
        signals.Spread.Should().Be(SignalLevel.Caution);
        signals.GSpreadFraction.Should().Be(0.05m);
        signals.SpreadVsBasketMedianFraction.Should().Be(0.02m);
    }

    [Fact]
    public void Assess_NoneLiquidityAndNullSpread_BothSignalsNeutral_NotCaution()
    {
        // План часть A.1: null-данные (нет спреда/ликвидности) → сигнал Neutral, не Caution.
        var signals = CandidateRiskSignalService.Assess(
            liquidityScore: LiquidityScore.None,
            listLevel: null,
            gSpreadFraction: null,
            basketMedianGSpreadFraction: null);

        signals.Liquidity.Should().Be(SignalLevel.Neutral);
        signals.Spread.Should().Be(SignalLevel.Neutral);
    }

    // ─── Задача 44: порог оси спреда от IQR корзины; подпись «тонкий стакан» vs «стакан не виден» ──

    [Fact]
    public void AssessSpread_WithWideBasketThreshold_FortyBpDeviationIsNeutral_NotCaution()
    {
        // Плоский порог 20 б.п. дал бы Caution на +40 б.п.; в широкой корзине порог 50 б.п. → Neutral.
        CandidateRiskSignalService.AssessSpread(0.0240m, 0.0200m).Should().Be(SignalLevel.Caution);
        CandidateRiskSignalService.AssessSpread(0.0240m, 0.0200m, thresholdFraction: 0.0050m).Should().Be(SignalLevel.Neutral);
        CandidateRiskSignalService.AssessSpread(0.0260m, 0.0200m, thresholdFraction: 0.0050m).Should().Be(SignalLevel.Caution);
        CandidateRiskSignalService.AssessSpread(0.0140m, 0.0200m, thresholdFraction: 0.0050m).Should().Be(SignalLevel.Good);
    }

    [Fact]
    public void AssessLiquidity_Labels_DistinguishThinBookFromBookNotVisible()
    {
        CandidateRiskSignalService.AssessLiquidity(LiquidityScore.Low, 1, bookSpreadFraction: 0.021m).Label
            .Should().Be("Низкая ликвидность, тонкий стакан 2.1%, листинг 1");

        CandidateRiskSignalService.AssessLiquidity(LiquidityScore.Medium, 1, bookSpreadFraction: null, bookMissing: true).Label
            .Should().Be("Средняя ликвидность, стакан не виден в снимке, листинг 1");

        CandidateRiskSignalService.AssessLiquidity(LiquidityScore.Low, null, bookSpreadFraction: null, bookMissing: true).Label
            .Should().Be("Низкий оборот, стакан не виден в снимке");

        CandidateRiskSignalService.AssessLiquidity(LiquidityScore.Medium, 2, bookSpreadFraction: 0.01m).Label
            .Should().Be("Средняя ликвидность, листинг 2", "известный нормальный спред — подпись без пометок");
    }

    [Fact]
    public void Assess_LiquidBondWithoutBook_IsNotCautionOnLiquidityAxis()
    {
        // Раньше оборот 300 млн без стакана → Low → Caution → Red. Теперь Medium → Neutral (листинг 1). Светофор — скоркарта (ReliabilityScoreServiceTests).
        var liquidity = Bonds.Core.Universe.LiquidityScoreCalculator.Assess(300_000_000m, null, null, null);

        var signals = CandidateRiskSignalService.Assess(
            liquidity.Score, listLevel: 1, gSpreadFraction: 0.03m, basketMedianGSpreadFraction: 0.03m,
            spreadThresholdFraction: 0.002m, bookSpreadFraction: liquidity.BookSpreadFraction, bookMissing: true);

        signals.Liquidity.Should().Be(SignalLevel.Neutral);
        signals.LiquidityLabel.Should().Contain("стакан не виден");
    }
}

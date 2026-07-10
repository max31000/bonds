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

    // ─── Aggregate — светофор надёжности (задача 38 часть A.1) ─────────────────────────────────
    //
    // Матрица (см. doc-comment CandidateRiskSignalService.Aggregate): Red — любой Caution (спред
    // Caution гос/муниципального сектора не считается, суверенный кредит); Green — обе оси
    // Good/Neutral, ни одного Caution, листинг ∈ {1,2}, известная ликвидность (raw != None), ЛИБО
    // гос/муниципальный сектор при любом непро-Caution состоянии спреда (спред игнорируется); Yellow
    // — всё остальное (недостаточно данных по ликвидности, листинг вне {1,2}/неизвестен).

    private static CandidateRiskSignals BuildSignals(
        LiquidityScore liquidityScore, int? listLevel, decimal? gSpreadFraction, decimal? basketMedianGSpreadFraction) =>
        CandidateRiskSignalService.Assess(liquidityScore, listLevel, gSpreadFraction, basketMedianGSpreadFraction);

    [Theory]
    // ── Green: чистые случаи (не суверен) ──
    [InlineData(LiquidityScore.High, 1, 0.031, 0.03, null, ReliabilityLight.Green)]
    [InlineData(LiquidityScore.High, 2, 0.01, 0.03, null, ReliabilityLight.Green)]
    [InlineData(LiquidityScore.Medium, 1, 0.031, 0.03, null, ReliabilityLight.Green)] // Neutral+Neutral допустим для Green.
    [InlineData(LiquidityScore.Medium, 2, 0.01, 0.03, null, ReliabilityLight.Green)]
    [InlineData(LiquidityScore.High, 1, null, null, null, ReliabilityLight.Green)] // null-спред один (без null-ликвидности) не блокирует Green.
    [InlineData(LiquidityScore.High, 1, 0.031, 0.03, "Корпоративные", ReliabilityLight.Green)]
    // ── Red: любой Caution ──
    [InlineData(LiquidityScore.Low, 1, 0.01, 0.03, null, ReliabilityLight.Red)] // ликвидность Caution.
    [InlineData(LiquidityScore.Low, null, null, null, null, ReliabilityLight.Red)] // Caution даже без остальных данных.
    [InlineData(LiquidityScore.High, 1, 0.05, 0.03, null, ReliabilityLight.Red)] // спред Caution, не суверен.
    [InlineData(LiquidityScore.Medium, 3, 0.031, 0.03, null, ReliabilityLight.Red)] // Medium+листинг3 → Caution ликвидности.
    [InlineData(LiquidityScore.Low, 1, 0.05, 0.03, null, ReliabilityLight.Red)] // оба Caution.
    [InlineData(LiquidityScore.High, 1, 0.05, 0.03, "Корпоративные", ReliabilityLight.Red)] // некорректный "суверенный" ярлык не защищает — сектор не гос/муниц.
    // ── Yellow: нехватка данных / листинг вне 1-2 ──
    [InlineData(LiquidityScore.None, null, null, null, null, ReliabilityLight.Yellow)] // null-спред И null-ликвидность одновременно → Yellow, не Red.
    [InlineData(LiquidityScore.None, 1, 0.031, 0.03, null, ReliabilityLight.Yellow)] // ликвидность неизвестна, остальное в норме.
    [InlineData(LiquidityScore.High, 3, 0.031, 0.03, null, ReliabilityLight.Yellow)] // листинг 3 вне {1,2} (сигнал уже не Caution — High+листинг3=Neutral).
    [InlineData(LiquidityScore.High, null, 0.031, 0.03, null, ReliabilityLight.Yellow)] // листинг неизвестен.
    // ── Суверенный сектор — исключение только для спреда ──
    [InlineData(LiquidityScore.High, 1, 0.05, 0.03, "Гособлигации", ReliabilityLight.Green)] // спред Caution проигнорирован — суверенный кредит.
    [InlineData(LiquidityScore.High, 1, 0.05, 0.03, "Муниципальные", ReliabilityLight.Green)]
    [InlineData(LiquidityScore.Low, 1, 0.05, 0.03, "Гособлигации", ReliabilityLight.Red)] // сектор НЕ спасает ликвидность.
    [InlineData(LiquidityScore.High, 3, 0.05, 0.03, "Гособлигации", ReliabilityLight.Yellow)] // спред бесплатен, но листинг 3 всё ещё вне {1,2}.
    [InlineData(LiquidityScore.None, 1, 0.05, 0.03, "Гособлигации", ReliabilityLight.Yellow)] // сектор НЕ спасает нехватку данных по ликвидности.
    public void Aggregate_MatchesMatrix(
        LiquidityScore liquidityScore, int? listLevel, double? gSpreadFraction, double? basketMedianGSpreadFraction,
        string? sector, ReliabilityLight expected)
    {
        // xUnit [InlineData] не поддерживает decimal-константы атрибутов (не valid attribute
        // argument type в C#) — Theory-параметры double, конвертация в decimal здесь на границе теста.
        var signals = BuildSignals(liquidityScore, listLevel, (decimal?)gSpreadFraction, (decimal?)basketMedianGSpreadFraction);

        var (level, reason) = CandidateRiskSignalService.Aggregate(signals, listLevel, sector);

        level.Should().Be(expected);
        reason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Aggregate_ReasonText_NeverClaimsCreditRating()
    {
        // Владелец явно запретил выдавать светофор за кредитный рейтинг — ни в одном исходе
        // reason не должен содержать слово "рейтинг" (см. план: "НЕ называть светофор рейтингом").
        var green = CandidateRiskSignalService.Aggregate(BuildSignals(LiquidityScore.High, 1, 0.01m, 0.03m), 1, null);
        var yellow = CandidateRiskSignalService.Aggregate(BuildSignals(LiquidityScore.None, null, null, null), null, null);
        var red = CandidateRiskSignalService.Aggregate(BuildSignals(LiquidityScore.Low, 1, 0.01m, 0.03m), 1, null);

        foreach (var (_, reason) in new[] { green, yellow, red })
        {
            reason.Should().NotContainEquivalentOf("рейтинг");
        }
    }

    [Fact]
    public void Aggregate_RedReason_MentionsLiquidityWhenLiquidityCausesIt()
    {
        var signals = BuildSignals(LiquidityScore.Low, 1, 0.01m, 0.03m);

        var (level, reason) = CandidateRiskSignalService.Aggregate(signals, 1, null);

        level.Should().Be(ReliabilityLight.Red);
        reason.Should().ContainEquivalentOf("ликвидн");
    }

    [Fact]
    public void Aggregate_RedReason_MentionsSpreadWhenSpreadCausesIt()
    {
        var signals = BuildSignals(LiquidityScore.High, 1, 0.05m, 0.03m);

        var (level, reason) = CandidateRiskSignalService.Aggregate(signals, 1, null);

        level.Should().Be(ReliabilityLight.Red);
        reason.Should().ContainEquivalentOf("спред");
    }
}

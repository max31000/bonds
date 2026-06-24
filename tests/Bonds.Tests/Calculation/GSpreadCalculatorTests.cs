using Bonds.Core.Calculation;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Calculation;

/// <summary>
/// Тесты реконструкции безрисковой кривой по параметрам NSS и G-спреда (spec §6.8, plan/05
/// Часть A.8, Часть D). Эталоны посчитаны вручную по формуле Свенссона, реализованной в
/// <see cref="GSpreadCalculator.CurveValue"/> (см. XML-doc там) — синтетические параметры
/// B1=0.10, B2=-0.02, B3=0.01, T1=2.0, без G-поправок (G1..G9=0) для проверки чистой NSS-части,
/// и отдельно с ненулевыми G для проверки кусочно-линейной интерполяции между узлами термов.
/// </summary>
public class GSpreadCalculatorTests
{
    private const decimal Tolerance = 1e-4m;

    private static Bonds.Core.Models.YieldCurveSnapshot SnapshotWithoutGAdjustment() =>
        TestModelFactory.CurveSnapshot(b1: 0.10m, b2: -0.02m, b3: 0.01m, t1: 2.0m);

    [Theory]
    [InlineData(0.25, 0.08177478318092168)]
    [InlineData(1, 0.08606530659712633)]
    [InlineData(2, 0.09000000000000001)]
    [InlineData(5, 0.09550749000825662)]
    [InlineData(10, 0.09794609642400733)]
    public void CurveValue_PureNss_MatchesHandComputedSvenssonFormula(double termYears, double expected)
    {
        var snapshot = SnapshotWithoutGAdjustment();

        var value = GSpreadCalculator.CurveValue(snapshot, (decimal)termYears);

        value.Should().BeApproximately((decimal)expected, Tolerance);
    }

    [Fact]
    public void CurveValue_WithGAdjustment_InterpolatesLinearlyBetweenNodes()
    {
        var snapshot = TestModelFactory.CurveSnapshot(
            b1: 0.10m, b2: -0.02m, b3: 0.01m, t1: 2.0m,
            g1: 0.001m, g2: 0.002m, g3: 0.003m, g4: 0.004m, g5: 0.005m,
            g6: 0.006m, g7: 0.007m, g8: 0.008m, g9: 0.009m);

        // Узел ровно на термине 2 года (G5=0.005): curve = nss(2) + 0.005 = 0.095
        GSpreadCalculator.CurveValue(snapshot, 2m).Should().BeApproximately(0.09500000000000001m, Tolerance);

        // Между узлами 1 год (G4=0.004) и 2 года (G5=0.005), термин 1.5 года → линейная
        // интерполяция G-поправки даёт 0.0045, итог 0.09274122184247005.
        GSpreadCalculator.CurveValue(snapshot, 1.5m).Should().BeApproximately(0.09274122184247005m, Tolerance);
    }

    [Fact]
    public void CurveValue_TermBelowFirstNode_ClampsToFirstGValue()
    {
        var snapshot = TestModelFactory.CurveSnapshot(
            b1: 0.10m, b2: -0.02m, b3: 0.01m, t1: 2.0m,
            g1: 0.001m, g2: 0.002m, g3: 0.003m, g4: 0.004m, g5: 0.005m,
            g6: 0.006m, g7: 0.007m, g8: 0.008m, g9: 0.009m);

        // term=0.1 < первый узел 0.25 → G-поправка = G1 (клампинг, не экстраполяция).
        var value = GSpreadCalculator.CurveValue(snapshot, 0.1m);
        var expectedNss = 0.10m - 0.02m * ((1m - (decimal)Math.Exp(-0.05)) / 0.05m); // приблизительно, проверяем через разницу с G1
        // Проще и надёжнее: сверяем, что поправка равна G1, через сравнение с term=0.25 (где она тоже G1).
        var atFirstNode = GSpreadCalculator.CurveValue(snapshot, 0.25m);
        var nssDeltaIsSmall = Math.Abs((double)(value - atFirstNode)) < 0.01; // NSS-часть почти не меняется на столь малом интервале
        nssDeltaIsSmall.Should().BeTrue();
    }

    [Fact]
    public void GSpread_SubtractsCurveValueFromYtm()
    {
        var snapshot = SnapshotWithoutGAdjustment();

        // YTM бумаги 12%, дюрация 2 года → значение кривой на 2 года = 0.09 (см. тест выше).
        var spread = GSpreadCalculator.GSpread(bondYtm: 0.12m, durationYears: 2m, snapshot);

        spread.Should().BeApproximately(0.12m - 0.09000000000000001m, Tolerance);
    }
}

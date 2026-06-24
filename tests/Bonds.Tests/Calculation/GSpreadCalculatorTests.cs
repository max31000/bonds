using Bonds.Core.Calculation;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Calculation;

/// <summary>
/// Тесты реконструкции безрисковой кривой по официальной формуле методики MOEX
/// ("Методика определения кривой бескупонной доходности государственных облигаций (облигаций
/// федеральных займов)", утв. 07.07.2017, Протокол №40, раздел 4) и расчёта G-спреда (spec
/// §6.8, plan/05 Часть A.8, Часть D).
/// <para>
/// Эталоны посчитаны вручную (Python-скрипт, не код проекта) по формуле раздела 4.1-4.2:
/// <c>G(t) = β0 + (β1+β2)·(τ/t)·[1-exp(-t/τ)] - β2·exp(-t/τ) + Σgi·exp(-(t-ai)²/bi²)</c> (б.п.),
/// <c>Y(t) = exp(G(t)/10000) - 1</c> (годовая капитализация, в долях). Параметры B1/B2/B3
/// заданы в МАСШТАБЕ БАЗИСНЫХ ПУНКТОВ (как в реальных снимках MOEX, напр.
/// tests/Bonds.Tests/Fixtures/Moex/zcyc_gcurve.json, где B1~1521), а не в долях — это
/// сознательный выбор тестовых данных, чтобы поймать класс ошибок единиц измерения, не
/// пойманный предыдущей версией тестов (которая использовала B1=0.10 в долевом масштабе и
/// тем самым маскировала баг конвертации б.п.→доли, см. история этого файла).
/// </para>
/// </summary>
public class GSpreadCalculatorTests
{
    private const decimal Tolerance = 1e-4m;

    // Синтетические, но реалистичные по масштабу (б.п.) параметры NSS без G-поправок.
    private static Bonds.Core.Models.YieldCurveSnapshot SnapshotWithoutGAdjustment() =>
        TestModelFactory.CurveSnapshot(b1: 1000.0m, b2: -200.0m, b3: 100.0m, t1: 2.0m);

    [Theory]
    [InlineData(0.25, 0.08521137445132165)]
    [InlineData(1, 0.0898775021719933)]
    [InlineData(2, 0.09417428370521042)]
    [InlineData(5, 0.10021706263795749)]
    [InlineData(10, 0.10290333307255661)]
    public void CurveValue_PureNss_MatchesOfficialSvenssonFormula(double termYears, double expected)
    {
        var snapshot = SnapshotWithoutGAdjustment();

        var value = GSpreadCalculator.CurveValue(snapshot, (decimal)termYears);

        value.Should().BeApproximately((decimal)expected, Tolerance);
    }

    [Theory]
    [InlineData(0.25, 0.09926708502050197)]
    [InlineData(0.6, 0.10065954567612745)]
    [InlineData(1, 0.10037635604270312)]
    [InlineData(2, 0.09801812360540141)]
    [InlineData(5, 0.10843846094222198)]
    [InlineData(10, 0.10877593382616446)]
    public void CurveValue_WithGCorrections_MatchesOfficialGaussianTermFormula(double termYears, double expected)
    {
        // G-поправки — НЕ узлы сплайна/линейная интерполяция (так считала предыдущая версия
        // калькулятора — это и было ошибкой). По методике (раздел 4.1) каждый Gi — это
        // коэффициент гауссова "бугра" exp(-(t-ai)^2/bi^2), центрированного в ФИКСИРОВАННОЙ
        // точке ai (растущей до ~42 лет, не до 10), а не значение на годовом узле срока.
        var snapshot = TestModelFactory.CurveSnapshot(
            b1: 1000.0m, b2: -200.0m, b3: 100.0m, t1: 2.0m,
            g1: 50.0m, g2: 80.0m, g3: 30.0m, g4: -60.0m, g5: 90.0m,
            g6: 40.0m, g7: -20.0m, g8: 10.0m, g9: -5.0m);

        var value = GSpreadCalculator.CurveValue(snapshot, (decimal)termYears);

        value.Should().BeApproximately((decimal)expected, Tolerance);
    }

    [Fact]
    public void CurveValue_RealMoexFixtureScale_ProducesPlausibleYield()
    {
        // Регрессионный тест против реального снимка MOEX (фикстура этапа 04) — главная цель:
        // убедиться, что на реальном масштабе параметров (B1 порядка 1500 б.п., НЕ 0.10 в долях)
        // результат остаётся в правдоподобном диапазоне доходности гособлигаций РФ (грубо
        // 5%-30% годовых), а не выдаёт абсурдные тысячи процентов — именно так проявлялся баг
        // единиц измерения в предыдущей реализации (она не делала exp(G/10000)-1, поэтому
        // на реальных б.п.-параметрах "доходность" получалась порядка 1000+ "процентов").
        var snapshot = TestModelFactory.CurveSnapshot(
            b1: 1521.439389m, b2: -171.755139m, b3: -574.458809m, t1: 0.860877m,
            g1: 0.049362m, g2: 0.678947m, g3: 0.129863m, g4: -2.920334m, g5: 3.347373m,
            g6: 4.310632m, g7: -1.328817m, g8: 0.0m, g9: 0.0m);

        foreach (var term in new[] { 0.25m, 1m, 2m, 5m, 10m, 15m })
        {
            var value = GSpreadCalculator.CurveValue(snapshot, term);
            value.Should().BeInRange(0.05m, 0.30m, $"term={term}: доходность должна быть в правдоподобном диапазоне, не абсурдным числом из-за бага единиц измерения");
        }
    }

    [Fact]
    public void GSpread_SubtractsCurveValueFromYtm()
    {
        var snapshot = TestModelFactory.CurveSnapshot(
            b1: 1000.0m, b2: -200.0m, b3: 100.0m, t1: 2.0m,
            g1: 50.0m, g2: 80.0m, g3: 30.0m, g4: -60.0m, g5: 90.0m,
            g6: 40.0m, g7: -20.0m, g8: 10.0m, g9: -5.0m);

        // Значение кривой на 2 года = 0.09801812360540141 (см. тест выше).
        var spread = GSpreadCalculator.GSpread(bondYtm: 0.15m, durationYears: 2m, snapshot);

        spread.Should().BeApproximately(0.15m - 0.09801812360540141m, Tolerance);
    }
}

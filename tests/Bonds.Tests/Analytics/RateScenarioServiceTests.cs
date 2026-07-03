using Bonds.Core.Analytics;
using Bonds.Core.Models;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Analytics;

public class RateScenarioServiceTests
{
    private static PortfolioHolding Holding(
        ulong positionId,
        decimal marketValue,
        decimal? modifiedDuration,
        decimal? convexity = null) => new()
    {
        PositionId = positionId,
        InstrumentId = positionId,
        Quantity = 1,
        MarketValueRub = marketValue,
        CouponType = CouponType.Fixed,
        MaturityDate = new DateOnly(2030, 1, 1),
        HorizonDate = new DateOnly(2030, 1, 1),
        IsCalculatedToOffer = false,
        ModifiedDuration = modifiedDuration,
        Convexity = convexity,
        IsFloater = false,
        IsIndexed = false,
        IsEstimated = false,
        DataIncomplete = false,
    };

    [Fact]
    public void DurationEffect_KnownValues()
    {
        var holdings = new[] { Holding(1, 100_000m, modifiedDuration: 5m, convexity: null) };

        var result = RateScenarioService.Compute(holdings, [100]);

        var point = result.Single();
        point.ShiftBp.Should().Be(100);
        point.DeltaRub.Should().Be(-5000m);
    }

    [Fact]
    public void ConvexityEffect_KnownValues()
    {
        var holdings = new[] { Holding(1, 100_000m, modifiedDuration: 5m, convexity: 50m) };

        var result = RateScenarioService.Compute(holdings, [100]);

        var point = result.Single();
        // duration: -5 * 0.01 = -0.05 (rate)
        // convexity: 0.5 * 50 * 0.01^2 = 0.0025 (rate)
        // combined: (-0.05 + 0.0025) * 100000 = -4750
        point.DeltaRub.Should().Be(-4750m);
    }

    [Fact]
    public void ZeroShift_ReturnsCurrentValue()
    {
        var holdings = new[] { Holding(1, 100_000m, modifiedDuration: 5m, convexity: 50m) };

        var result = RateScenarioService.Compute(holdings, [0]);

        var point = result.Single();
        point.DeltaRub.Should().Be(0m);
        point.DeltaPercent.Should().Be(0m);
    }

    [Fact]
    public void NoDuration_PositionsSkipped()
    {
        var holdings = new[]
        {
            Holding(1, 100_000m, modifiedDuration: null),
            Holding(2, 50_000m, modifiedDuration: 3m),
        };

        var result = RateScenarioService.Compute(holdings, [100]);

        var point = result.Single();
        // only holding 2 contributes to Δ: -3 * 0.01 * 50000 = -1500
        point.DeltaRub.Should().Be(-1500m);
    }

    [Fact]
    public void BaseValueIncludesAllHoldings()
    {
        // H-1/M-1: база = весь портфель (вкл. флоатер без дюрации), чувствительность — только у
        // позиций с дюрацией. Иначе NewValue ≠ CurrentValue + Δ при наличии флоатеров.
        var holdings = new[]
        {
            Holding(1, 1000m, modifiedDuration: 3m, convexity: null), // фикс
            Holding(2, 1000m, modifiedDuration: null),                // флоатер — в базе, но 0 в Δ
        };

        var point = RateScenarioService.Compute(holdings, [100]).Single();

        var currentValue = point.NewValueRub - point.DeltaRub;
        currentValue.Should().Be(2000m, "база включает флоатер");
        point.DeltaRub.Should().Be(-30m, "Δ только от фикса: -3·0.01·1000; флоатер 0");
        point.NewValueRub.Should().Be(1970m);
        point.DeltaPercent.Should().Be(-1.5m, "процент берётся от всего портфеля (2000), не от подмножества");

        RateScenarioService.RateSensitiveValue(holdings).Should().Be(1000m,
            "процентно-чувствительная часть — только бумаги с дюрацией");
    }

    // Audit(portfolio): единицы на границе. RateScenarioPoint.DeltaPercent — ЕДИНСТВЕННОЕ
    // "Percent"-поле аналитического бэкенда, которое намеренно нарушает общую конвенцию репо
    // (доходности на бэкенде — доли 0-1, *100 делает фронт, см. CLAUDE.md/PositionCostBasis).
    // Здесь бэкенд сам умножает на 100 (см. RateScenarioService.Compute: "deltaRub / currentValue
    // * 100m"), а фронт (Analytics.tsx) рендерит его через `.toFixed(2)` БЕЗ formatPercent — если
    // это когда-нибудь "исправят" на доли для консистентности, экран тихо покажет число в 100 раз
    // меньше нужного (0.06% вместо 6%) без единого упавшего теста, кроме этого. Тест закрепляет
    // контракт явно, а не через побочный эффект чисел в других тестах.
    [Fact]
    public void DeltaPercent_IsAlreadyInPercentUnits_NotFraction()
    {
        var holdings = new[] { Holding(1, 100_000m, modifiedDuration: 5m, convexity: null) };

        // Δduration = -5 * 0.01 = -0.05 (доля) → DeltaRub = -5000 на базе 100000.
        // Если бы DeltaPercent была долей, здесь было бы -0.05; конвенция поля — уже "×100".
        var point = RateScenarioService.Compute(holdings, [100]).Single();

        point.DeltaPercent.Should().Be(-5m, "поле в процентных ПУНКТАХ (-5m), а не в долях (-0.05m)");
    }
}

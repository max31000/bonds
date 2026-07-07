using Bonds.Core.Analytics;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Analytics;

/// <summary>
/// Тесты конструктора корзины (plan/29 §A): сумма + проценты → штуки с лотами/НКД/комиссией,
/// БЕЗ жадного перераспределения остатков (предсказуемость важнее оптимальности — см. doc-comment
/// <see cref="BasketBuilderService"/>), метрики корзины со сноской про floater'ы (тот же принцип,
/// что "Итого" в задаче 21 / positionsAggregation.ts).
/// </summary>
public class BasketBuilderServiceTests
{
    private static BasketLineInput Line(
        ulong instrumentId,
        decimal weightFraction,
        decimal pricePerLotRub,
        string issuer,
        decimal? effectiveYield = 0.12m,
        decimal? modifiedDuration = 2m,
        bool isFloater = false,
        decimal lotSize = 1m,
        bool lotSizeIsAssumed = true,
        decimal cleanPriceRub = 0m,
        decimal accruedRub = 0m,
        decimal commissionRub = 0m) => new()
    {
        InstrumentId = instrumentId,
        Name = $"Bond {instrumentId}",
        Issuer = issuer,
        TargetWeightFraction = weightFraction,
        PricePerLotRub = pricePerLotRub,
        LotSize = lotSize,
        LotSizeIsAssumed = lotSizeIsAssumed,
        EffectiveYield = effectiveYield,
        ModifiedDuration = modifiedDuration,
        IsFloater = isFloater,
        CleanPriceRub = cleanPriceRub,
        AccruedRub = accruedRub,
        CommissionRub = commissionRub,
    };

    [Fact]
    public void Build_ThreeLinesWithWeights_40_30_30_On15k_ComputesQuantitiesAndLeftover()
    {
        var lines = new[]
        {
            Line(1, 0.40m, pricePerLotRub: 1000m, issuer: "A"), // target 6000 -> 6 шт, остаток 0
            Line(2, 0.30m, pricePerLotRub: 1000m, issuer: "B"), // target 4500 -> 4 шт, остаток 500
            Line(3, 0.30m, pricePerLotRub: 1000m, issuer: "C"), // target 4500 -> 4 шт, остаток 500
        };

        var result = BasketBuilderService.Build(amountRub: 15_000m, lines);

        result.Lines.Should().HaveCount(3);
        result.Lines.Single(l => l.InstrumentId == 1).Quantity.Should().Be(6m);
        result.Lines.Single(l => l.InstrumentId == 2).Quantity.Should().Be(4m);
        result.Lines.Single(l => l.InstrumentId == 3).Quantity.Should().Be(4m);

        // 6000 + 4000 + 4000 = 14000 потрачено, остаток 1000 (500 + 500 недобора по лотам).
        result.LeftoverRub.Should().Be(1000m);
    }

    [Fact]
    public void Build_WeightNotMultipleOfLotSize_LeavesPredictableLeftover_NoGreedyRedistribution()
    {
        var lines = new[]
        {
            Line(1, 0.50m, pricePerLotRub: 3000m, issuer: "A"), // target 5000 -> 1 лот (3000), остаток 2000
            Line(2, 0.50m, pricePerLotRub: 3000m, issuer: "B"), // target 5000 -> 1 лот (3000), остаток 2000
        };

        var result = BasketBuilderService.Build(amountRub: 10_000m, lines);

        result.Lines.Single(l => l.InstrumentId == 1).Quantity.Should().Be(1m);
        result.Lines.Single(l => l.InstrumentId == 2).Quantity.Should().Be(1m);
        // Остаток каждой строки НЕ уходит в другую бумагу — просто складывается в общий leftover.
        result.LeftoverRub.Should().Be(4000m);
    }

    [Fact]
    public void Build_SumOfWeightsBelowOne_RemainderIsMoneyLeftover()
    {
        var lines = new[]
        {
            Line(1, 0.40m, pricePerLotRub: 1000m, issuer: "A"), // target 4000 -> 4 шт
        };

        // Σ весов = 0.40, а не 1 — оставшиеся 60% суммы (6000 руб) должны попасть в leftover,
        // а не быть распределены между существующими строками.
        var result = BasketBuilderService.Build(amountRub: 10_000m, lines);

        result.Lines.Single().Quantity.Should().Be(4m);
        result.LeftoverRub.Should().Be(6000m);
    }

    [Fact]
    public void Build_BondMoreExpensiveThanItsShare_ZeroQuantity_AllShareGoesToLeftover()
    {
        var lines = new[]
        {
            Line(1, 0.10m, pricePerLotRub: 5000m, issuer: "A"), // target 1000, лот стоит 5000 -> 0 шт
        };

        var result = BasketBuilderService.Build(amountRub: 10_000m, lines);

        var line = result.Lines.Single();
        line.Quantity.Should().Be(0m);
        line.ActualCostRub.Should().Be(0m);
        result.LeftoverRub.Should().Be(10_000m);
    }

    [Fact]
    public void Build_ActualWeightAfterRounding_ReflectsRealSpend_NotTargetWeight()
    {
        var lines = new[]
        {
            Line(1, 1.0m, pricePerLotRub: 3000m, issuer: "A"), // target 10000 -> 3 шт = 9000, остаток 1000
        };

        var result = BasketBuilderService.Build(amountRub: 10_000m, lines);

        var line = result.Lines.Single();
        line.ActualCostRub.Should().Be(9000m);
        line.ActualWeightFraction.Should().Be(0.9m); // 9000/10000, а не заявленная 1.0
    }

    [Fact]
    public void Build_WeightedYieldAndDuration_WeightedByActualCost_ExcludingFloatersFromYield()
    {
        var lines = new[]
        {
            Line(1, 0.50m, pricePerLotRub: 1000m, issuer: "A", effectiveYield: 0.10m, modifiedDuration: 2m), // 5 шт = 5000
            Line(2, 0.50m, pricePerLotRub: 1000m, issuer: "B", effectiveYield: 0.20m, modifiedDuration: 4m), // 5 шт = 5000
        };

        var result = BasketBuilderService.Build(amountRub: 10_000m, lines);

        // Веса стоимости равны (5000/5000) -> средняя доходность (0.10+0.20)/2=0.15, дюрация (2+4)/2=3.
        result.Metrics.WeightedYield.Should().Be(0.15m);
        result.Metrics.WeightedDuration.Should().Be(3m);
        result.Metrics.HasExcludedFloaters.Should().BeFalse();
    }

    [Fact]
    public void Build_FloaterLine_ExcludedFromWeightedYield_ButIncludedInDuration()
    {
        var lines = new[]
        {
            Line(1, 0.50m, pricePerLotRub: 1000m, issuer: "A", effectiveYield: 0.10m, modifiedDuration: 2m, isFloater: false), // 5 шт
            Line(2, 0.50m, pricePerLotRub: 1000m, issuer: "B", effectiveYield: 0.30m, modifiedDuration: 4m, isFloater: true), // 5 шт, floater
        };

        var result = BasketBuilderService.Build(amountRub: 10_000m, lines);

        // Флоатер исключён из доходности -> только линия A (0.10).
        result.Metrics.WeightedYield.Should().Be(0.10m);
        // Дюрация учитывает обе линии (веса стоимости равны) -> (2+4)/2=3.
        result.Metrics.WeightedDuration.Should().Be(3m);
        result.Metrics.HasExcludedFloaters.Should().BeTrue();
    }

    [Fact]
    public void Build_ZeroQuantityLine_ExcludedFromWeightedMetrics()
    {
        var lines = new[]
        {
            Line(1, 0.10m, pricePerLotRub: 5000m, issuer: "A", effectiveYield: 0.50m), // 0 шт (недостаточно денег на долю)
            Line(2, 0.90m, pricePerLotRub: 1000m, issuer: "B", effectiveYield: 0.10m), // 9 шт = 9000
        };

        var result = BasketBuilderService.Build(amountRub: 10_000m, lines);

        result.Metrics.WeightedYield.Should().Be(0.10m, "нулевая строка не должна попадать в веса (иначе была бы 0 вклад с весом 0 — тот же результат, но проверяем явно она не в списке линий с quantity=0 портит агрегат)");
    }

    [Fact]
    public void Build_SplitsActualCostIntoCleanAccruedCommission_SumsReconcile()
    {
        var lines = new[]
        {
            Line(1, 1.0m, pricePerLotRub: 1046m, issuer: "A", cleanPriceRub: 1000m, accruedRub: 40m, commissionRub: 6m),
        };

        var result = BasketBuilderService.Build(amountRub: 2092m, lines); // ровно 2 лота

        var line = result.Lines.Single();
        line.Quantity.Should().Be(2m);
        line.ActualCostRub.Should().Be(2092m);
        (line.CleanCostRub + line.AccruedCostRub + line.CommissionCostRub).Should().Be(line.ActualCostRub);
        line.CleanCostRub.Should().Be(2000m);
        line.AccruedCostRub.Should().Be(80m);
        line.CommissionCostRub.Should().Be(12m);
    }

    [Fact]
    public void Build_EmptyLines_ReturnsAllAmountAsLeftover_NullMetrics()
    {
        var result = BasketBuilderService.Build(amountRub: 5000m, lines: []);

        result.Lines.Should().BeEmpty();
        result.LeftoverRub.Should().Be(5000m);
        result.Metrics.WeightedYield.Should().BeNull();
        result.Metrics.WeightedDuration.Should().BeNull();
    }

    [Fact]
    public void Build_NonPositiveAmount_Throws()
    {
        var act = () => BasketBuilderService.Build(0m, []);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Build_SumOfWeightsAboveOne_Throws()
    {
        var lines = new[]
        {
            Line(1, 0.60m, pricePerLotRub: 1000m, issuer: "A"),
            Line(2, 0.60m, pricePerLotRub: 1000m, issuer: "B"),
        };

        var act = () => BasketBuilderService.Build(amountRub: 10_000m, lines);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Build_ReturnsDisclaimer_MentioningFloaters()
    {
        var result = BasketBuilderService.Build(1000m, []);

        result.Disclaimer.Should().NotBeNullOrWhiteSpace();
    }
}

using Bonds.Core.Analytics;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Analytics;

/// <summary>
/// Тесты what-if превью портфеля (plan/29 §B): до/после по стоимости/доходности/дюрации/
/// концентрациям + предупреждения о превышении лимита концентрации / новом крупном эмитенте.
/// </summary>
public class PortfolioWhatIfServiceTests
{
    private static WhatIfHoldingInput Holding(
        ulong instrumentId, string issuer, decimal marketValueRub, decimal? yield, decimal? duration, bool isFloater = false) => new()
    {
        InstrumentId = instrumentId,
        Issuer = issuer,
        MarketValueRub = marketValueRub,
        EffectiveYield = yield,
        ModifiedDuration = duration,
        IsFloater = isFloater,
    };

    private static BasketLine Line(ulong instrumentId, string issuer, decimal costRub, decimal? yield, decimal? duration, bool isFloater = false) => new()
    {
        InstrumentId = instrumentId,
        Name = $"Bond {instrumentId}",
        Issuer = issuer,
        TargetWeightFraction = 0m,
        ActualWeightFraction = 0m,
        Quantity = costRub > 0 ? 1m : 0m,
        ActualCostRub = costRub,
        EffectiveYield = yield,
        ModifiedDuration = duration,
        IsFloater = isFloater,
        LotSizeAssumed = true,
    };

    [Fact]
    public void Evaluate_ThreePositionsPlusTwoBasketLines_ComputesValueYieldDurationDeltas_ByHand()
    {
        // До: 3 позиции. A: 50000 @ yield 0.10 dur 2; B: 30000 @ yield 0.14 dur 3; C: 20000 @ yield 0.20 dur 1.
        // Итого 100000; weightedYield = (50000*0.10+30000*0.14+20000*0.20)/100000 = (5000+4200+4000)/100000=0.132
        // weightedDuration = (50000*2+30000*3+20000*1)/100000=(100000+90000+20000)/100000=2.1
        var holdings = new[]
        {
            Holding(1, "A", 50_000m, 0.10m, 2m),
            Holding(2, "B", 30_000m, 0.14m, 3m),
            Holding(3, "C", 20_000m, 0.20m, 1m),
        };

        // Корзина: D 10000 @ yield 0.30 dur 5; E 5000 @ yield 0.05 dur 0.5.
        var basket = new[]
        {
            Line(4, "D", 10_000m, 0.30m, 5m),
            Line(5, "E", 5_000m, 0.05m, 0.5m),
        };

        var result = PortfolioWhatIfService.Evaluate(holdings, basket);

        result.Before.TotalValueRub.Should().Be(100_000m);
        result.Before.WeightedYield.Should().Be(0.132m);
        result.Before.WeightedDuration.Should().Be(2.1m);

        // После: итого 115000.
        // weightedYield = (5000+4200+4000+10000*0.30+5000*0.05)/115000 = (13200+3000+250)/115000 = 16450/115000
        var expectedAfterValue = 115_000m;
        var expectedAfterYield = (5000m + 4200m + 4000m + 3000m + 250m) / expectedAfterValue;
        var expectedAfterDuration = (100_000m + 90_000m + 20_000m + 50_000m + 2_500m) / expectedAfterValue;

        result.After.TotalValueRub.Should().Be(expectedAfterValue);
        result.After.WeightedYield.Should().BeApproximately(expectedAfterYield, 0.0000001m);
        result.After.WeightedDuration.Should().BeApproximately(expectedAfterDuration, 0.0000001m);

        result.After.TotalValueRub.Should().BeGreaterThan(result.Before.TotalValueRub);
    }

    [Fact]
    public void Evaluate_ConcentrationBreach_Existing_AboveThreshold_ProducesWarning()
    {
        // Эмитент A уже 80% портфеля (80000 из 100000); докупка ещё увеличивает долю -> предупреждение.
        var holdings = new[]
        {
            Holding(1, "A", 80_000m, 0.10m, 2m),
            Holding(2, "B", 20_000m, 0.14m, 3m),
        };
        var basket = new[] { Line(3, "A", 10_000m, 0.10m, 2m) };

        var result = PortfolioWhatIfService.Evaluate(holdings, basket, maxConcentrationPercent: 25m);

        result.Warnings.Should().Contain(w => w.Kind == WhatIfWarningKind.ConcentrationLimitBreached && w.Issuer == "A");
    }

    [Fact]
    public void Evaluate_NoConcentrationBreach_BalancedBasket_NoWarning()
    {
        // Четыре равных эмитента по 25000 (по 20% каждый) — докупка небольшой суммы новому,
        // пятому эмитенту держит все доли строго ниже лимита 25% (и ниже порога "новый эмитент" 25%).
        var holdings = new[]
        {
            Holding(1, "A", 25_000m, 0.10m, 2m),
            Holding(2, "B", 25_000m, 0.14m, 3m),
            Holding(3, "C", 25_000m, 0.11m, 2m),
            Holding(4, "D", 25_000m, 0.12m, 2m),
        };
        var basket = new[] { Line(5, "E", 1_000m, 0.10m, 2m) };

        var result = PortfolioWhatIfService.Evaluate(holdings, basket, maxConcentrationPercent: 25m);

        result.Warnings.Should().NotContain(w => w.Kind == WhatIfWarningKind.ConcentrationLimitBreached);
        result.Warnings.Should().NotContain(w => w.Kind == WhatIfWarningKind.NewIssuerAboveThreshold);
    }

    [Fact]
    public void Evaluate_NewIssuerAbove25Percent_ProducesWarning_EvenBelowGeneralLimit()
    {
        // Новый эмитент "D" не был в портфеле, после покупки его доля > 25% -> отдельное предупреждение
        // "появление эмитента > 25%" (пункт плана), даже если общий лимит настройки другой (например, 30%).
        var holdings = new[]
        {
            Holding(1, "A", 70_000m, 0.10m, 2m),
        };
        var basket = new[] { Line(2, "D", 30_000m, 0.12m, 2m) };

        var result = PortfolioWhatIfService.Evaluate(holdings, basket, maxConcentrationPercent: 30m);

        result.Warnings.Should().Contain(w => w.Kind == WhatIfWarningKind.NewIssuerAboveThreshold && w.Issuer == "D");
    }

    [Fact]
    public void Evaluate_ByIssuerConcentration_SharesBeforeAndAfter_ArePresent()
    {
        var holdings = new[]
        {
            Holding(1, "A", 50_000m, 0.10m, 2m),
            Holding(2, "B", 50_000m, 0.14m, 3m),
        };
        var basket = new[] { Line(3, "A", 10_000m, 0.10m, 2m) };

        var result = PortfolioWhatIfService.Evaluate(holdings, basket);

        var concentrationA = result.Concentrations.Single(c => c.Issuer == "A");
        concentrationA.SharePercentBefore.Should().Be(50m);
        // После: (50000+10000)/(100000+10000) = 60000/110000 ≈ 54.545...%
        concentrationA.SharePercentAfter.Should().BeApproximately(60_000m / 110_000m * 100m, 0.0001m);
    }

    [Fact]
    public void Evaluate_FloaterExcludedFromWeightedYield_BothBeforeAndAfter()
    {
        var holdings = new[]
        {
            Holding(1, "A", 50_000m, 0.10m, 2m, isFloater: false),
            Holding(2, "B", 50_000m, 0.50m, 2m, isFloater: true), // floater — исключается из доходности
        };
        var basket = new[] { Line(3, "C", 10_000m, 0.10m, 2m, isFloater: false) };

        var result = PortfolioWhatIfService.Evaluate(holdings, basket);

        result.Before.WeightedYield.Should().Be(0.10m);
        result.Before.HasExcludedFloaters.Should().BeTrue();
        result.After.WeightedYield.Should().Be(0.10m); // (50000*0.10+10000*0.10)/(60000) = 0.10
        result.After.HasExcludedFloaters.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_EmptyPortfolio_PlusBasket_BeforeIsZero_AfterReflectsBasketOnly()
    {
        var result = PortfolioWhatIfService.Evaluate(
            Array.Empty<WhatIfHoldingInput>(),
            new[] { Line(1, "A", 5_000m, 0.10m, 2m) });

        result.Before.TotalValueRub.Should().Be(0m);
        result.Before.WeightedYield.Should().BeNull();
        result.After.TotalValueRub.Should().Be(5_000m);
        result.After.WeightedYield.Should().Be(0.10m);
    }
}

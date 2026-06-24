using Bonds.Core.Calculation;
using Bonds.Core.Models;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Calculation;

/// <summary>
/// Тесты доходности к оферте (spec §6.4/§7.3, plan/05 Часть A.4/D) — отдельный класс,
/// в дополнение к проверке через фасад <see cref="BondMetricsCalculatorTests"/>, чтобы
/// зафиксировать контракт API, которым будет напрямую пользоваться этап 06.
/// </summary>
public class YieldToOfferCalculatorTests
{
    private const ulong InstrumentId = 1;
    private static readonly DateOnly AsOf = new(2025, 1, 1);

    [Fact]
    public void Calculate_EligibleOffer_ReturnsYieldToOfferHorizon()
    {
        // Денежный поток обрезается на дате оферты (BondCashFlowBuilder трактует оферту как
        // полное погашение по номиналу на эту дату, см. XML-doc YieldToOfferCalculator):
        // на offerDate выплата = купон 100 + номинал 1000 = 1100. Купон на дату погашения
        // (далеко за горизонтом оферты) в поток к оферте не попадает — это и проверяем.
        // Эталон: price = 1100 / 1.12 = 982.142857142857 при YTM=12%.
        var maturity = AsOf.AddYears(10);
        var offerDate = AsOf.AddDays(365);
        var coupons = new[]
        {
            TestModelFactory.Coupon(InstrumentId, offerDate, 100m, periodDays: 365),
            TestModelFactory.Coupon(InstrumentId, maturity, 100m, periodDays: 365),
        };
        var offers = new[] { TestModelFactory.Offer(InstrumentId, offerDate, OfferType.Put) };

        var result = YieldToOfferCalculator.Calculate(
            faceValue: 1000m,
            dirtyPrice: 982.142857142857m,
            asOf: AsOf,
            maturityDate: maturity,
            coupons: coupons,
            amortizations: null,
            offers: offers);

        result.Should().NotBeNull();
        result!.Value.Horizon.Date.Should().Be(offerDate);
        result.Value.Horizon.IsOffer.Should().BeTrue();
        result.Value.Yield.EffectiveYield.Should().BeApproximately(0.12m, 1e-4m);
        result.Value.CashFlow.Should().HaveCount(1, "поток обрезается на дате оферты — купон на дату погашения не попадает в горизонт");
    }

    [Fact]
    public void Calculate_NoEligibleOffer_ReturnsNull()
    {
        var maturity = AsOf.AddYears(2);
        var coupons = new[] { TestModelFactory.Coupon(InstrumentId, maturity, 100m, periodDays: 730) };

        var result = YieldToOfferCalculator.Calculate(
            faceValue: 1000m,
            dirtyPrice: 950m,
            asOf: AsOf,
            maturityDate: maturity,
            coupons: coupons,
            amortizations: null,
            offers: null);

        result.Should().BeNull("без неисполненных оферт горизонт — погашение, а не оферта");
    }

    [Fact]
    public void Calculate_OnlyExecutedOffers_ReturnsNull()
    {
        var maturity = AsOf.AddYears(2);
        var executedOffer = TestModelFactory.Offer(InstrumentId, AsOf.AddDays(180), OfferType.Put, isExecuted: true);
        var coupons = new[] { TestModelFactory.Coupon(InstrumentId, maturity, 100m, periodDays: 730) };

        var result = YieldToOfferCalculator.Calculate(
            faceValue: 1000m,
            dirtyPrice: 950m,
            asOf: AsOf,
            maturityDate: maturity,
            coupons: coupons,
            amortizations: null,
            offers: new[] { executedOffer });

        result.Should().BeNull();
    }
}

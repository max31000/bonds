using Bonds.Core.Calculation;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Calculation;

/// <summary>
/// Тесты текущей купонной доходности (spec §6 «Краевые случаи» — флоатер/индексируемая бумага,
/// plan/05 Часть B/D) и оценки периодичности купонов.
/// </summary>
public class CurrentYieldCalculatorTests
{
    private const ulong InstrumentId = 1;
    private static readonly DateOnly AsOf = new(2025, 1, 1);

    [Fact]
    public void Calculate_KnownCurrentCoupon_AnnualizesCorrectly()
    {
        // Купон 20 руб. на период 91 день (квартальный) -> годовая сумма = 20 * 365/91 = 80.2198;
        // доходность к грязной цене 1000 = 0.0802198.
        var coupon = TestModelFactory.Coupon(InstrumentId, AsOf.AddDays(30), 20m, periodDays: 91);

        var result = CurrentYieldCalculator.Calculate(AsOf, dirtyPrice: 1000m, new[] { coupon });

        result.Should().NotBeNull();
        result!.Value.Should().BeApproximately(20m * 365m / 91m / 1000m, 1e-6m);
    }

    [Fact]
    public void Calculate_NoKnownCoupons_ReturnsNull()
    {
        var unknown = TestModelFactory.Coupon(InstrumentId, AsOf.AddDays(30), null, isKnown: false);

        CurrentYieldCalculator.Calculate(AsOf, 1000m, new[] { unknown }).Should().BeNull();
    }

    [Fact]
    public void Calculate_NonPositiveDirtyPrice_ReturnsNull()
    {
        var coupon = TestModelFactory.Coupon(InstrumentId, AsOf.AddDays(30), 20m, periodDays: 91);

        CurrentYieldCalculator.Calculate(AsOf, 0m, new[] { coupon }).Should().BeNull();
    }

    [Fact]
    public void Calculate_AllKnownCouponsInPast_UsesLastKnownAsBestEstimate()
    {
        var pastCoupon = TestModelFactory.Coupon(InstrumentId, AsOf.AddDays(-10), 20m, periodDays: 91);

        var result = CurrentYieldCalculator.Calculate(AsOf, 1000m, new[] { pastCoupon });

        result.Should().NotBeNull("даже если все известные купоны в прошлом, последний из них — лучшая доступная оценка ставки");
    }
}

/// <summary>Тесты оценки периодичности купонов (нужна для модифицированной дюрации, spec §6.6).</summary>
public class CouponFrequencyEstimatorTests
{
    private const ulong InstrumentId = 1;

    [Fact]
    public void EstimateCouponsPerYear_QuarterlySchedule_Returns4()
    {
        var dates = new[] { 1, 91, 182, 273, 365 };
        var coupons = dates.Select(d => TestModelFactory.Coupon(InstrumentId, new DateOnly(2025, 1, 1).AddDays(d), 25m)).ToArray();

        CouponFrequencyEstimator.EstimateCouponsPerYear(coupons).Should().Be(4);
    }

    [Fact]
    public void EstimateCouponsPerYear_AnnualSchedule_Returns1()
    {
        var coupons = new[]
        {
            TestModelFactory.Coupon(InstrumentId, new DateOnly(2025, 1, 1), 100m),
            TestModelFactory.Coupon(InstrumentId, new DateOnly(2026, 1, 1), 100m),
        };

        CouponFrequencyEstimator.EstimateCouponsPerYear(coupons).Should().Be(1);
    }

    [Fact]
    public void EstimateCouponsPerYear_LessThanTwoCoupons_FallsBackToOne()
    {
        var coupons = new[] { TestModelFactory.Coupon(InstrumentId, new DateOnly(2025, 1, 1), 100m) };

        CouponFrequencyEstimator.EstimateCouponsPerYear(coupons).Should().Be(1);
    }
}

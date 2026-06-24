using Bonds.Core.Calculation;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Calculation;

/// <summary>
/// Тесты построителя денежного потока с амортизацией (spec §6 «Краевые случаи», plan/05 Часть B/D).
/// </summary>
public class BondCashFlowBuilderTests
{
    private const ulong InstrumentId = 1;
    private static readonly DateOnly AsOf = new(2025, 1, 1);

    [Fact]
    public void Build_NoAmortization_RepaysFullFaceValueAtMaturity()
    {
        var maturity = AsOf.AddDays(730);
        var coupons = new[]
        {
            TestModelFactory.Coupon(InstrumentId, AsOf.AddDays(365), 100m),
            TestModelFactory.Coupon(InstrumentId, maturity, 100m),
        };

        var flow = BondCashFlowBuilder.Build(1000m, AsOf, maturity, coupons, amortizations: null);

        flow.Should().HaveCount(2);
        flow[0].CouponAmount.Should().Be(100m);
        flow[0].PrincipalAmount.Should().Be(0m);
        flow[1].CouponAmount.Should().Be(100m);
        flow[1].PrincipalAmount.Should().Be(1000m, "без амортизации весь номинал гасится при погашении");
    }

    [Fact]
    public void Build_WithAmortization_ReducesPrincipalAtMaturityByAmortizedAmount()
    {
        var maturity = AsOf.AddDays(730);
        var amortizationDate = AsOf.AddDays(365);

        var coupons = new[]
        {
            TestModelFactory.Coupon(InstrumentId, amortizationDate, 100m),
            TestModelFactory.Coupon(InstrumentId, maturity, 50m), // купон на остаток номинала (500), уже из источника
        };
        var amortizations = new[]
        {
            TestModelFactory.Amortization(InstrumentId, amortizationDate, 500m),
        };

        var flow = BondCashFlowBuilder.Build(1000m, AsOf, maturity, coupons, amortizations);

        flow.Should().HaveCount(2);

        var amortPoint = flow.Single(f => f.Date == amortizationDate);
        amortPoint.CouponAmount.Should().Be(100m);
        amortPoint.PrincipalAmount.Should().Be(500m, "частичный возврат номинала в дату амортизации");

        var maturityPoint = flow.Single(f => f.Date == maturity);
        maturityPoint.CouponAmount.Should().Be(50m);
        maturityPoint.PrincipalAmount.Should().Be(500m, "остаточный номинал после амортизации 500 из 1000");
    }

    [Fact]
    public void Build_FullyAmortizedBeforeMaturity_NoAdditionalPrincipalAtMaturity()
    {
        var maturity = AsOf.AddDays(730);
        var amortizationDate = AsOf.AddDays(365);

        var coupons = new[] { TestModelFactory.Coupon(InstrumentId, maturity, 10m) };
        var amortizations = new[]
        {
            TestModelFactory.Amortization(InstrumentId, amortizationDate, 1000m), // полностью гасит номинал
        };

        var flow = BondCashFlowBuilder.Build(1000m, AsOf, maturity, coupons, amortizations);

        var maturityPoint = flow.Single(f => f.Date == maturity);
        maturityPoint.PrincipalAmount.Should().Be(0m, "номинал уже полностью погашен амортизацией, повторного возврата нет");
    }

    [Fact]
    public void Build_UnknownFutureCoupon_MarksItemAsNotKnown()
    {
        var maturity = AsOf.AddDays(365);
        var coupons = new[] { TestModelFactory.Coupon(InstrumentId, maturity, null, isKnown: false) };

        var flow = BondCashFlowBuilder.Build(1000m, AsOf, maturity, coupons, amortizations: null);

        flow.Single().IsKnown.Should().BeFalse();
    }

    [Fact]
    public void Build_HorizonBeforeAsOf_ReturnsEmpty()
    {
        var coupons = new[] { TestModelFactory.Coupon(InstrumentId, AsOf.AddDays(-10), 100m) };

        var flow = BondCashFlowBuilder.Build(1000m, AsOf, AsOf.AddDays(-1), coupons, amortizations: null);

        flow.Should().BeEmpty();
    }
}

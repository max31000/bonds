using Bonds.Core.Calculation;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Calculation;

/// <summary>
/// Тесты НКД (пропорциональное накопление, spec §6.1) и грязной цены (§6.2) — plan/05
/// Часть A.1–A.2, Часть D.
/// </summary>
public class AccruedInterestCalculatorTests
{
    private const ulong InstrumentId = 1;

    [Fact]
    public void Calculate_MidPeriod_ProRatesCorrectly()
    {
        // Период купона 182 дня (полугодовой), купон 50 руб., предыдущий купон 2025-01-01,
        // следующий — 2025-07-02 (182 дня). asOf на 91-й день периода (ровно половина) →
        // НКД = 50 * 91/182 = 25.
        var previous = TestModelFactory.Coupon(InstrumentId, new DateOnly(2025, 1, 1), 50m, periodDays: 182);
        var next = TestModelFactory.Coupon(InstrumentId, new DateOnly(2025, 7, 2), 50m, periodDays: 182);

        var asOf = new DateOnly(2025, 1, 1).AddDays(91);

        var result = AccruedInterestCalculator.Calculate(asOf, new[] { previous, next });

        result.Should().NotBeNull();
        result!.Value.Should().BeApproximately(25m, 1e-4m);
    }

    [Fact]
    public void Calculate_AtCouponDate_AccruedIsZero()
    {
        var previous = TestModelFactory.Coupon(InstrumentId, new DateOnly(2025, 1, 1), 50m, periodDays: 182);
        var next = TestModelFactory.Coupon(InstrumentId, new DateOnly(2025, 7, 2), 50m, periodDays: 182);

        var result = AccruedInterestCalculator.Calculate(new DateOnly(2025, 1, 1), new[] { previous, next });

        result.Should().NotBeNull();
        result!.Value.Should().Be(0m);
    }

    [Fact]
    public void Calculate_NoCoupons_ReturnsNull()
    {
        AccruedInterestCalculator.Calculate(new DateOnly(2025, 1, 1), Array.Empty<Bonds.Core.Models.CouponSchedule>())
            .Should().BeNull();
    }

    [Fact]
    public void Calculate_NoFutureCoupon_ReturnsNull()
    {
        // Все купоны в графике уже в прошлом относительно asOf — горизонт за пределами графика,
        // расчёт недостоверен (spec §4.4 — не подставлять значение молча).
        var past = TestModelFactory.Coupon(InstrumentId, new DateOnly(2025, 1, 1), 50m, periodDays: 182);

        var result = AccruedInterestCalculator.Calculate(new DateOnly(2026, 1, 1), new[] { past });

        result.Should().BeNull();
    }

    [Fact]
    public void Calculate_NextCouponUnknown_ReturnsNull()
    {
        // Флоатер: следующий купон после ближайшего пересчёта неизвестен — IsKnown=false.
        var unknown = TestModelFactory.Coupon(InstrumentId, new DateOnly(2025, 7, 2), null, periodDays: 182, isKnown: false);

        var result = AccruedInterestCalculator.Calculate(new DateOnly(2025, 4, 1), new[] { unknown });

        result.Should().BeNull();
    }

    [Fact]
    public void DirtyPrice_AddsAccruedToClean()
    {
        AccruedInterestCalculator.DirtyPrice(980m, 15.5m).Should().Be(995.5m);
    }
}

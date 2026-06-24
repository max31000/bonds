using Bonds.Core.Calculation;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Calculation;

/// <summary>
/// Тесты дюрации Маколея, модифицированной дюрации, выпуклости и PVBP (plan/05 Часть A.5–A.7,
/// Часть D). Эталон — та же двухпериодная облигация, что в <see cref="YtmCalculatorTests"/>,
/// посчитанная аналитически вручную при y=12%, k=1 (см. комментарии у констант).
/// </summary>
public class DurationCalculatorTests
{
    private const decimal Tolerance = 1e-4m;
    private static readonly DateOnly AsOf = new(2025, 1, 1);

    private static List<BondCashFlowItem> ReferenceCashFlow() => new()
    {
        new(AsOf.AddDays(365), 100m, 0m, true),
        new(AsOf.AddDays(730), 100m, 1000m, true),
    };

    private const decimal ReferenceDirtyPrice = 966.1989795918366m;
    private const decimal ReferenceYtm = 0.12m;

    [Fact]
    public void Calculate_TwoPeriodBond_MacaulayDurationMatchesHandComputedReference()
    {
        // pv1 = 100/1.12 = 89.2857142857143; pv2 = 1100/1.12^2 = 876.9132653061224
        // macaulay = (1*pv1 + 2*pv2) / (pv1+pv2) = 1.9075907590759074 years
        var result = DurationCalculator.Calculate(ReferenceDirtyPrice, ReferenceYtm, AsOf, ReferenceCashFlow(), couponsPerYear: 1);

        result.Should().NotBeNull();
        result!.Value.MacaulayDurationYears.Should().BeApproximately(1.9075907590759074m, Tolerance);
    }

    [Fact]
    public void Calculate_TwoPeriodBond_ModifiedDurationMatchesHandComputedReference()
    {
        // modified = macaulay / (1 + y/k) = 1.9075907590759074 / 1.12 = 1.7032060348892029
        var result = DurationCalculator.Calculate(ReferenceDirtyPrice, ReferenceYtm, AsOf, ReferenceCashFlow(), couponsPerYear: 1);

        result.Should().NotBeNull();
        result!.Value.ModifiedDuration.Should().BeApproximately(1.7032060348892029m, Tolerance);
    }

    [Fact]
    public void Calculate_TwoPeriodBond_ConvexityMatchesHandComputedReference()
    {
        // convexity = (1*2*pv1 + 2*3*pv2) / (price * 1.12^2) = 4.488490940930827
        var result = DurationCalculator.Calculate(ReferenceDirtyPrice, ReferenceYtm, AsOf, ReferenceCashFlow(), couponsPerYear: 1);

        result.Should().NotBeNull();
        result!.Value.Convexity.Should().BeApproximately(4.488490940930827m, 1e-3m);
    }

    [Fact]
    public void Calculate_TwoPeriodBond_PvbpMatchesHandComputedReference()
    {
        // pvbp = modifiedDuration * price * 0.0001 = 1.7032060348892029 * 966.1989795918366 * 0.0001
        //      = 0.1645635932944606
        var result = DurationCalculator.Calculate(ReferenceDirtyPrice, ReferenceYtm, AsOf, ReferenceCashFlow(), couponsPerYear: 1);

        result.Should().NotBeNull();
        result!.Value.Pvbp.Should().BeApproximately(0.1645635932944606m, Tolerance);
    }

    [Fact]
    public void Calculate_NonPositivePrice_ReturnsNull()
    {
        DurationCalculator.Calculate(0m, ReferenceYtm, AsOf, ReferenceCashFlow(), 1).Should().BeNull();
    }

    [Fact]
    public void Calculate_EmptyCashFlow_ReturnsNull()
    {
        DurationCalculator.Calculate(1000m, ReferenceYtm, AsOf, new List<BondCashFlowItem>(), 1).Should().BeNull();
    }

    [Fact]
    public void Calculate_ZeroOrNegativeCouponsPerYear_FallsBackToOne()
    {
        // couponsPerYear <= 0 не должно приводить к делению на ноль — калькулятор подставляет 1.
        var result = DurationCalculator.Calculate(ReferenceDirtyPrice, ReferenceYtm, AsOf, ReferenceCashFlow(), couponsPerYear: 0);

        result.Should().NotBeNull();
        result!.Value.ModifiedDuration.Should().BeApproximately(1.7032060348892029m, Tolerance);
    }
}

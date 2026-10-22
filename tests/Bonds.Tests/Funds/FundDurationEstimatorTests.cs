using Bonds.Core.Funds;
using Bonds.Core.Models;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Funds;

public class FundDurationEstimatorTests
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    private static MarketIndexPoint Rgbi(int ageDays = 1, decimal? yield = 0.1603m) =>
        new(Today.AddDays(-ageDays), 100m, yield, 1657m / 365m);

    [Fact]
    public void Index_ModifiedFromMacaulay_UsesSemiannualCoupon()
    {
        var e = FundDurationEstimator.Estimate(null, true, "RGBI", Rgbi(), Today);

        e.ModifiedDurationYears.Should().BeApproximately(4.2028m, 0.0005m); // 4.5397 / 1.08015
        e.Source.Should().Be("index:RGBI");
        e.AsOf.Should().Be(Today.AddDays(-1));
        e.Warnings.Should().BeEmpty();
    }

    [Fact]
    public void Manual_OverridesIndex()
    {
        var e = FundDurationEstimator.Estimate(0.3m, true, "RGBI", Rgbi(), Today);

        e.ModifiedDurationYears.Should().Be(0.3m);
        e.Source.Should().Be("manual");
    }

    [Fact]
    public void StaleIndex_UsedWithWarning()
    {
        var e = FundDurationEstimator.Estimate(null, true, "RGBI", Rgbi(ageDays: 11), Today);

        e.ModifiedDurationYears.Should().NotBeNull();
        e.Warnings.Should().Contain("IndexStale");
    }

    [Fact]
    public void NoIndexApplicable_NoManual_Null()
    {
        var e = FundDurationEstimator.Estimate(null, false, "RGBI", Rgbi(), Today);

        e.ModifiedDurationYears.Should().BeNull();
        e.Source.Should().BeNull();
    }

    [Fact]
    public void NoIndexPoint_Null()
    {
        FundDurationEstimator.Estimate(null, true, "RGBI", null, Today).ModifiedDurationYears.Should().BeNull();
    }

    [Fact]
    public void Fund_FloaterFundWithoutBenchmark_NoIndexEstimate_OfzFundGetsRgbi()
    {
        var tpay = new Fund { Ticker = "TPAY", Name = "Пассивный доход", FocusType = "fixed_income" };
        var tofz = new Fund { Ticker = "TOFZ", Name = "Облигации ОФЗ", FocusType = "fixed_income" };

        FundDurationEstimator.Estimate(tpay, Rgbi(), Today).ModifiedDurationYears.Should().BeNull();
        FundDurationEstimator.Estimate(tofz, Rgbi(), Today).Source.Should().Be("index:RGBI");
    }

    [Fact]
    public void Fund_ExplicitBenchmark_UsesItsCodeInSource()
    {
        var f = new Fund { Ticker = "X", BenchmarkIndex = "RUGBITR3Y" };
        FundDurationEstimator.Estimate(f, Rgbi(), Today).Source.Should().Be("index:RUGBITR3Y");
    }
}

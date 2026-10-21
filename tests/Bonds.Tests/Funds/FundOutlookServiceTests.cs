using Bonds.Core.Funds;
using Bonds.Core.Models;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Funds;

public class FundOutlookServiceTests
{
    private static readonly DateOnly Today = new(2026, 10, 21);

    private static FundOutlookInput Input(
        FundForecastMethod method = FundForecastMethod.Auto,
        string? focus = "fixed_income",
        string? name = "Пассивный доход",
        string? ticker = "TPAY",
        decimal? ter = 0.0079m,
        decimal? price = 100m,
        decimal qty = 10m,
        IReadOnlyList<FundPricePoint>? prices = null,
        IReadOnlyList<FundDistribution>? dists = null,
        MarketIndexPoint? index = null,
        bool explicitBenchmark = false) =>
        new(method, focus, name, ticker, ter, price, qty, (price ?? 0m) * qty, prices ?? [], dists ?? [],
            "RGBI", explicitBenchmark, index, Today);

    private static FundDistribution D(int daysAgo, decimal amount) => new(Today.AddDays(-daysAgo), null, amount);

    private static MarketIndexPoint Rgbi(int daysAgo, decimal yield) => new(Today.AddDays(-daysAgo), 100m, yield, 6m);

    [Fact]
    public void Distributions_AnnualizesLast92Days()
    {
        var r = FundOutlookService.Compute(Input(dists: [D(60, 1.3m), D(30, 1.25m), D(2, 1.2m)]));

        r.Method.Should().Be(FundForecastMethod.Distributions);
        r.ExpectedAnnualYieldFraction.Should().BeApproximately(3.75m * 365m / 92m / 100m, 0.00001m); // ≈ 0.14878
        r.ExpectedMonthlyIncomeRub.Should().Be(12m); // 1.2 × 10 паёв
        r.ExpectedIncome1yRub.Should().BeApproximately(1000m * r.ExpectedAnnualYieldFraction!.Value, 0.001m);
        r.AfterTaxYieldFraction.Should().BeApproximately(r.ExpectedAnnualYieldFraction.Value * 0.87m, 0.00001m);
        r.Confidence.Should().Be(FundOutlookConfidence.High);
        r.BasisLabel.Should().Contain("выплатам");
    }

    [Fact]
    public void IndexYield_IsIndexMinusTer()
    {
        var r = FundOutlookService.Compute(Input(name: "Т-Капитал ОФЗ", ticker: "TOFZ", index: Rgbi(1, 0.1603m)));

        r.Method.Should().Be(FundForecastMethod.IndexYield);
        r.ExpectedAnnualYieldFraction.Should().Be(0.1524m);
        r.BasisLabel.Should().Be("доходность индекса RGBI 16.03% − комиссия фонда 0.79%");
        r.Warnings.Should().BeEmpty();
        r.Confidence.Should().Be(FundOutlookConfidence.Medium);
    }

    [Fact]
    public void IndexYield_StaleIndexAndUnknownTer_Warn()
    {
        var r = FundOutlookService.Compute(Input(name: "Т-Капитал ОФЗ", ticker: "TOFZ", ter: null, index: Rgbi(30, 0.16m)));

        r.ExpectedAnnualYieldFraction.Should().Be(0.16m);
        r.Warnings.Should().Contain([FundOutlookWarning.IndexStale, FundOutlookWarning.TerUnknown]);
        r.Confidence.Should().Be(FundOutlookConfidence.Low);
    }

    [Fact]
    public void Auto_OfzFundWithoutDistributions_PicksIndex_ButNonOfzFallsToTrailing()
    {
        var ofz = FundOutlookService.Compute(Input(ticker: "TOFZ", name: "Т-Капитал ОФЗ", index: Rgbi(1, 0.16m)));
        ofz.Method.Should().Be(FundForecastMethod.IndexYield);

        var prices = new[] { new FundPricePoint(Today.AddDays(-400), 100m), new FundPricePoint(Today, 108m) };
        var other = FundOutlookService.Compute(Input(ticker: "TMON", name: "Денежный рынок", prices: prices, index: Rgbi(1, 0.16m)));
        other.Method.Should().Be(FundForecastMethod.TrailingReturn);
        other.ExpectedAnnualYieldFraction.Should().BeApproximately(0.08m, 0.0001m);
    }

    [Fact]
    public void Auto_ExplicitBenchmarkEnablesIndexForNonOfzFund()
    {
        var r = FundOutlookService.Compute(Input(ticker: "TMON", name: "Денежный рынок", explicitBenchmark: true, index: Rgbi(1, 0.15m)));
        r.Method.Should().Be(FundForecastMethod.IndexYield);
    }

    [Fact]
    public void Auto_OfzFundWithoutIndexData_FallsBackToTrailing()
    {
        var r = FundOutlookService.Compute(Input(ticker: "TOFZ", name: "Т-Капитал ОФЗ", index: null));
        r.Method.Should().Be(FundForecastMethod.TrailingReturn);
        r.ExpectedAnnualYieldFraction.Should().BeNull();
        r.UnavailableReason.Should().Contain("мало данных");
    }

    [Fact]
    public void ExplicitMethodWithoutData_IsNullWithReason_NoSilentFallback()
    {
        // Выплаты есть бы для Auto, но явный IndexYield данных не имеет.
        var r = FundOutlookService.Compute(Input(FundForecastMethod.IndexYield, dists: [D(60, 1m), D(30, 1m)], index: null));

        r.Method.Should().Be(FundForecastMethod.IndexYield);
        r.ExpectedAnnualYieldFraction.Should().BeNull();
        r.UnavailableReason.Should().Contain("RGBI");

        var d = FundOutlookService.Compute(Input(FundForecastMethod.Distributions));
        d.ExpectedAnnualYieldFraction.Should().BeNull();
        d.UnavailableReason.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Distributions_ExplicitWithSinglePayment_WarnsFew()
    {
        var r = FundOutlookService.Compute(Input(FundForecastMethod.Distributions, dists: [D(20, 1m)]));
        r.ExpectedAnnualYieldFraction.Should().NotBeNull();
        r.Warnings.Should().Contain(FundOutlookWarning.FewDistributions);
        r.Confidence.Should().Be(FundOutlookConfidence.Low);
    }

    [Fact]
    public void Trailing_ShortHistory_AnnualizesAndWarns()
    {
        var prices = new[] { new FundPricePoint(Today.AddDays(-120), 100m), new FundPricePoint(Today, 104m) };
        var r = FundOutlookService.Compute(Input(FundForecastMethod.TrailingReturn, prices: prices));
        r.Warnings.Should().Contain(FundOutlookWarning.ShortHistory);
        r.ExpectedAnnualYieldFraction.Should().BeApproximately((decimal)(Math.Pow(1.04, 365.0 / 120) - 1), 0.0001m);

        var tooShort = FundOutlookService.Compute(Input(FundForecastMethod.TrailingReturn,
            prices: [new FundPricePoint(Today.AddDays(-30), 100m), new FundPricePoint(Today, 101m)]));
        tooShort.ExpectedAnnualYieldFraction.Should().BeNull();
    }

    [Theory]
    [InlineData("fixed_income", "Т-Капитал ОФЗ", "TOFZ", true)]
    [InlineData("fixed_income", "Пассивный доход", "TPAY", false)]
    [InlineData("equity", "Т-Капитал ОФЗ", "TOFZ", false)]
    [InlineData("fixed_income", null, "TOFZ", true)]
    public void IsOfzOriented(string? focus, string? name, string? ticker, bool expected) =>
        FundOutlookService.IsOfzOriented(focus, name, ticker).Should().Be(expected);
}

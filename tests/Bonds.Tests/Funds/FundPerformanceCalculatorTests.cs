using Bonds.Core.Funds;
using Bonds.Core.Models;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Funds;

public class FundPerformanceCalculatorTests
{
    private static readonly DateOnly Today = new(2026, 10, 21);

    private static FundPricePoint P(DateOnly d, decimal v) => new(d, v);

    [Fact]
    public void Window_TotalReturn_IncludesDistributionsInHalfOpenRange()
    {
        var start = Today.AddDays(-90);
        var prices = new[] { P(start, 100m), P(Today, 101m) };
        var dists = new[]
        {
            new FundDistribution(start, null, 5m),                 // ровно на старте — НЕ входит (start, end]
            new FundDistribution(start.AddDays(10), null, 1.2m),
            new FundDistribution(start.AddDays(40), null, 1.2m),
            new FundDistribution(Today, null, 1.2m),               // ровно на конце — входит
            new FundDistribution(Today.AddDays(1), null, 9m),      // после конца — нет
        };

        var w = FundPerformanceCalculator.ComputeWindow(prices, dists, start, Today);

        w.Should().NotBeNull();
        w!.ReturnFraction.Should().Be(0.046m); // (101 + 3.6)/100 − 1
        w.Days.Should().Be(90);
        w.AnnualizedFraction.Should().BeApproximately((decimal)(Math.Pow(1.046, 365.0 / 90) - 1), 0.0001m);
    }

    [Theory]
    [InlineData(29, false)]
    [InlineData(30, true)]
    public void Annualize_ShortWindows_AreNull(int days, bool hasValue)
        => FundPerformanceCalculator.Annualize(0.01m, days).HasValue.Should().Be(hasValue);

    [Fact]
    public void Window_ShorterThan30Days_HasNullAnnualized()
    {
        var start = Today.AddDays(-20);
        var w = FundPerformanceCalculator.ComputeWindow([P(start, 100m), P(Today, 101m)], [], start, Today);
        w!.ReturnFraction.Should().Be(0.01m);
        w.AnnualizedFraction.Should().BeNull();
    }

    [Fact]
    public void Window_ForwardFillsStartPrice()
    {
        var start = Today.AddDays(-30);
        var prices = new[] { P(start.AddDays(-5), 100m), P(Today, 110m) }; // на дату старта цены нет — берём последнюю ≤
        var w = FundPerformanceCalculator.ComputeWindow(prices, [], start, Today);
        w!.ReturnFraction.Should().Be(0.10m);
    }

    [Fact]
    public void Window_NoPriceAtStart_ReturnsNull()
    {
        var start = Today.AddDays(-30);
        var prices = new[] { P(start.AddDays(5), 100m), P(Today, 110m) };
        FundPerformanceCalculator.ComputeWindow(prices, [], start, Today).Should().BeNull();
    }

    [Fact]
    public void Window_NoEndPriceOrBadDates_ReturnsNull()
    {
        FundPerformanceCalculator.ComputeWindow([], [], Today.AddDays(-30), Today).Should().BeNull();
        FundPerformanceCalculator.ComputeWindow([P(Today, 1m)], [], Today, Today).Should().BeNull();
    }

    [Fact]
    public void Compute_BuildsStandardWindows_AndSinceFirstBuy()
    {
        var prices = new List<FundPricePoint>();
        for (var d = Today.AddMonths(-13); d <= Today; d = d.AddDays(1)) prices.Add(P(d, 100m));
        var perf = FundPerformanceCalculator.Compute(prices, [], Today, Today.AddMonths(-2));

        perf.M1.Should().NotBeNull();
        perf.M3.Should().NotBeNull();
        perf.M6.Should().NotBeNull();
        perf.M12.Should().NotBeNull();
        perf.SinceFirstBuy.Should().NotBeNull();
        perf.SinceFirstBuy!.Days.Should().Be(Today.DayNumber - Today.AddMonths(-2).DayNumber);
        perf.M12!.ReturnFraction.Should().Be(0m);
    }

    [Fact]
    public void Compute_WithoutFirstBuy_SinceFirstBuyIsNull()
    {
        var perf = FundPerformanceCalculator.Compute([P(Today.AddMonths(-2), 100m), P(Today, 101m)], [], Today, null);
        perf.SinceFirstBuy.Should().BeNull();
        perf.M1.Should().NotBeNull("цена на старте окна 1м есть (прошлая, forward fill)");
        perf.M12.Should().BeNull("истории короче года — цены на старте окна нет");
    }
}

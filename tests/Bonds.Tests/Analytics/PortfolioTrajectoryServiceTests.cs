using Bonds.Core.Analytics;
using Bonds.Core.CashFlow;
using Bonds.Core.Models;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Analytics;

public class PortfolioTrajectoryServiceTests
{
    private static PortfolioHolding Holding(
        ulong positionId,
        decimal marketValue,
        decimal? ytmEffective = null) => new()
    {
        PositionId = positionId,
        InstrumentId = positionId,
        Quantity = 1,
        MarketValueRub = marketValue,
        CouponType = CouponType.Fixed,
        MaturityDate = new DateOnly(2030, 1, 1),
        HorizonDate = new DateOnly(2030, 1, 1),
        IsCalculatedToOffer = false,
        YtmEffective = ytmEffective,
        IsFloater = false,
        IsIndexed = false,
        IsEstimated = false,
        DataIncomplete = false,
    };

    private static MonthlyCashFlowSummary MonthSummary(DateOnly month, decimal netRub) => new()
    {
        Month = month,
        GrossRub = netRub,
        TaxRub = 0m,
        NetRub = netRub,
        CouponGrossRub = netRub,
        PrincipalGrossRub = 0m,
        HasEstimatedFlows = false,
    };

    private static MonthlyCashFlowSummary Summary(DateOnly month, decimal couponGross, decimal tax, decimal principalGross) => new()
    {
        Month = month,
        GrossRub = couponGross + principalGross,
        TaxRub = tax,
        NetRub = couponGross - tax + principalGross,
        CouponGrossRub = couponGross,
        PrincipalGrossRub = principalGross,
        HasEstimatedFlows = false,
    };

    private static DateOnly FirstOfCurrentMonth()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        return new DateOnly(today.Year, today.Month, 1);
    }

    // ─── T-3 (C-1/C-2/M-2): корректная модель учёта тела и дохода ─────────────

    [Fact]
    public void Redemption_DoesNotDoubleCountPrincipal()
    {
        // C-1: при погашении тело должно ПЕРЕЙТИ из стоимости бумаг в кэш (нейтрально для суммы),
        // а не прибавиться к кэшу поверх неизменной стоимости бумаг.
        var holdings = new[] { Holding(1, 1000m) };
        var firstOfMonth = FirstOfCurrentMonth();
        var summaries = new[]
        {
            Summary(firstOfMonth, couponGross: 50m, tax: 6.5m, principalGross: 0m),               // месяц 1 — купон
            Summary(firstOfMonth.AddMonths(1), couponGross: 0m, tax: 0m, principalGross: 1000m),   // месяц 2 — погашение тела
        };

        var result = PortfolioTrajectoryService.Compute(holdings, summaries, horizonMonths: 2, reinvestRate: 0m);

        // Месяц 2: бумага погашена (bondValue=0), стоимость = кэш = купон-нетто 43.5 + тело 1000.
        result.WithoutReinvest[1].PortfolioValueRub.Should().BeApproximately(1043.5m, 0.01m,
            "тело не должно учитываться дважды (≈2043 — это баг C-1)");
        result.WithoutReinvest[1].CumulativeIncomeRub.Should().Be(43.5m,
            "доход = только купон-нетто, возврат тела доходом не является (C-2)");
    }

    [Fact]
    public void Income_ExcludesPrincipal()
    {
        // C-2: возврат тела (амортизация/погашение) — возврат капитала, не доход.
        var holdings = new[] { Holding(1, 1000m) };
        var firstOfMonth = FirstOfCurrentMonth();
        var summaries = new[]
        {
            Summary(firstOfMonth.AddMonths(1), couponGross: 0m, tax: 0m, principalGross: 500m),
        };

        var result = PortfolioTrajectoryService.Compute(holdings, summaries, horizonMonths: 3, reinvestRate: 0m);

        result.WithoutReinvest.Should().OnlyContain(p => p.CumulativeIncomeRub == 0m,
            "возврат тела не должен попадать в накопленный доход");
    }

    [Fact]
    public void CurrentMonthIncluded()
    {
        // M-2: поток текущего месяца не должен теряться (итерация стартует с i=0).
        var holdings = new[] { Holding(1, 1000m) };
        var firstOfMonth = FirstOfCurrentMonth();
        var summaries = new[]
        {
            Summary(firstOfMonth, couponGross: 100m, tax: 13m, principalGross: 0m),
        };

        var result = PortfolioTrajectoryService.Compute(holdings, summaries, horizonMonths: 3, reinvestRate: 0m);

        result.WithoutReinvest[0].CumulativeIncomeRub.Should().Be(87m,
            "купон текущего месяца (100 − 13 налог) должен войти уже в первую точку");
    }

    [Fact]
    public void MonotonicWithNoFlows()
    {
        var holdings = new[] { Holding(1, 100_000m, 0.15m) };
        var result = PortfolioTrajectoryService.Compute(holdings, [], 36, 0.12m);

        result.WithReinvest.Should().HaveCount(36);
        result.WithoutReinvest.Should().HaveCount(36);

        foreach (var p in result.WithReinvest)
        {
            p.PortfolioValueRub.Should().Be(100_000m);
            p.CumulativeIncomeRub.Should().Be(0m);
        }
        foreach (var p in result.WithoutReinvest)
        {
            p.PortfolioValueRub.Should().Be(100_000m);
            p.CumulativeIncomeRub.Should().Be(0m);
        }
    }

    [Fact]
    public void WithReinvestGrowsFasterThanWithout()
    {
        var holdings = new[] { Holding(1, 100_000m, 0.15m) };

        // Build 12 monthly summaries with 1000 each, all in future months
        var today = DateOnly.FromDateTime(DateTime.Today);
        var summaries = Enumerable.Range(1, 12)
            .Select(i => MonthSummary(new DateOnly(today.Year, today.Month, 1).AddMonths(i), 1000m))
            .ToList();

        var result = PortfolioTrajectoryService.Compute(holdings, summaries, 12, 0.12m);

        result.WithReinvest[11].PortfolioValueRub
            .Should().BeGreaterThan(result.WithoutReinvest[11].PortfolioValueRub);
    }

    [Fact]
    public void DefaultReinvestRate_WeightedAverage()
    {
        var holdings = new[]
        {
            Holding(1, 100_000m, ytmEffective: 0.10m),
            Holding(2, 200_000m, ytmEffective: 0.20m),
        };

        var rate = PortfolioTrajectoryService.DefaultReinvestRate(holdings);

        // (0.10 * 100000 + 0.20 * 200000) / 300000 = 50000/300000 ≈ 0.1667
        rate.Should().BeApproximately(50_000m / 300_000m, 0.000001m);
    }

    [Fact]
    public void DefaultReinvestRate_FallbackWhenNoYtm()
    {
        var holdings = new[]
        {
            Holding(1, 100_000m, ytmEffective: null),
            Holding(2, 50_000m, ytmEffective: null),
        };

        var rate = PortfolioTrajectoryService.DefaultReinvestRate(holdings);

        rate.Should().Be(0.12m);
    }
}

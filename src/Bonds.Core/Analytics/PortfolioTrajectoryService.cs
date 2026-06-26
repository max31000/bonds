using Bonds.Core.CashFlow;

namespace Bonds.Core.Analytics;

public static class PortfolioTrajectoryService
{
    public static TrajectoryResult Compute(
        IEnumerable<PortfolioHolding> holdings,
        IEnumerable<MonthlyCashFlowSummary> monthlySummaries,
        int horizonMonths,
        decimal reinvestRate)
    {
        var holdingsList = holdings.ToList();
        var currentValue = holdingsList.Sum(h => h.MarketValueRub);

        var monthFlows = monthlySummaries
            .ToDictionary(m => m.Month, m => m.NetRub);

        var today = DateOnly.FromDateTime(DateTime.Today);
        var withReinvest = new List<TrajectoryPoint>();
        var withoutReinvest = new List<TrajectoryPoint>();

        decimal cashWith = 0m;
        decimal cashWithout = 0m;
        decimal cumulativeIncome = 0m;

        for (var i = 1; i <= horizonMonths; i++)
        {
            var monthStart = new DateOnly(today.Year, today.Month, 1).AddMonths(i);
            var netFlow = monthFlows.TryGetValue(monthStart, out var flow) ? flow : 0m;

            cumulativeIncome += netFlow;
            cashWith = (cashWith + netFlow) * (1 + reinvestRate / 12m);
            cashWithout += netFlow;

            var month = monthStart.ToString("yyyy-MM");
            withReinvest.Add(new TrajectoryPoint
            {
                Month = month,
                PortfolioValueRub = currentValue + cashWith,
                CumulativeIncomeRub = cumulativeIncome,
            });
            withoutReinvest.Add(new TrajectoryPoint
            {
                Month = month,
                PortfolioValueRub = currentValue + cashWithout,
                CumulativeIncomeRub = cumulativeIncome,
            });
        }

        return new TrajectoryResult
        {
            InitialValueRub = currentValue,
            WithReinvest = withReinvest,
            WithoutReinvest = withoutReinvest,
            ReinvestRateUsed = reinvestRate,
        };
    }

    public static decimal DefaultReinvestRate(IEnumerable<PortfolioHolding> holdings)
    {
        var relevant = holdings
            .Where(h => h.YtmEffective.HasValue && h.MarketValueRub > 0)
            .ToList();
        var totalWeight = relevant.Sum(h => h.MarketValueRub);
        if (totalWeight == 0) return 0.12m;
        return relevant.Sum(h => h.YtmEffective!.Value * h.MarketValueRub) / totalWeight;
    }
}

public sealed record TrajectoryPoint
{
    public required string Month { get; init; }
    public required decimal PortfolioValueRub { get; init; }
    public required decimal CumulativeIncomeRub { get; init; }
}

public sealed record TrajectoryResult
{
    public required decimal InitialValueRub { get; init; }
    public required IReadOnlyList<TrajectoryPoint> WithReinvest { get; init; }
    public required IReadOnlyList<TrajectoryPoint> WithoutReinvest { get; init; }
    public required decimal ReinvestRateUsed { get; init; }
}

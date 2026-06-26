using Bonds.Core.CashFlow;

namespace Bonds.Core.Analytics;

public static class PortfolioTrajectoryService
{
    public static TrajectoryResult Compute(
        IEnumerable<PortfolioHolding> holdings,
        IEnumerable<MonthlyCashFlowSummary> monthlySummaries,
        int horizonMonths,
        decimal reinvestRate,
        DateOnly asOf)
    {
        var holdingsList = holdings.ToList();
        var initialValue = holdingsList.Sum(h => h.MarketValueRub);

        var monthFlows = monthlySummaries
            .ToDictionary(m => m.Month, m => m);

        // T-5/M-3: «сегодня» приходит извне (московская бизнес-дата), а не из DateTime.Today —
        // так горизонт траектории согласован с остальными расчётными путями.
        var firstOfCurrentMonth = new DateOnly(asOf.Year, asOf.Month, 1);

        // Месячный множитель реинвеста: годовая эффективная ставка → месячный корень (M-4),
        // а не линейное /12. Для линии «без реинвеста» множитель = 1.
        var monthlyFactor = reinvestRate == 0m
            ? 1m
            : (decimal)Math.Pow(1.0 + (double)reinvestRate, 1.0 / 12.0);

        var withReinvest = BuildTrajectory(initialValue, firstOfCurrentMonth, horizonMonths, monthFlows, monthlyFactor);
        var withoutReinvest = BuildTrajectory(initialValue, firstOfCurrentMonth, horizonMonths, monthFlows, 1m);

        return new TrajectoryResult
        {
            InitialValueRub = initialValue,
            WithReinvest = withReinvest,
            WithoutReinvest = withoutReinvest,
            ReinvestRateUsed = reinvestRate,
        };
    }

    /// <summary>
    /// Корректная модель (T-3): два состояния — стоимость бумаг <c>bondValue</c> (старт = Σ рыночной
    /// стоимости) и <c>cash</c> (старт = 0). Возврат тела УХОДИТ из стоимости бумаг и ПРИХОДИТ в кэш
    /// (перенос, не новый капитал) — поэтому суммарная стоимость не скачет при погашении (C-1).
    /// Доходом считается только купон-нетто (C-2). Итерация включает текущий месяц (i=0, M-2).
    /// </summary>
    private static List<TrajectoryPoint> BuildTrajectory(
        decimal initialValue,
        DateOnly firstOfCurrentMonth,
        int horizonMonths,
        IReadOnlyDictionary<DateOnly, MonthlyCashFlowSummary> monthFlows,
        decimal monthlyFactor)
    {
        var points = new List<TrajectoryPoint>(horizonMonths);
        var bondValue = initialValue;
        var cash = 0m;
        var cumulativeIncome = 0m;

        for (var i = 0; i < horizonMonths; i++)
        {
            var monthStart = firstOfCurrentMonth.AddMonths(i);

            decimal netCoupon = 0m;
            decimal netPrincipal = 0m;
            if (monthFlows.TryGetValue(monthStart, out var m))
            {
                netCoupon = m.CouponGrossRub - m.TaxRub; // налог только на купон
                netPrincipal = m.PrincipalGrossRub;       // возврат тела налогом не облагается
            }

            bondValue -= netPrincipal;                 // тело уходит из стоимости бумаг
            cash += netCoupon + netPrincipal;          // …и приходит в кэш (перенос)
            cash *= monthlyFactor;                      // реинвест (для линии «без» factor = 1)
            cumulativeIncome += netCoupon;              // доход = только купоны

            points.Add(new TrajectoryPoint
            {
                Month = monthStart.ToString("yyyy-MM"),
                PortfolioValueRub = Math.Max(bondValue, 0m) + cash,
                CumulativeIncomeRub = cumulativeIncome,
            });
        }

        return points;
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

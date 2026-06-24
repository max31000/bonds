namespace Bonds.Core.Calculation;

/// <summary>
/// Дюрация Маколея, модифицированная дюрация, выпуклость и PVBP (spec §6.5–§6.7).
/// Использует ту же конвенцию дисконтирования Actual/365, что и <see cref="YtmCalculator"/>
/// (срок каждого потока — в годах от даты расчёта). Чистая функция, без I/O.
/// </summary>
public static class DurationCalculator
{
    public readonly record struct DurationResult(
        decimal MacaulayDurationYears,
        decimal ModifiedDuration,
        decimal Convexity,
        decimal Pvbp);

    /// <summary>
    /// Считает дюрацию/выпуклость/PVBP для денежного потока, дисконтированного по ставке
    /// <paramref name="yieldRate"/> (обычно — YTM той же бумаги).
    /// </summary>
    /// <param name="couponsPerYear">
    /// Число купонных периодов в году k — нужно для модифицированной дюрации
    /// (Маколей / (1 + y/k), spec §6.6). Если периодичность определить не удалось,
    /// передать 1 (годовой пересчёт — консервативное допущение).
    /// </param>
    public static DurationResult? Calculate(
        decimal dirtyPrice,
        decimal yieldRate,
        DateOnly asOf,
        IReadOnlyList<BondCashFlowItem> cashFlow,
        int couponsPerYear)
    {
        if (dirtyPrice <= 0m) return null;
        if (cashFlow.Count == 0) return null;
        if (couponsPerYear <= 0) couponsPerYear = 1;

        var y = (double)yieldRate;
        var price = (double)dirtyPrice;

        var flows = cashFlow
            .Select(c => (Years: (c.Date.DayNumber - asOf.DayNumber) / (double)YtmCalculator.DaysInYear, Amount: (double)c.TotalAmount))
            .Where(f => f.Years > 0)
            .ToList();

        if (flows.Count == 0) return null;

        var weightedTimeSum = 0.0;
        var weightedTimeSquaredSum = 0.0;
        var pvSum = 0.0;

        foreach (var (t, amount) in flows)
        {
            var pv = amount / Math.Pow(1.0 + y, t);
            pvSum += pv;
            weightedTimeSum += t * pv;
            // Выпуклость: средневзвешенная t(t+1) приведённых потоков / (P * (1+y)^2) — стандартная
            // формула для дискретного годового компаундирования.
            weightedTimeSquaredSum += t * (t + 1.0) * pv;
        }

        if (pvSum <= 0) return null;

        var macaulayYears = weightedTimeSum / pvSum;
        var modifiedDuration = macaulayYears / (1.0 + y / couponsPerYear);
        var convexity = weightedTimeSquaredSum / (pvSum * Math.Pow(1.0 + y, 2));
        var pvbp = (decimal)modifiedDuration * dirtyPrice * 0.0001m;

        return new DurationResult(
            (decimal)macaulayYears,
            (decimal)modifiedDuration,
            (decimal)convexity,
            pvbp);
    }
}

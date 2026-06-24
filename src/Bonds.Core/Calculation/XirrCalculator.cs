namespace Bonds.Core.Calculation;

/// <summary>
/// XIRR (внутренняя норма доходности по нерегулярным датам) по журналу операций (spec §6.9).
/// Покупки — со знаком минус, купоны/продажи/амортизации — плюс (знак выставляет вызывающий
/// слой/этап 06 — этот калькулятор просто решает уравнение для уже подписанных потоков), плюс
/// терминальный поток текущей стоимости позиции/портфеля на дату оценки. Тоже Ньютон-Рафсон
/// с фолбэком на бисекцию (<see cref="IrrSolver"/>). Чистая функция, без I/O.
/// </summary>
public static class XirrCalculator
{
    public const int DaysInYear = 365;

    public readonly record struct CashFlow(DateOnly Date, decimal Amount);

    public readonly record struct XirrResult(decimal Rate, bool ConvergedByNewton);

    /// <summary>
    /// Считает XIRR. Возвращает null, если потоков меньше двух, все суммы одного знака
    /// (нет искомого корня — типичный мусорный вход), либо решатель не сходится.
    /// </summary>
    public static XirrResult? Calculate(IReadOnlyList<CashFlow> cashFlows)
    {
        if (cashFlows.Count < 2) return null;
        if (cashFlows.All(c => c.Amount >= 0) || cashFlows.All(c => c.Amount <= 0)) return null;

        var baseDate = cashFlows.Min(c => c.Date);

        var flows = cashFlows
            .Select(c => (Years: (c.Date.DayNumber - baseDate.DayNumber) / (double)DaysInYear, Amount: (double)c.Amount))
            .ToList();

        double Npv(double r)
        {
            var sum = 0.0;
            foreach (var (years, amount) in flows)
            {
                sum += amount / Math.Pow(1.0 + r, years);
            }
            return sum;
        }

        double NpvPrime(double r)
        {
            var sum = 0.0;
            foreach (var (years, amount) in flows)
            {
                if (years == 0) continue;
                sum += -years * amount / Math.Pow(1.0 + r, years + 1.0);
            }
            return sum;
        }

        var result = IrrSolver.Solve(
            r => (decimal)Npv((double)r),
            r => (decimal)NpvPrime((double)r),
            initialGuess: 0.1m,
            lowerBound: -0.99m,
            upperBound: 10.0m);

        if (!result.Converged) return null;

        return new XirrResult(result.Rate, !result.UsedBisectionFallback);
    }
}

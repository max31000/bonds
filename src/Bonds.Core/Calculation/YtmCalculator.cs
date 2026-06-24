namespace Bonds.Core.Calculation;

/// <summary>
/// YTM (доходность к погашению/оферте) по текущей грязной цене (spec §6.3, §13). Решает IRR:
/// ставку y, при которой сумма приведённых стоимостей денежного потока равна грязной цене.
/// Денежный поток (включая убывающий номинал при амортизации) строится заранее
/// <see cref="BondCashFlowBuilder"/> — этот класс знает только про дисконтирование.
/// <para>
/// Конвенция day-count: Actual/365 (фактическое число календарных дней между датой расчёта и
/// потоком, делённое на 365) — общепринятая практика российских облигационных калькуляторов
/// (MOEX, Cbonds, dohod.ru) для рублёвых бумаг. Дисконт-фактор для эффективной (сложной годовой)
/// доходности: <c>(1 + y) ^ (days/365)</c>.
/// </para>
/// Чистая функция, без I/O; гарантия сходимости — Ньютон-Рафсон с фолбэком на бисекцию
/// (<see cref="IrrSolver"/>), как того требует plan/05 Часть A.3.
/// </summary>
public static class YtmCalculator
{
    public const int DaysInYear = 365;

    public readonly record struct YtmResult(decimal EffectiveYield, decimal SimpleYield, bool ConvergedByNewton);

    /// <summary>
    /// Считает эффективную и простую YTM. Возвращает null, если поток пуст, цена не положительна,
    /// либо итоговая ставка решателя выходит за разумные пределы (защита от мусорных входов —
    /// в этом случае вызывающий слой должен трактовать результат как недостоверный, не бросая
    /// исключение, см. spec §4.4).
    /// </summary>
    /// <param name="dirtyPrice">Грязная цена на одну облигацию.</param>
    /// <param name="asOf">Дата расчёта.</param>
    /// <param name="cashFlow">Денежный поток после <paramref name="asOf"/> (купоны + номинал).</param>
    public static YtmResult? Calculate(decimal dirtyPrice, DateOnly asOf, IReadOnlyList<BondCashFlowItem> cashFlow)
    {
        if (dirtyPrice <= 0m) return null;
        if (cashFlow.Count == 0) return null;
        if (cashFlow.Any(c => !c.IsKnown)) return null; // неизвестные потоки — расчёт YTM не имеет смысла

        var flows = cashFlow
            .Select(c => (Days: (double)(c.Date.DayNumber - asOf.DayNumber), Amount: (double)c.TotalAmount))
            .Where(f => f.Days > 0)
            .ToList();

        if (flows.Count == 0) return null;

        var price = (double)dirtyPrice;

        double Npv(double y)
        {
            var sum = 0.0;
            foreach (var (days, amount) in flows)
            {
                sum += amount / Math.Pow(1.0 + y, days / DaysInYear);
            }
            return sum - price;
        }

        double NpvPrime(double y)
        {
            var sum = 0.0;
            foreach (var (days, amount) in flows)
            {
                var t = days / DaysInYear;
                sum += -t * amount / Math.Pow(1.0 + y, t + 1.0);
            }
            return sum;
        }

        var solverResult = IrrSolver.Solve(
            r => (decimal)Npv((double)r),
            r => (decimal)NpvPrime((double)r),
            initialGuess: 0.1m,
            lowerBound: -0.99m,
            upperBound: 10.0m);

        if (!solverResult.Converged) return null;

        var effectiveYield = solverResult.Rate;

        // Простая (линейная) доходность: суммарный поток / цена, приведённый к годовой ставке
        // линейно по средневзвешенному сроку — общепринятое упрощение "simple yield to maturity":
        // (totalCashFlow - price) / price / (weightedAverageYears), где числитель — суммарная
        // доходность за весь период, делённая на средний срок в годах.
        var totalAmount = flows.Sum(f => f.Amount);
        var weightedDays = flows.Sum(f => f.Days * f.Amount) / totalAmount;
        var years = weightedDays / DaysInYear;

        decimal simpleYield;
        if (years > 0)
        {
            simpleYield = (decimal)((totalAmount - price) / price / years);
        }
        else
        {
            simpleYield = effectiveYield;
        }

        return new YtmResult(effectiveYield, simpleYield, !solverResult.UsedBisectionFallback);
    }
}

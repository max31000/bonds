using Bonds.Core.Models;

namespace Bonds.Core.Calculation;

/// <summary>
/// Текущая купонная доходность — заменяет YTM для флоатеров и индексируемых бумаг (spec §6
/// «Краевые случаи»: «текущий купон, приведённый к году, к грязной цене»). Чистая функция,
/// без I/O.
/// </summary>
public static class CurrentYieldCalculator
{
    /// <summary>
    /// Берёт последний известный купон (на дату расчёта или предшествующий ей — "текущая ставка"),
    /// приводит его к годовой сумме исходя из периодичности (PeriodDays либо оценки по графику),
    /// и делит на грязную цену.
    /// </summary>
    /// <returns>
    /// Null, если нет ни одного известного купона, по которому можно судить о текущей ставке.
    /// </returns>
    public static decimal? Calculate(DateOnly asOf, decimal dirtyPrice, IReadOnlyCollection<CouponSchedule> coupons)
    {
        if (dirtyPrice <= 0m) return null;

        var known = coupons.Where(c => c.IsKnown && c.ValueRub.HasValue).OrderBy(c => c.CouponDate).ToList();
        if (known.Count == 0) return null;

        // "Текущий" купон — ближайший известный к asOf: либо текущий незавершённый период
        // (последний известный купон с датой >= asOf), либо, если все известные купоны в прошлом
        // (нет данных по будущему пересчёту), берём последний известный как лучшую оценку ставки.
        var current = known.FirstOrDefault(c => c.CouponDate >= asOf) ?? known[^1];

        var periodDays = current.PeriodDays ?? CouponFrequencyEstimator.EstimateCouponsPerYear(coupons) switch
        {
            var k when k > 0 => 365 / k,
            _ => 365,
        };

        if (periodDays <= 0) periodDays = 365;

        var annualizedCoupon = current.ValueRub!.Value * 365m / periodDays;

        return annualizedCoupon / dirtyPrice;
    }
}

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
    /// Берёт ближайший к дате расчёта известный купон с РЕГУЛЯРНЫМ периодом ("текущая ставка"),
    /// приводит его к годовой сумме (×365/период) и делит на грязную цену. Аномальные короткие/
    /// длинные крайние периоды (нерегулярный первый/последний купон) игнорируются — иначе оценка
    /// ставки «гуляет» (T-8/L-3): например, короткий первый огрызок аннуализировался бы ×365/5.
    /// </summary>
    /// <returns>
    /// Null, если нет ни одного известного купона, по которому можно судить о текущей ставке.
    /// </returns>
    public static decimal? Calculate(DateOnly asOf, decimal dirtyPrice, IReadOnlyCollection<CouponSchedule> coupons)
    {
        if (dirtyPrice <= 0m) return null;

        var known = coupons.Where(c => c.IsKnown && c.ValueRub.HasValue).OrderBy(c => c.CouponDate).ToList();
        if (known.Count == 0) return null;

        var medianPeriod = MedianPeriodDays(known);
        var representative = SelectRepresentative(known, asOf, medianPeriod);

        // Период регулярного представителя; если он неизвестен — устойчивый фолбэк на частоту k.
        var periodDays = representative.PeriodDays is int p && p > 0
            ? p
            : 365 / Math.Max(1, CouponFrequencyEstimator.EstimateCouponsPerYear(coupons));
        if (periodDays <= 0) periodDays = 365;

        var annualizedCoupon = representative.ValueRub!.Value * 365m / periodDays;

        return annualizedCoupon / dirtyPrice;
    }

    /// <summary>
    /// Представительный купон для оценки текущей ставки: ближайший к asOf известный купон с
    /// регулярным периодом (в пределах [0.5..1.5]× медианного), сначала текущий/будущий, затем
    /// прошлый. Если регулярных нет (или периоды неизвестны) — ближайший к asOf как лучшая оценка.
    /// </summary>
    private static CouponSchedule SelectRepresentative(List<CouponSchedule> known, DateOnly asOf, int medianPeriod)
    {
        bool IsRegular(CouponSchedule c) =>
            medianPeriod > 0 && c.PeriodDays is int p && p > 0
            && p >= medianPeriod * 0.5 && p <= medianPeriod * 1.5;

        var regularFuture = known.FirstOrDefault(c => c.CouponDate >= asOf && IsRegular(c));
        if (regularFuture is not null) return regularFuture;

        var regularPast = known.LastOrDefault(c => c.CouponDate < asOf && IsRegular(c));
        if (regularPast is not null) return regularPast;

        // Нет ни одного регулярного периода — ближайший известный (текущий/будущий, иначе последний).
        return known.FirstOrDefault(c => c.CouponDate >= asOf) ?? known[^1];
    }

    private static int MedianPeriodDays(List<CouponSchedule> known)
    {
        var periods = known
            .Where(c => c.PeriodDays is int p && p > 0)
            .Select(c => c.PeriodDays!.Value)
            .OrderBy(p => p)
            .ToList();
        return periods.Count == 0 ? 0 : periods[periods.Count / 2];
    }
}

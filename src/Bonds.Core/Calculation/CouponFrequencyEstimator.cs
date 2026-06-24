using Bonds.Core.Models;

namespace Bonds.Core.Calculation;

/// <summary>
/// Оценка числа купонных периодов в году k — нужна для модифицированной дюрации
/// (Маколей / (1 + y/k), spec §6.6). Чистая функция, без I/O.
/// </summary>
public static class CouponFrequencyEstimator
{
    /// <summary>
    /// Оценивает k по среднему числу дней между соседними купонами в графике. Если данных
    /// недостаточно (меньше двух купонов с известной датой), возвращает 1 (годовой пересчёт —
    /// консервативное допущение, явно фиксируем как решение по умолчанию).
    /// </summary>
    public static int EstimateCouponsPerYear(IReadOnlyCollection<CouponSchedule> coupons)
    {
        var dates = coupons.Select(c => c.CouponDate).OrderBy(d => d).Distinct().ToList();
        if (dates.Count < 2) return 1;

        var gaps = new List<int>();
        for (var i = 1; i < dates.Count; i++)
        {
            var gap = dates[i].DayNumber - dates[i - 1].DayNumber;
            if (gap > 0) gaps.Add(gap);
        }

        if (gaps.Count == 0) return 1;

        var avgGapDays = gaps.Average();
        if (avgGapDays <= 0) return 1;

        var periodsPerYear = (int)Math.Round(365.0 / avgGapDays, MidpointRounding.AwayFromZero);
        return Math.Clamp(periodsPerYear, 1, 12);
    }
}

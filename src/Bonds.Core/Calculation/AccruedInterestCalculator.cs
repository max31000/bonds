using Bonds.Core.Models;

namespace Bonds.Core.Calculation;

/// <summary>
/// НКД (накопленный купонный доход) — пропорциональное накопление между датами купонов
/// (spec §6.1, §13). Используется как fallback, когда НКД из T-Invest недоступен (plan/05
/// Часть A.1) — например, для бумаг без открытой позиции (кандидаты на замену) или для сверки.
/// Чистая функция, без I/O.
/// </summary>
public static class AccruedInterestCalculator
{
    /// <summary>
    /// Считает НКД на дату <paramref name="asOf"/> исходя из графика купонов.
    /// Находит купонный период, в который попадает <paramref name="asOf"/> (между предыдущим
    /// и следующим купоном), и пропорционально накопленному числу дней относительно длины
    /// периода считает долю следующего купона.
    /// </summary>
    /// <param name="asOf">Дата расчёта.</param>
    /// <param name="coupons">Полный график купонов инструмента (отсортирован внутри метода).</param>
    /// <returns>
    /// НКД на одну облигацию, либо null, если расчёт невозможен (нет купонов вокруг даты,
    /// либо ближайший будущий купон неизвестен — например, флоатер за горизонтом пересчёта).
    /// Возврат null — осознанный выбор: вызывающий слой решает, как пометить недостоверность
    /// (spec §4.4 — не подставлять значение молча).
    /// </returns>
    public static decimal? Calculate(DateOnly asOf, IReadOnlyCollection<CouponSchedule> coupons)
    {
        if (coupons.Count == 0) return null;

        var sorted = coupons.OrderBy(c => c.CouponDate).ToList();

        // Купон, который будет выплачен следующим после asOf (НКД копится "в счёт" него).
        var nextCoupon = sorted.FirstOrDefault(c => c.CouponDate > asOf);
        if (nextCoupon is null) return null; // горизонт за пределами графика — недостоверно

        if (!nextCoupon.ValueRub.HasValue || !nextCoupon.IsKnown)
        {
            // Сумма следующего купона неизвестна (флоатер/индексируемая бумага за горизонтом
            // пересчёта) — пропорциональный НКД посчитать нельзя без допущений по ставке.
            return null;
        }

        // Предыдущий купон (старт текущего периода). Если купонов до asOf не было —
        // считаем, что период начался от даты, предшествующей самому первому купону минус
        // период; на практике для размещённых бумаг такого почти не бывает, но чтобы не падать,
        // в этом случае используем PeriodDays следующего купона как длину периода и считаем,
        // что период начался PeriodDays дней до его даты.
        var previousCoupon = sorted.LastOrDefault(c => c.CouponDate <= asOf);

        DateOnly periodStart;
        int periodLengthDays;

        if (previousCoupon is not null)
        {
            periodStart = previousCoupon.CouponDate;
            periodLengthDays = nextCoupon.PeriodDays
                ?? (nextCoupon.CouponDate.DayNumber - previousCoupon.CouponDate.DayNumber);
        }
        else if (nextCoupon.PeriodDays.HasValue)
        {
            periodLengthDays = nextCoupon.PeriodDays.Value;
            periodStart = nextCoupon.CouponDate.AddDays(-periodLengthDays);
        }
        else
        {
            return null; // недостаточно данных, чтобы оценить длину периода
        }

        if (periodLengthDays <= 0) return null;

        var daysAccrued = asOf.DayNumber - periodStart.DayNumber;
        if (daysAccrued < 0) daysAccrued = 0;
        if (daysAccrued > periodLengthDays) daysAccrued = periodLengthDays;

        return Math.Round(nextCoupon.ValueRub.Value * daysAccrued / periodLengthDays, 6);
    }

    /// <summary>Грязная цена = чистая цена + НКД (spec §6.2).</summary>
    public static decimal DirtyPrice(decimal cleanPrice, decimal accruedInterest) => cleanPrice + accruedInterest;
}

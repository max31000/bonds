using Bonds.Core.Models;

namespace Bonds.Core.Calculation;

/// <summary>
/// Строит будущий денежный поток одной облигации (на одну штуку, номинал = 1 облигация)
/// от даты расчёта до заданного горизонта (погашение либо оферта — см.
/// <see cref="OfferCutoffResolver"/>), учитывая амортизацию (plan/05 Часть A.3, Часть B).
/// Купоны начисляются на остаточный номинал: каждая выплата амортизации после своей даты
/// уменьшает базу для последующих купонов, если сами суммы купонов из расписания уже не
/// рассчитаны на остаток (расписание MOEX обычно уже отдаёт суммы в рублях на бумагу, поэтому
/// мы используем их as-is, но проверяем consistency с остаточным номиналом для PrincipalAmount
/// на последней дате горизонта — погашение остатка, а не полного номинала).
/// </summary>
public static class BondCashFlowBuilder
{
    /// <summary>
    /// Строит поток для горизонта "asOf эксклюзивно" до <paramref name="horizonDate"/> включительно.
    /// </summary>
    /// <param name="faceValue">Номинал бумаги (1 шт.).</param>
    /// <param name="asOf">Дата расчёта (не включается в поток).</param>
    /// <param name="horizonDate">Дата окончания горизонта (погашение или оферта).</param>
    /// <param name="coupons">Полный график купонов инструмента.</param>
    /// <param name="amortizations">Полный график амортизаций инструмента (может быть пустым).</param>
    public static List<BondCashFlowItem> Build(
        decimal faceValue,
        DateOnly asOf,
        DateOnly horizonDate,
        IEnumerable<CouponSchedule> coupons,
        IEnumerable<AmortizationSchedule>? amortizations)
    {
        var amortList = (amortizations ?? Enumerable.Empty<AmortizationSchedule>())
            .Where(a => a.Date > asOf && a.Date <= horizonDate)
            .OrderBy(a => a.Date)
            .ToList();

        var couponList = coupons
            .Where(c => c.CouponDate > asOf && c.CouponDate <= horizonDate)
            .OrderBy(c => c.CouponDate)
            .ToList();

        // Остаточный номинал к погашению/оферте после всех амортизаций, попавших в горизонт.
        var amortizedTotal = amortList.Sum(a => a.AmountRub);
        var remainingPrincipalAtHorizon = Math.Max(0m, faceValue - amortizedTotal);

        var byDate = new SortedDictionary<DateOnly, (decimal Coupon, decimal Principal, bool CouponKnown)>();

        foreach (var coupon in couponList)
        {
            var entry = byDate.TryGetValue(coupon.CouponDate, out var existing)
                ? existing
                : (Coupon: 0m, Principal: 0m, CouponKnown: true);

            var amount = coupon.ValueRub ?? 0m;
            byDate[coupon.CouponDate] = (entry.Coupon + amount, entry.Principal, entry.CouponKnown && coupon.IsKnown);
        }

        foreach (var amort in amortList)
        {
            var entry = byDate.TryGetValue(amort.Date, out var existing)
                ? existing
                : (Coupon: 0m, Principal: 0m, CouponKnown: true);

            byDate[amort.Date] = (entry.Coupon, entry.Principal + amort.AmountRub, entry.CouponKnown);
        }

        // Погашение/оферта на горизонте — возврат остатка номинала, если он ещё не был
        // полностью погашен прошедшими амортизациями. Если горизонт не позже даты расчёта
        // (рассинхрон данных о погашении/оферте — например, дата уже прошла), поток пуст:
        // выплата "в прошлое" не может быть будущим денежным потоком.
        if (remainingPrincipalAtHorizon > 0m && horizonDate > asOf)
        {
            var entry = byDate.TryGetValue(horizonDate, out var existing)
                ? existing
                : (Coupon: 0m, Principal: 0m, CouponKnown: true);

            byDate[horizonDate] = (entry.Coupon, entry.Principal + remainingPrincipalAtHorizon, entry.CouponKnown);
        }

        return byDate
            .Select(kv => new BondCashFlowItem(kv.Key, kv.Value.Coupon, kv.Value.Principal, kv.Value.CouponKnown))
            .OrderBy(i => i.Date)
            .ToList();
    }
}

using Bonds.Core.Calculation;

namespace Bonds.Core.Income;

/// <summary>
/// Калькулятор дохода по ОДНОМУ активу (облигация или фонд) по его журналу — задача 52. Чистая функция.
/// <para>
/// <b>Метод — average cost</b> (как <see cref="Bonds.Core.Analytics.PositionCostBasisService"/>: не FIFO, налоговой
/// отчётности не заменяет; обоснование — в doc-comment того сервиса).
/// </para>
/// <para>
/// <b>НКД.</b> Покупка облигации оплачивает продавцу НКД (сумма = чистая цена + НКД), первый купон его возвращает.
/// Поэтому НКД покупки выносится из себестоимости (<c>cleanCost = |amount| − accrued</c>) и считается «уплаченным»,
/// а в «честном купонном доходе» вычитается. НКД продажи вынесен из выручки (<c>proceedsClean = amount − accrued</c>).
/// НКД «известен», если задан и <c>0 ≤ accrued &lt; |amount|</c>; иначе (null или ≥ суммы — допущение «Payment включает
/// НКД» нарушено) НКД игнорируется, весь amount идёт в себестоимость/выручку, ставится <c>AccruedUnknown</c>
/// (для фондов НКД не бывает — флаг не ставится).
/// </para>
/// <para>
/// <b>Закрытая позиция.</b> Если журнал считает остаток &gt; 0, а позиции уже нет (погашение/вывод без записи в журнале),
/// невозвращённая себестоимость списывается в реализованный убыток и ставится <c>HasUnknownLots</c> — иначе деньги,
/// ушедшие на покупку, «пропали» бы из итога. <c>SecurityOut</c> тоже списывает среднюю себестоимость выведенного в
/// реализованный результат (бумаги ушли без поступления денег). Погашение в журнале перекрывает остаток позиции
/// (стоимость 0, приток уже в журнале) — то же правило, что у снимков.
/// </para>
/// </summary>
public static class PositionIncomeCalculator
{
    /// <summary>Минимальный срок владения для XIRR (короткий срок даёт абсурдные годовые).</summary>
    public const int MinXirrHoldingDays = 30;

    private const decimal QuantityEpsilon = 1e-6m;

    public static PositionIncome Calculate(
        AssetKind kind, ulong assetId, IEnumerable<LedgerEntry> entries,
        decimal currentQuantity, decimal marketValueCleanRub, decimal accruedNowRub, DateOnly asOf) =>
        CalculateDetailed(kind, assetId, entries, currentQuantity, marketValueCleanRub, accruedNowRub, asOf).Income;

    /// <summary>
    /// То же, плюс пошаговые строки. <paramref name="currentQuantity"/> — фактический остаток (позиция брокера),
    /// <paramref name="marketValueCleanRub"/> — стоимость остатка по ЧИСТОЙ цене, <paramref name="accruedNowRub"/> — НКД остатка
    /// (оба — РУБЛИ на весь остаток).
    /// </summary>
    public static AssetIncome CalculateDetailed(
        AssetKind kind, ulong assetId, IEnumerable<LedgerEntry> entries,
        decimal currentQuantity, decimal marketValueCleanRub, decimal accruedNowRub, DateOnly asOf)
    {
        var ordered = entries.OrderBy(e => e.Date).ThenBy(e => e.SourceId).ToList();

        decimal qty = 0m, cost = 0m, invested = 0m, accruedPaid = 0m, accruedReceived = 0m;
        decimal realized = 0m, incomeGross = 0m, taxes = 0m, fees = 0m, principalReturned = 0m, sumAmounts = 0m;
        bool hasUnknownLots = false, accruedUnknown = false, hasRedemption = false;
        var rows = new List<LedgerRow>(ordered.Count);

        foreach (var e in ordered)
        {
            var amount = e.AmountRub;
            sumAmounts += amount;
            decimal rowRealized = 0m, accruedUsed = 0m;
            var q = Math.Abs(e.Quantity ?? 0m);

            switch (e.Kind)
            {
                case LedgerEntryKind.Buy:
                {
                    var gross = Math.Abs(amount);
                    var acc = KnownAccrued(e.AccruedRub, gross);
                    if (acc is null && kind == AssetKind.Bond) accruedUnknown = true;
                    accruedUsed = acc ?? 0m;
                    if (q <= 0m) hasUnknownLots = true; // деньги ушли, а количество неизвестно
                    qty += q;
                    cost += gross - accruedUsed;
                    invested += gross;
                    accruedPaid += accruedUsed;
                    break;
                }
                case LedgerEntryKind.Sell:
                {
                    var acc = KnownAccrued(e.AccruedRub, Math.Abs(amount));
                    if (acc is null && kind == AssetKind.Bond) accruedUnknown = true;
                    accruedUsed = acc ?? 0m;
                    if (q <= 0m)
                    {
                        hasUnknownLots = true; // количество продажи неизвестно — себестоимость не списываем
                    }
                    else if (q > qty + QuantityEpsilon)
                    {
                        // Продано больше известного остатка (куплено до начала истории) — клампим, остаток не уходит в минус.
                        hasUnknownLots = true;
                    }
                    q = Math.Min(q, qty);
                    var avg = qty > 0m ? cost / qty : 0m;
                    rowRealized = (amount - accruedUsed) - avg * q;
                    cost -= avg * q;
                    qty -= q;
                    accruedReceived += accruedUsed;
                    break;
                }
                case LedgerEntryKind.Income:
                    incomeGross += amount;
                    break;
                case LedgerEntryKind.Tax:
                    taxes += amount;
                    break;
                case LedgerEntryKind.Fee:
                    fees += amount;
                    break;
                case LedgerEntryKind.PrincipalReturn:
                    principalReturned += amount;
                    if (cost >= amount)
                    {
                        cost -= amount;
                    }
                    else
                    {
                        rowRealized = amount - cost; // возврат больше остатка себестоимости — избыток это прибыль
                        cost = 0m;
                    }
                    break;
                case LedgerEntryKind.Redemption:
                    rowRealized = amount - cost;
                    cost = 0m;
                    qty = 0m;
                    hasRedemption = true;
                    principalReturned += amount;
                    break;
                case LedgerEntryKind.SecurityIn:
                    // Ввод бумаг: цена неизвестна — количество растёт, себестоимость нет; позиция помечается.
                    hasUnknownLots = true;
                    qty += q;
                    rowRealized = amount; // обычно 0 (денег нет); ненулевая сумма не должна потеряться из итога
                    break;
                case LedgerEntryKind.SecurityOut:
                {
                    hasUnknownLots = true;
                    var outQty = Math.Min(q, qty);
                    var avg = qty > 0m ? cost / qty : 0m;
                    rowRealized = amount - avg * outQty; // выведенная себестоимость списывается (денег за бумаги не получено)
                    cost -= avg * outQty;
                    qty -= outQty;
                    break;
                }
            }

            realized += rowRealized;
            rows.Add(new LedgerRow(e, qty, qty > 0m ? cost / qty : 0m, rowRealized, accruedUsed));
        }

        // Погашение перекрывает остаток позиции: бумаги погашены, приток уже в журнале.
        var redeemedOverride = hasRedemption && qty <= QuantityEpsilon;
        var qtyNow = redeemedOverride ? 0m : currentQuantity;
        var mv = qtyNow > 0m ? marketValueCleanRub : 0m;
        var accNow = qtyNow > 0m ? accruedNowRub : 0m;

        if (Math.Abs(qty - qtyNow) > QuantityEpsilon) hasUnknownLots = true;

        var status = qtyNow > 0m
            ? PositionIncomeStatus.Open
            : hasRedemption ? PositionIncomeStatus.Redeemed : PositionIncomeStatus.Closed;

        if (status != PositionIncomeStatus.Open && Math.Abs(cost) > 0.000001m)
        {
            // Журнал ещё «держит» бумаги, которых на счёте нет: списываем невозвращённую себестоимость в убыток (см. doc-comment).
            hasUnknownLots = true;
            realized -= cost;
            if (rows.Count > 0)
            {
                var last = rows[^1];
                rows[^1] = last with { RealizedPnlRub = last.RealizedPnlRub - cost };
            }
            cost = 0m;
        }

        decimal? unrealized = status == PositionIncomeStatus.Open ? mv - cost : null;
        var netCoupon = incomeGross - accruedPaid + accruedReceived + accNow;
        var total = sumAmounts + mv + accNow;

        var firstEntry = ordered.FirstOrDefault(e => e.Kind is LedgerEntryKind.Buy or LedgerEntryKind.SecurityIn) ?? ordered.FirstOrDefault();
        var firstDate = firstEntry is null ? asOf : DateOnly.FromDateTime(firstEntry.Date);
        var lastDate = ordered.Count == 0 ? asOf : DateOnly.FromDateTime(ordered[^1].Date);
        var endDate = status == PositionIncomeStatus.Open ? asOf : lastDate;
        var holdingDays = Math.Max(0, endDate.DayNumber - firstDate.DayNumber);

        decimal? xirr = null;
        if (holdingDays >= MinXirrHoldingDays)
        {
            var flows = ordered
                .Where(e => e.AmountRub != 0m)
                .Select(e => new XirrCalculator.CashFlow(DateOnly.FromDateTime(e.Date), e.AmountRub))
                .ToList();
            if (mv + accNow > 0m) flows.Add(new XirrCalculator.CashFlow(asOf, mv + accNow));
            xirr = XirrCalculator.Calculate(flows)?.Rate;
        }

        var income = new PositionIncome
        {
            AssetKind = kind,
            AssetId = assetId,
            Status = status,
            FirstDate = firstDate,
            LastDate = lastDate,
            HoldingDays = holdingDays,
            QuantityNow = qtyNow,
            InvestedTotalRub = invested,
            CostRemainingRub = cost,
            MarketValueCleanRub = mv,
            AccruedNowRub = accNow,
            UnrealizedPnlRub = unrealized,
            RealizedPnlRub = realized,
            IncomeGrossRub = incomeGross,
            AccruedPaidRub = accruedPaid,
            AccruedReceivedRub = accruedReceived,
            NetCouponIncomeRub = netCoupon,
            TaxesRub = taxes,
            FeesRub = fees,
            PrincipalReturnedRub = principalReturned,
            TotalResultRub = total,
            TotalReturnFraction = invested > 0m ? total / invested : null,
            XirrFraction = xirr,
            HasUnknownLots = hasUnknownLots,
            AccruedUnknown = accruedUnknown,
        };
        return new AssetIncome(income, rows);
    }

    /// <summary>НКД известен, если задан и 0 ≤ НКД &lt; |сумма операции|; иначе null (допущение «Payment включает НКД» не подтверждено).</summary>
    private static decimal? KnownAccrued(decimal? accrued, decimal grossAmount) =>
        accrued is { } a && a >= 0m && a < grossAmount ? a : null;
}

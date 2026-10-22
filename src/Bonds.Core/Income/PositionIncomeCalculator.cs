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
/// <para>
/// <b>Единицы с неизвестной себестоимостью</b> (куплены до начала истории, пришли вводом бумаг или — у валюты — не покупкой,
/// а, например, выручкой/купоном в этой валюте) результата НЕ дают: себестоимость 0 превратила бы всю выручку в «прибыль»
/// (баг 2026-10: юани, проданные без покупки в журнале, давали +217 тыс. ₽). Пул журнала делится на известные (с себестоимостью)
/// и неизвестные единицы; продажа списывает их пропорционально, продажа сверх пула — целиком неизвестная часть. Выручка
/// неизвестной части — <c>UnknownCostProceedsRub</c>, стоимость неизвестной части остатка — <c>UnknownCostHoldingsRub</c>;
/// обе считаются внесёнными «по рынку» и исключены из итога и XIRR (ставится <c>HasUnknownLots</c>).
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

        // known — единицы с известной себестоимостью (cost относится только к ним), unknown — без неё; пул журнала = known + unknown.
        decimal known = 0m, unknown = 0m, cost = 0m, invested = 0m, accruedPaid = 0m, accruedReceived = 0m;
        decimal realized = 0m, incomeGross = 0m, taxes = 0m, fees = 0m, principalReturned = 0m, sumAmounts = 0m;
        decimal unknownProceeds = 0m;
        bool hasUnknownLots = false, accruedUnknown = false, hasRedemption = false;
        var rows = new List<LedgerRow>(ordered.Count);

        foreach (var e in ordered)
        {
            var amount = e.AmountRub;
            sumAmounts += amount;
            decimal rowRealized = 0m, accruedUsed = 0m, rowUnknownProceeds = 0m;
            var qty = known + unknown;
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
                    known += q;
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
                    var proceeds = amount - accruedUsed;
                    accruedReceived += accruedUsed;
                    if (q <= 0m)
                    {
                        // Количество продажи неизвестно — какие единицы проданы, не понять: выручка без результата, пул не трогаем.
                        hasUnknownLots = true;
                        rowUnknownProceeds = proceeds;
                        break;
                    }

                    var excess = Math.Max(0m, q - qty); // продано сверх пула журнала (куплено до начала истории)
                    if (excess > QuantityEpsilon) hasUnknownLots = true;
                    var fromPool = q - excess;
                    var knownSold = qty > 0m ? fromPool * known / qty : 0m;
                    var unknownSold = q - knownSold;
                    if (unknownSold > QuantityEpsilon) hasUnknownLots = true;
                    var perUnit = proceeds / q;
                    var avg = known > 0m ? cost / known : 0m;
                    rowRealized = perUnit * knownSold - avg * knownSold;
                    rowUnknownProceeds = perUnit * unknownSold;
                    cost -= avg * knownSold;
                    known -= knownSold;
                    unknown = Math.Max(0m, unknown - (fromPool - knownSold));
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
                {
                    principalReturned += amount;
                    // Тело по единицам без себестоимости — не доход (доля неизвестных в пуле; пула нет — всё неизвестно).
                    var unknownPart = qty > 0m ? amount * unknown / qty : amount;
                    var knownPart = amount - unknownPart;
                    rowUnknownProceeds = unknownPart;
                    if (cost >= knownPart)
                    {
                        cost -= knownPart;
                    }
                    else
                    {
                        rowRealized = knownPart - cost; // возврат больше остатка себестоимости — избыток это прибыль
                        cost = 0m;
                    }
                    break;
                }
                case LedgerEntryKind.Redemption:
                {
                    var unknownPart = qty > 0m ? amount * unknown / qty : amount;
                    if (unknownPart > 0m) hasUnknownLots = true;
                    rowUnknownProceeds = unknownPart;
                    rowRealized = (amount - unknownPart) - cost;
                    cost = 0m;
                    known = 0m;
                    unknown = 0m;
                    hasRedemption = true;
                    principalReturned += amount;
                    break;
                }
                case LedgerEntryKind.SecurityIn:
                    // Ввод бумаг: цена неизвестна — количество растёт, себестоимость нет; позиция помечается.
                    hasUnknownLots = true;
                    unknown += q;
                    rowRealized = amount; // обычно 0 (денег нет); ненулевая сумма не должна потеряться из итога
                    break;
                case LedgerEntryKind.SecurityOut:
                {
                    hasUnknownLots = true;
                    var outQty = Math.Min(q, qty);
                    var knownOut = qty > 0m ? outQty * known / qty : 0m;
                    var avg = known > 0m ? cost / known : 0m;
                    rowRealized = amount - avg * knownOut; // выведенная себестоимость списывается (денег за бумаги не получено)
                    cost -= avg * knownOut;
                    known -= knownOut;
                    unknown = Math.Max(0m, unknown - (outQty - knownOut));
                    break;
                }
            }

            realized += rowRealized;
            unknownProceeds += rowUnknownProceeds;
            rows.Add(new LedgerRow(e, known + unknown, known > 0m ? cost / known : 0m, rowRealized, accruedUsed)
            {
                UnknownCostProceedsRub = rowUnknownProceeds,
            });
        }

        var journalQty = known + unknown;

        // Погашение перекрывает остаток позиции: бумаги погашены, приток уже в журнале.
        var redeemedOverride = hasRedemption && journalQty <= QuantityEpsilon;
        var qtyNow = redeemedOverride ? 0m : currentQuantity;
        var mv = qtyNow > 0m ? marketValueCleanRub : 0m;
        var accNow = qtyNow > 0m ? accruedNowRub : 0m;

        if (Math.Abs(journalQty - qtyNow) > QuantityEpsilon) hasUnknownLots = true;

        // Доля остатка без известной себестоимости: неизвестные единицы пула (пропорционально, если остаток меньше пула)
        // плюс всё, что на счёте сверх журнала. Их стоимость и НКД в результат не идут (см. doc-comment).
        decimal unknownHeldUnits = qtyNow <= 0m ? 0m
            : qtyNow >= journalQty ? unknown + (qtyNow - journalQty)
            : journalQty > 0m ? unknown * qtyNow / journalQty : 0m;
        var knownShare = qtyNow > 0m ? Math.Clamp((qtyNow - unknownHeldUnits) / qtyNow, 0m, 1m) : 0m;
        var mvKnown = mv * knownShare;
        var accKnown = accNow * knownShare;
        var unknownHoldings = (mv - mvKnown) + (accNow - accKnown);
        if (unknownHoldings > 0m) hasUnknownLots = true;

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

        decimal? unrealized = status == PositionIncomeStatus.Open ? mvKnown - cost : null;
        var netCoupon = incomeGross - accruedPaid + accruedReceived + accKnown;
        var total = sumAmounts - unknownProceeds + mvKnown + accKnown;

        var firstEntry = ordered.FirstOrDefault(e => e.Kind is LedgerEntryKind.Buy or LedgerEntryKind.SecurityIn) ?? ordered.FirstOrDefault();
        var firstDate = firstEntry is null ? asOf : DateOnly.FromDateTime(firstEntry.Date);
        var lastDate = ordered.Count == 0 ? asOf : DateOnly.FromDateTime(ordered[^1].Date);
        var endDate = status == PositionIncomeStatus.Open ? asOf : lastDate;
        var holdingDays = Math.Max(0, endDate.DayNumber - firstDate.DayNumber);

        decimal? xirr = null;
        if (holdingDays >= MinXirrHoldingDays)
        {
            // Выручка единиц без себестоимости гасится условным взносом той же датой — XIRR только по известным единицам.
            var flows = rows
                .Select(r => new XirrCalculator.CashFlow(DateOnly.FromDateTime(r.Entry.Date), r.Entry.AmountRub - r.UnknownCostProceedsRub))
                .Where(f => f.Amount != 0m)
                .ToList();
            if (mvKnown + accKnown > 0m) flows.Add(new XirrCalculator.CashFlow(asOf, mvKnown + accKnown));
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
            UnknownCostProceedsRub = unknownProceeds,
            UnknownCostHoldingsRub = unknownHoldings,
        };
        return new AssetIncome(income, rows);
    }

    /// <summary>НКД известен, если задан и 0 ≤ НКД &lt; |сумма операции|; иначе null (допущение «Payment включает НКД» не подтверждено).</summary>
    private static decimal? KnownAccrued(decimal? accrued, decimal grossAmount) =>
        accrued is { } a && a >= 0m && a < grossAmount ? a : null;
}

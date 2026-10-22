using System.Globalization;
using Bonds.Core.Calculation;

namespace Bonds.Core.Income;

/// <summary>Итоги all-time по набору позиций. Деньги — РУБЛИ; <see cref="TotalReturnFraction"/> — ДОЛЯ.</summary>
/// <remarks>
/// Поля-суммы — по ПОЗИЦИЯМ (включая закрытые). Налоги/комиссии «по счёту» (операции без актива) лежат отдельно
/// (<see cref="AccountTaxesRub"/>, <see cref="AccountFeesRub"/>) и включены только в <see cref="TotalResultRub"/>.
/// </remarks>
public sealed record IncomeTotals(
    int PositionsCount,
    decimal InvestedTotalRub,
    decimal MarketValueCleanRub,
    decimal AccruedNowRub,
    decimal UnrealizedPnlRub,
    decimal RealizedPnlRub,
    decimal IncomeGrossRub,
    decimal AccruedPaidRub,
    decimal AccruedReceivedRub,
    decimal NetCouponIncomeRub,
    decimal TaxesRub,
    decimal FeesRub,
    decimal PrincipalReturnedRub,
    decimal AccountTaxesRub,
    decimal AccountFeesRub,
    decimal TotalResultRub,
    decimal? TotalReturnFraction);

/// <summary>
/// Денежные события периода (включая налоги/комиссии «по счёту»). Бумажная прибыль в периоде НЕ считается
/// (нужна оценка на начало периода — вне скоупа).
/// </summary>
public sealed record IncomePeriodTotals(
    decimal CouponsGrossRub,
    decimal DistributionsGrossRub,
    decimal AccruedPaidRub,
    decimal AccruedReceivedRub,
    decimal TaxesRub,
    decimal FeesRub,
    decimal RealizedPnlRub,
    decimal NetRub,
    decimal CurrencyIncomeGrossRub = 0m);

/// <summary>Месяц ряда (<see cref="Month"/> = "yyyy-MM"). <c>AccruedNetRub</c> = −уплачен + получен; <c>NetRub</c> = сумма всех полей.</summary>
public sealed record IncomeMonthStats(
    string Month,
    decimal CouponsGrossRub,
    decimal DistributionsGrossRub,
    decimal AccruedNetRub,
    decimal TaxesRub,
    decimal FeesRub,
    decimal RealizedPnlRub,
    decimal NetRub,
    decimal CurrencyIncomeGrossRub = 0m);

/// <summary>Периодные денежные события одной позиции.</summary>
public sealed record PositionPeriodIncome(
    decimal IncomeGrossRub,
    decimal RealizedPnlRub,
    decimal TaxesRub,
    decimal FeesRub);

public sealed record IncomeStatistics(
    IncomeTotals Totals,
    DateOnly? PeriodFrom,
    DateOnly? PeriodTo,
    IncomePeriodTotals Period,
    IReadOnlyList<IncomeMonthStats> Months,
    IReadOnlyDictionary<(AssetKind Kind, ulong AssetId), PositionPeriodIncome> PositionPeriods,
    decimal? PortfolioXirrFraction);

/// <summary>
/// Агрегация журнала доходов (задача 52): итоги all-time, денежные события периода, помесячный ряд, XIRR портфеля.
/// Чистая функция. Реализованный результат атрибутируется дате операции, его признавшей (продажа/погашение/возврат
/// тела); бумажная прибыль — только all-time.
/// </summary>
public static class IncomeStatisticsService
{
    private const int MaxMonths = 1200;

    /// <param name="assets">Результаты калькулятора по активам выбранного фильтра.</param>
    /// <param name="accountEntries">Записи «по счёту» (налоги/комиссии без актива).</param>
    /// <param name="from">Начало периода (включительно) или null — с первого события.</param>
    /// <param name="to">Конец периода (включительно) или null — по <paramref name="asOf"/>.</param>
    public static IncomeStatistics Compute(
        IReadOnlyList<AssetIncome> assets,
        IReadOnlyList<LedgerEntry> accountEntries,
        DateOnly? from,
        DateOnly? to,
        DateOnly asOf)
    {
        var accountLevel = accountEntries.Where(e => e.Kind is LedgerEntryKind.Tax or LedgerEntryKind.Fee).ToList();
        var totals = BuildTotals(assets, accountLevel);

        // ── Период: денежные события ──────────────────────────────────────────────────────────
        var events = new List<PeriodEvent>();
        foreach (var asset in assets)
        {
            foreach (var row in asset.Rows)
            {
                var date = DateOnly.FromDateTime(row.Entry.Date);
                var kind = row.Entry.Kind;
                var assetKind = row.Entry.AssetKind;
                events.Add(new PeriodEvent(
                    date,
                    CouponsGross: kind == LedgerEntryKind.Income && assetKind == AssetKind.Bond ? row.Entry.AmountRub : 0m,
                    DistributionsGross: kind == LedgerEntryKind.Income && assetKind == AssetKind.Fund ? row.Entry.AmountRub : 0m,
                    // Проценты на остаток валюты — отдельное поле; реализованная курсовая разница остаётся в Realized.
                    CurrencyIncomeGross: kind == LedgerEntryKind.Income && assetKind == AssetKind.Currency ? row.Entry.AmountRub : 0m,
                    AccruedPaid: kind == LedgerEntryKind.Buy ? row.AccruedUsedRub : 0m,
                    AccruedReceived: kind == LedgerEntryKind.Sell ? row.AccruedUsedRub : 0m,
                    Taxes: kind == LedgerEntryKind.Tax ? row.Entry.AmountRub : 0m,
                    Fees: kind == LedgerEntryKind.Fee ? row.Entry.AmountRub : 0m,
                    Realized: row.RealizedPnlRub,
                    Key: (asset.Income.AssetKind, asset.Income.AssetId)));
            }
        }
        foreach (var e in accountLevel)
        {
            events.Add(new PeriodEvent(
                DateOnly.FromDateTime(e.Date), 0m, 0m, 0m, 0m, 0m,
                e.Kind == LedgerEntryKind.Tax ? e.AmountRub : 0m,
                e.Kind == LedgerEntryKind.Fee ? e.AmountRub : 0m,
                0m, null));
        }

        var effectiveFrom = from ?? (events.Count > 0 ? events.Min(e => e.Date) : asOf);
        var effectiveTo = to ?? asOf;
        var inPeriod = events.Where(e => e.Date >= effectiveFrom && e.Date <= effectiveTo).ToList();

        var period = new IncomePeriodTotals(
            inPeriod.Sum(e => e.CouponsGross), inPeriod.Sum(e => e.DistributionsGross),
            inPeriod.Sum(e => e.AccruedPaid), inPeriod.Sum(e => e.AccruedReceived),
            inPeriod.Sum(e => e.Taxes), inPeriod.Sum(e => e.Fees), inPeriod.Sum(e => e.Realized),
            inPeriod.Sum(e => e.Net), inPeriod.Sum(e => e.CurrencyIncomeGross));

        var positionPeriods = assets.ToDictionary(
            a => (a.Income.AssetKind, a.Income.AssetId),
            a =>
            {
                var mine = inPeriod.Where(e => e.Key == (a.Income.AssetKind, a.Income.AssetId)).ToList();
                return new PositionPeriodIncome(
                    mine.Sum(e => e.CouponsGross + e.DistributionsGross + e.CurrencyIncomeGross), mine.Sum(e => e.Realized),
                    mine.Sum(e => e.Taxes), mine.Sum(e => e.Fees));
            });

        // ── Помесячный ряд: ровная ось (пустые месяцы — нулевые строки) ───────────────────────
        var months = new List<IncomeMonthStats>();
        if (effectiveTo >= effectiveFrom && (from is not null || events.Count > 0))
        {
            var cursor = new DateOnly(effectiveFrom.Year, effectiveFrom.Month, 1);
            var last = new DateOnly(effectiveTo.Year, effectiveTo.Month, 1);
            // Защита от абсурдного диапазона: берём последние MaxMonths месяцев.
            var span = (last.Year - cursor.Year) * 12 + last.Month - cursor.Month + 1;
            if (span > MaxMonths) cursor = last.AddMonths(-(MaxMonths - 1));
            var byMonth = inPeriod.GroupBy(e => (e.Date.Year, e.Date.Month)).ToDictionary(g => g.Key, g => g.ToList());
            for (; cursor <= last; cursor = cursor.AddMonths(1))
            {
                byMonth.TryGetValue((cursor.Year, cursor.Month), out var list);
                list ??= [];
                var coupons = list.Sum(e => e.CouponsGross);
                var dist = list.Sum(e => e.DistributionsGross);
                var currencyIncome = list.Sum(e => e.CurrencyIncomeGross);
                var accruedNet = list.Sum(e => e.AccruedReceived - e.AccruedPaid);
                var taxes = list.Sum(e => e.Taxes);
                var fees = list.Sum(e => e.Fees);
                var realized = list.Sum(e => e.Realized);
                months.Add(new IncomeMonthStats(
                    cursor.ToString("yyyy-MM", CultureInfo.InvariantCulture),
                    coupons, dist, accruedNet, taxes, fees, realized,
                    coupons + dist + currencyIncome + accruedNet + taxes + fees + realized, currencyIncome));
            }
        }

        return new IncomeStatistics(
            totals, from, to, period, months, positionPeriods, PortfolioXirr(assets, accountLevel, asOf));
    }

    private static IncomeTotals BuildTotals(IReadOnlyList<AssetIncome> assets, IReadOnlyList<LedgerEntry> accountLevel)
    {
        decimal Sum(Func<PositionIncome, decimal> f) => assets.Sum(a => f(a.Income));
        var accTaxes = accountLevel.Where(e => e.Kind == LedgerEntryKind.Tax).Sum(e => e.AmountRub);
        var accFees = accountLevel.Where(e => e.Kind == LedgerEntryKind.Fee).Sum(e => e.AmountRub);
        var invested = Sum(p => p.InvestedTotalRub);
        var total = Sum(p => p.TotalResultRub) + accTaxes + accFees;
        return new IncomeTotals(
            assets.Count, invested, Sum(p => p.MarketValueCleanRub), Sum(p => p.AccruedNowRub),
            Sum(p => p.UnrealizedPnlRub ?? 0m), Sum(p => p.RealizedPnlRub), Sum(p => p.IncomeGrossRub),
            Sum(p => p.AccruedPaidRub), Sum(p => p.AccruedReceivedRub), Sum(p => p.NetCouponIncomeRub),
            Sum(p => p.TaxesRub), Sum(p => p.FeesRub), Sum(p => p.PrincipalReturnedRub),
            accTaxes, accFees, total, invested > 0m ? total / invested : null);
    }

    /// <summary>
    /// XIRR портфеля (облигации + фонды выбранного фильтра): все операции с активом + налоги/комиссии «по счёту» +
    /// терминальный поток = текущая стоимость остатков (грязная для облигаций: чистая + НКД). Деньги на счёте и
    /// пополнения не участвуют (как у существующего XIRR). Null — потоков мало, меньше 30 дней или не сошёлся.
    /// </summary>
    private static decimal? PortfolioXirr(IReadOnlyList<AssetIncome> assets, IReadOnlyList<LedgerEntry> accountLevel, DateOnly asOf)
    {
        var flows = new List<XirrCalculator.CashFlow>();
        foreach (var a in assets)
        {
            foreach (var r in a.Rows)
            {
                if (r.Entry.AmountRub != 0m) flows.Add(new XirrCalculator.CashFlow(DateOnly.FromDateTime(r.Entry.Date), r.Entry.AmountRub));
            }
        }
        foreach (var e in accountLevel)
        {
            if (e.AmountRub != 0m) flows.Add(new XirrCalculator.CashFlow(DateOnly.FromDateTime(e.Date), e.AmountRub));
        }
        if (flows.Count == 0) return null;

        var terminal = assets.Sum(a => a.Income.MarketValueCleanRub + a.Income.AccruedNowRub);
        if (terminal > 0m) flows.Add(new XirrCalculator.CashFlow(asOf, terminal));

        var first = flows.Min(f => f.Date);
        if (asOf.DayNumber - first.DayNumber < PositionIncomeCalculator.MinXirrHoldingDays) return null;
        return XirrCalculator.Calculate(flows)?.Rate;
    }

    private sealed record PeriodEvent(
        DateOnly Date, decimal CouponsGross, decimal DistributionsGross, decimal CurrencyIncomeGross, decimal AccruedPaid, decimal AccruedReceived,
        decimal Taxes, decimal Fees, decimal Realized, (AssetKind, ulong)? Key)
    {
        public decimal Net => CouponsGross + DistributionsGross + CurrencyIncomeGross + AccruedReceived - AccruedPaid + Taxes + Fees + Realized;
    }
}

using Bonds.Core.Income;
using Bonds.Core.Models;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Income;

/// <summary>
/// Задача 55: курсовая разница юаней через общий PositionIncomeCalculator (average cost в рублях за ЕДИНИЦУ). Эталон из плана:
/// купил 1 000 CNY по 13.00, курс 12.20 -> бумажный убыток 800; продал 400 по 12.50 -> реализованный 400 x (12.50 - 13.00) = -200.
/// </summary>
public class CurrencyIncomeTests
{
    private static readonly DateOnly AsOf = new(2026, 10, 22);
    private const ulong CnyId = 3;

    private static LedgerEntry Entry(int daysAgo, LedgerEntryKind kind, decimal amount, decimal? qty = null, ulong id = 1) =>
        new(AssetKind.Currency, CnyId, AsOf.AddDays(-daysAgo).ToDateTime(new TimeOnly(10, 0)), kind, amount, qty, null, id);

    [Fact]
    public void Buy1000At13_RateDropsTo1220_UnrealizedMinus800()
    {
        var entries = new[] { Entry(120, LedgerEntryKind.Buy, -13000m, 1000m) };

        var r = PositionIncomeCalculator.CalculateDetailed(AssetKind.Currency, CnyId, entries, 1000m, 1000m * 12.20m, 0m, AsOf).Income;

        r.Status.Should().Be(PositionIncomeStatus.Open);
        r.CostRemainingRub.Should().Be(13000m);
        r.MarketValueCleanRub.Should().Be(12200m);
        r.UnrealizedPnlRub.Should().Be(-800m);
        r.RealizedPnlRub.Should().Be(0m);
        r.AccruedUnknown.Should().BeFalse("НКД у валюты не бывает");
        r.TotalResultRub.Should().Be(-800m);
        r.TotalReturnFraction.Should().BeApproximately(-800m / 13000m, 0.0000001m);
        r.HasUnknownLots.Should().BeFalse();
    }

    [Fact]
    public void PartialSale_RealizesFxDifference_AndInvariantHolds()
    {
        var entries = new[]
        {
            Entry(120, LedgerEntryKind.Buy, -13000m, 1000m, 1),
            Entry(60, LedgerEntryKind.Sell, 400m * 12.50m, 400m, 2),
        };

        var r = PositionIncomeCalculator.CalculateDetailed(AssetKind.Currency, CnyId, entries, 600m, 600m * 12.20m, 0m, AsOf).Income;

        r.RealizedPnlRub.Should().Be(-200m, "400 x (12.50 - 13.00)");
        r.CostRemainingRub.Should().Be(600m * 13m);
        r.UnrealizedPnlRub.Should().Be(600m * 12.20m - 600m * 13m, "-480");
        // Инвариант: Итог = Σ AmountRub + стоимость остатка = Realized + Unrealized + NetCoupon + налоги + комиссии.
        r.TotalResultRub.Should().Be(-13000m + 5000m + 7320m);
        r.TotalResultRub.Should().Be(r.RealizedPnlRub + (r.UnrealizedPnlRub ?? 0m) + r.NetCouponIncomeRub + r.TaxesRub + r.FeesRub);
    }

    [Fact]
    public void FullySold_Closed_RealizedOnly_NoPhantomLoss()
    {
        var entries = new[]
        {
            Entry(120, LedgerEntryKind.Buy, -13000m, 1000m, 1),
            Entry(30, LedgerEntryKind.Sell, 12500m, 1000m, 2),
        };

        var r = PositionIncomeCalculator.CalculateDetailed(AssetKind.Currency, CnyId, entries, 0m, 0m, 0m, AsOf).Income;

        r.Status.Should().Be(PositionIncomeStatus.Closed);
        r.RealizedPnlRub.Should().Be(-500m);
        r.TotalResultRub.Should().Be(-500m);
        r.HasUnknownLots.Should().BeFalse();
    }

    [Fact]
    public void JournalShortOfPosition_FlagsUnknownLots_ButKeepsHangingCostWhileHeld()
    {
        // Журнал знает только 600 юаней, на счёте 1000 (остальные пришли иначе). Позиция держится - себестоимость не списывается.
        var entries = new[] { Entry(60, LedgerEntryKind.Buy, -7800m, 600m) };

        var r = PositionIncomeCalculator.CalculateDetailed(AssetKind.Currency, CnyId, entries, 1000m, 1000m * 12.2m, 0m, AsOf).Income;

        r.HasUnknownLots.Should().BeTrue();
        r.Status.Should().Be(PositionIncomeStatus.Open);
        r.CostRemainingRub.Should().Be(7800m, "висящая себестоимость остаётся, пока позиция по-прежнему держится");
        r.RealizedPnlRub.Should().Be(0m, "в убыток ничего не списано");
    }

    [Fact]
    public void JournalHoldsMoreThanPosition_ClosedPosition_WritesOffAsRealizedLoss_WithUnknownLots()
    {
        // Позиции уже нет (потрачена вне журнала): оставшаяся себестоимость - реализованный убыток (правило закрытых позиций).
        var entries = new[] { Entry(60, LedgerEntryKind.Buy, -13000m, 1000m) };

        var r = PositionIncomeCalculator.CalculateDetailed(AssetKind.Currency, CnyId, entries, 0m, 0m, 0m, AsOf).Income;

        r.HasUnknownLots.Should().BeTrue();
        r.RealizedPnlRub.Should().Be(-13000m);
        r.TotalResultRub.Should().Be(-13000m);
    }

    [Fact]
    public void LedgerMapper_MapsAllCurrencyOperationTypes()
    {
        LedgerEntryMapper.ToKind(CurrencyOperationType.Buy).Should().Be(LedgerEntryKind.Buy);
        LedgerEntryMapper.ToKind(CurrencyOperationType.Sell).Should().Be(LedgerEntryKind.Sell);
        LedgerEntryMapper.ToKind(CurrencyOperationType.Tax).Should().Be(LedgerEntryKind.Tax);
        LedgerEntryMapper.ToKind(CurrencyOperationType.Fee).Should().Be(LedgerEntryKind.Fee);
        LedgerEntryMapper.ToKind(CurrencyOperationType.Income).Should().Be(LedgerEntryKind.Income);

        var op = new CurrencyOperation { Id = 9, CurrencyId = CnyId, Type = CurrencyOperationType.Buy, Date = new DateTime(2026, 6, 1), AmountRub = -13000m, Quantity = 1000m };
        var e = LedgerEntryMapper.FromCurrencyOperation(op);
        e.AssetKind.Should().Be(AssetKind.Currency);
        e.AssetId.Should().Be(CnyId);
        e.AmountRub.Should().Be(-13000m, "знак брокера не переписывается");
        e.AccruedRub.Should().BeNull();
        FromNoCurrency().AssetId.Should().Be(0UL);

        LedgerEntry FromNoCurrency() => LedgerEntryMapper.FromCurrencyOperation(new CurrencyOperation { Type = CurrencyOperationType.Fee, AmountRub = -1m });
    }

    [Fact]
    public void Statistics_CurrencyIncomeIsSeparateField_RealizedFxStaysInRealized_NetSumsAll()
    {
        var entries = new[]
        {
            Entry(120, LedgerEntryKind.Buy, -13000m, 1000m, 1),
            Entry(40, LedgerEntryKind.Income, 30m, null, 2),            // проценты на остаток
            Entry(30, LedgerEntryKind.Sell, 400m * 12.50m, 400m, 3),   // realized -200
            Entry(30, LedgerEntryKind.Fee, -5m, null, 4),
        };
        var asset = PositionIncomeCalculator.CalculateDetailed(AssetKind.Currency, CnyId, entries, 600m, 600m * 12.2m, 0m, AsOf);

        var stats = IncomeStatisticsService.Compute([asset], [], null, null, AsOf);

        stats.Period.CurrencyIncomeGrossRub.Should().Be(30m);
        stats.Period.CouponsGrossRub.Should().Be(0m, "проценты по валюте - не купоны");
        stats.Period.DistributionsGrossRub.Should().Be(0m, "и не выплаты фонда");
        stats.Period.RealizedPnlRub.Should().Be(-200m, "курсовая разница - в существующем realizedPnlRub");
        stats.Period.FeesRub.Should().Be(-5m);
        stats.Period.NetRub.Should().Be(30m - 200m - 5m);
        stats.Months.Sum(m => m.CurrencyIncomeGrossRub).Should().Be(30m);
        stats.Months.Sum(m => m.NetRub).Should().Be(stats.Period.NetRub);
        stats.PositionPeriods[(AssetKind.Currency, CnyId)].IncomeGrossRub.Should().Be(30m);
    }

    [Fact]
    public void Statistics_PortfolioXirr_IncludesCurrencyFlowsAndTerminalValue()
    {
        var entries = new[] { Entry(200, LedgerEntryKind.Buy, -13000m, 1000m) };
        var asset = PositionIncomeCalculator.CalculateDetailed(AssetKind.Currency, CnyId, entries, 1000m, 1000m * 13.5m, 0m, AsOf);

        var stats = IncomeStatisticsService.Compute([asset], [], null, null, AsOf);

        stats.PortfolioXirrFraction.Should().NotBeNull().And.BeGreaterThan(0m, "13 500 / 13 000 за ~200 дней");
        stats.Totals.MarketValueCleanRub.Should().Be(13500m);
    }
}

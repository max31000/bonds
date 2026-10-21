using Bonds.Core.Calculation;
using Bonds.Core.Income;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Income;

/// <summary>Периодная статистика — денежные события периода; даты относительно «сегодня» (якорь — 1-е число текущего месяца).</summary>
public class IncomeStatisticsServiceTests
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);
    private static readonly DateOnly M0 = new(Today.Year, Today.Month, 1);
    private static ulong _id = 1000;

    private static DateTime At(int monthsBack, int day, int hour = 12) =>
        M0.AddMonths(-monthsBack).AddDays(day - 1).ToDateTime(new TimeOnly(hour, 0));

    private static LedgerEntry Bond(DateTime d, LedgerEntryKind k, decimal amount, decimal? qty = null, decimal? acc = null, ulong asset = 1) =>
        new(AssetKind.Bond, asset, d, k, amount, qty, acc, ++_id);

    private static LedgerEntry Fund(DateTime d, LedgerEntryKind k, decimal amount, decimal? qty = null) =>
        new(AssetKind.Fund, 7, d, k, amount, qty, null, ++_id);

    private static AssetIncome Calc(AssetKind kind, ulong id, List<LedgerEntry> entries, decimal qty, decimal mv, decimal acc) =>
        PositionIncomeCalculator.CalculateDetailed(kind, id, entries, qty, mv, acc, Today);

    // Облигация 1: покупка M-5 (−10 200, НКД 200, 10 шт.), купон M-2 +400 и налог −52, продажа 4 шт. в M-1 за 4 160 (НКД 40).
    //   Средняя чистая = 10 000/10 = 1 000; реализованный на продаже = 4 120 − 4 000 = 120. Остаток 6 шт.: чистая 6 100, НКД 30.
    // Фонд 7: покупка M-4 (−10 000, 100 паёв), выплата M-1 +120, налог M-1 −15.6; цена → 10 100.
    // Счёт: комиссия M-2 −10.
    private static (List<AssetIncome> Assets, List<LedgerEntry> Account) Sample()
    {
        var bond = Calc(AssetKind.Bond, 1, [
            Bond(At(5, 10), LedgerEntryKind.Buy, -10_200m, 10, 200m),
            Bond(At(2, 5), LedgerEntryKind.Income, 400m),
            Bond(At(2, 5), LedgerEntryKind.Tax, -52m),
            Bond(At(1, 10), LedgerEntryKind.Sell, 4_160m, 4, 40m),
        ], 6, 6_100m, 30m);
        var fund = Calc(AssetKind.Fund, 7, [
            Fund(At(4, 3), LedgerEntryKind.Buy, -10_000m, 100),
            Fund(At(1, 3), LedgerEntryKind.Income, 120m),
            Fund(At(1, 3), LedgerEntryKind.Tax, -15.6m),
        ], 100, 10_100m, 0m);
        var account = new List<LedgerEntry> { new(AssetKind.Bond, 0, At(2, 20), LedgerEntryKind.Fee, -10m, null, null, ++_id) };
        return ([bond, fund], account);
    }

    [Fact]
    public void Totals_AreSumsOverPositions_PlusAccountLevel()
    {
        var (assets, account) = Sample();
        var s = IncomeStatisticsService.Compute(assets, account, null, null, Today);

        // Облигация: invested 10 200; realized 120; unrealized = 6 100 − 6 000 = 100; netCoupon = 400 − 200 + 40 + 30 = 270; taxes −52;
        //   total = 120 + 100 + 270 − 52 = 438. Фонд: unrealized 100, netCoupon 120, taxes −15.6 → 204.4. Счёт: −10.
        s.Totals.PositionsCount.Should().Be(2);
        s.Totals.InvestedTotalRub.Should().Be(20_200m);
        s.Totals.RealizedPnlRub.Should().Be(120m);
        s.Totals.UnrealizedPnlRub.Should().Be(200m);
        s.Totals.NetCouponIncomeRub.Should().Be(390m);
        s.Totals.TaxesRub.Should().Be(-67.6m);
        s.Totals.AccountFeesRub.Should().Be(-10m);
        s.Totals.TotalResultRub.Should().Be(438m + 204.4m - 10m);
        assets.Sum(a => a.Income.TotalResultRub).Should().Be(642.4m);
        s.Totals.TotalReturnFraction.Should().Be(632.4m / 20_200m);
    }

    [Fact]
    public void Period_CutsEventsByDates_Inclusive_AndAccountLevelIsIncluded()
    {
        var (assets, account) = Sample();
        // Период M-2 (1-е) … M-1 (последний день): купон/налог/комиссия M-2, продажа/выплата/налог фонда M-1; покупки M-5/M-4 вне периода.
        var from = M0.AddMonths(-2);
        var to = M0.AddDays(-1);
        var s = IncomeStatisticsService.Compute(assets, account, from, to, Today);

        s.Period.CouponsGrossRub.Should().Be(400m);
        s.Period.DistributionsGrossRub.Should().Be(120m);
        s.Period.TaxesRub.Should().Be(-52m - 15.6m);
        s.Period.FeesRub.Should().Be(-10m);
        s.Period.RealizedPnlRub.Should().Be(120m);
        s.Period.AccruedPaidRub.Should().Be(0m, "покупка с уплаченным НКД вне периода");
        s.Period.AccruedReceivedRub.Should().Be(40m);
        s.Period.NetRub.Should().Be(400m + 120m - 67.6m - 10m + 120m + 40m);
    }

    [Fact]
    public void Period_BoundariesAreInclusive_OnBothEnds_AndExcludeNeighbours()
    {
        var onFrom = Bond(At(3, 10, 0), LedgerEntryKind.Income, 100m);
        var onTo = Bond(At(3, 20, 23), LedgerEntryKind.Income, 10m);
        var before = Bond(At(3, 9, 23), LedgerEntryKind.Income, 1_000m);
        var after = Bond(At(3, 21, 0), LedgerEntryKind.Income, 10_000m);
        var asset = Calc(AssetKind.Bond, 1, [Bond(At(6, 1), LedgerEntryKind.Buy, -1_000m, 1, 0m), before, onFrom, onTo, after], 1, 1_000m, 0m);

        var s = IncomeStatisticsService.Compute([asset], [], DateOnly.FromDateTime(onFrom.Date), DateOnly.FromDateTime(onTo.Date), Today);
        s.Period.CouponsGrossRub.Should().Be(110m);
        s.PositionPeriods[(AssetKind.Bond, 1UL)].IncomeGrossRub.Should().Be(110m);
    }

    [Fact]
    public void Monthly_RealizedGoesToSellMonth_EmptyMonthsFilled_AxisIsFlat()
    {
        var (assets, account) = Sample();
        var s = IncomeStatisticsService.Compute(assets, account, M0.AddMonths(-4), M0.AddDays(-1), Today);

        s.Months.Select(m => m.Month).Should().Equal(
            M0.AddMonths(-4).ToString("yyyy-MM"), M0.AddMonths(-3).ToString("yyyy-MM"),
            M0.AddMonths(-2).ToString("yyyy-MM"), M0.AddMonths(-1).ToString("yyyy-MM"));

        s.Months[0].NetRub.Should().Be(0m, "M-4: только покупка фонда — не доход");
        s.Months[1].Should().Be(new IncomeMonthStats(M0.AddMonths(-3).ToString("yyyy-MM"), 0, 0, 0, 0, 0, 0, 0), "пустой месяц — нулевая строка");

        var m2 = s.Months[2]; // купон 400, налог −52, комиссия −10
        m2.CouponsGrossRub.Should().Be(400m);
        m2.TaxesRub.Should().Be(-52m);
        m2.FeesRub.Should().Be(-10m);
        m2.RealizedPnlRub.Should().Be(0m);
        m2.NetRub.Should().Be(338m);

        var m1 = s.Months[3]; // продажа: realized 120, НКД получен 40; выплата фонда 120, налог −15.6
        m1.RealizedPnlRub.Should().Be(120m);
        m1.AccruedNetRub.Should().Be(40m);
        m1.DistributionsGrossRub.Should().Be(120m);
        m1.TaxesRub.Should().Be(-15.6m);
        m1.NetRub.Should().Be(120m + 40m + 120m - 15.6m);
    }

    [Fact]
    public void Monthly_AccruedPaidIsNegative_InPurchaseMonth()
    {
        var (assets, account) = Sample();
        var s = IncomeStatisticsService.Compute(assets, account, M0.AddMonths(-5), M0.AddMonths(-5).AddDays(27), Today);
        s.Months.Should().ContainSingle();
        s.Months[0].AccruedNetRub.Should().Be(-200m);
        s.Months[0].NetRub.Should().Be(-200m);
    }

    [Fact]
    public void NoPeriod_UsesAllTime_FromFirstEventToToday()
    {
        var (assets, account) = Sample();
        var s = IncomeStatisticsService.Compute(assets, account, null, null, Today);
        s.Months.First().Month.Should().Be(M0.AddMonths(-5).ToString("yyyy-MM"));
        s.Months.Last().Month.Should().Be(M0.ToString("yyyy-MM"));
        s.Months.Sum(m => m.RealizedPnlRub).Should().Be(120m);
        s.Period.RealizedPnlRub.Should().Be(s.Totals.RealizedPnlRub, "без периода денежные события = all-time");
    }

    [Fact]
    public void Empty_ReturnsZeros_AndNoMonths()
    {
        var s = IncomeStatisticsService.Compute([], [], null, null, Today);
        s.Totals.TotalResultRub.Should().Be(0m);
        s.Months.Should().BeEmpty();
        s.PortfolioXirrFraction.Should().BeNull();
    }

    [Fact]
    public void PortfolioXirr_MatchesXirrCalculator_OnSameFlows_AccountLevelIncluded_TerminalIsDirtyValue()
    {
        var (assets, account) = Sample();
        var s = IncomeStatisticsService.Compute(assets, account, null, null, Today);

        var flows = new List<XirrCalculator.CashFlow>
        {
            new(DateOnly.FromDateTime(At(5, 10)), -10_200m),
            new(DateOnly.FromDateTime(At(2, 5)), 400m),
            new(DateOnly.FromDateTime(At(2, 5)), -52m),
            new(DateOnly.FromDateTime(At(1, 10)), 4_160m),
            new(DateOnly.FromDateTime(At(4, 3)), -10_000m),
            new(DateOnly.FromDateTime(At(1, 3)), 120m),
            new(DateOnly.FromDateTime(At(1, 3)), -15.6m),
            new(DateOnly.FromDateTime(At(2, 20)), -10m),
            new(Today, 6_100m + 30m + 10_100m), // облигации: чистая + НКД, фонд: стоимость паёв
        };
        s.PortfolioXirrFraction.Should().Be(XirrCalculator.Calculate(flows)!.Value.Rate);
    }

    [Fact]
    public void PositionPeriods_SplitPerAsset()
    {
        var (assets, account) = Sample();
        var s = IncomeStatisticsService.Compute(assets, account, M0.AddMonths(-1), M0.AddDays(-1), Today);
        s.PositionPeriods[(AssetKind.Bond, 1UL)].RealizedPnlRub.Should().Be(120m);
        s.PositionPeriods[(AssetKind.Bond, 1UL)].IncomeGrossRub.Should().Be(0m);
        s.PositionPeriods[(AssetKind.Fund, 7UL)].IncomeGrossRub.Should().Be(120m);
        s.PositionPeriods[(AssetKind.Fund, 7UL)].TaxesRub.Should().Be(-15.6m);
    }
}

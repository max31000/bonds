using Bonds.Core.Calculation;
using Bonds.Core.Income;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Income;

/// <summary>
/// Эталоны <see cref="PositionIncomeCalculator"/> — числа посчитаны РУКАМИ (в комментариях). На каждом эталоне закреплён
/// двойной инвариант: TotalResult = Σ AmountRub + MarketValueClean + AccruedNow и
/// TotalResult = Realized + Unrealized + NetCouponIncome + Taxes + Fees. Даты — относительно «сегодня».
/// </summary>
public class PositionIncomeCalculatorTests
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);
    private static DateTime Ago(int days) => Today.AddDays(-days).ToDateTime(new TimeOnly(10, 0));
    private static ulong _id;

    private static LedgerEntry E(AssetKind k, int daysAgo, LedgerEntryKind kind, decimal amount, decimal? qty = null, decimal? accrued = null) =>
        new(k, 1, Ago(daysAgo), kind, amount, qty, accrued, ++_id);

    private static LedgerEntry B(int d, LedgerEntryKind kind, decimal amount, decimal? qty = null, decimal? accrued = null) =>
        E(AssetKind.Bond, d, kind, amount, qty, accrued);

    private static PositionIncome Calc(AssetKind kind, IReadOnlyList<LedgerEntry> entries, decimal qtyNow, decimal mvClean, decimal accNow)
    {
        var r = PositionIncomeCalculator.CalculateDetailed(kind, 1, entries, qtyNow, mvClean, accNow, Today);
        AssertInvariants(r.Income, entries, r);
        return r.Income;
    }

    private static void AssertInvariants(PositionIncome p, IReadOnlyList<LedgerEntry> entries, AssetIncome detailed)
    {
        // (1) итог = Σ денежных потоков журнала + текущая стоимость остатка, без единиц с неизвестной себестоимостью
        //     (их выручка и стоимость остатка считаются внесёнными «по рынку» — результата не дают)
        p.TotalResultRub.Should().Be(
            entries.Sum(e => e.AmountRub) - p.UnknownCostProceedsRub + p.MarketValueCleanRub + p.AccruedNowRub - p.UnknownCostHoldingsRub,
            "инвариант 1");
        detailed.Rows.Sum(r => r.UnknownCostProceedsRub).Should().Be(p.UnknownCostProceedsRub, "строки журнала = сводка (неизвестная себестоимость)");
        // (2) итог = разложение
        p.TotalResultRub.Should().Be(
            p.RealizedPnlRub + (p.UnrealizedPnlRub ?? 0m) + p.NetCouponIncomeRub + p.TaxesRub + p.FeesRub, "инвариант 2");
        // (3) реализованный по строкам сходится со сводкой
        detailed.Rows.Sum(r => r.RealizedPnlRub).Should().Be(p.RealizedPnlRub, "строки журнала = сводка");
    }

    [Fact]
    public void Etalon1_BuyWithAccrued_CouponTax_Hold()
    {
        // Купил 10 шт., сумма −10 200, НКД 200 → чистая себестоимость 10 000, вложено 10 200.
        // Купон +400 брутто, налог −52. Сейчас: чистая стоимость 10 100, НКД 50.
        // Unrealized = 10 100 − 10 000 = 100. NetCoupon = 400 − 200 + 0 + 50 = 250. Taxes = −52.
        // Total = 0 + 100 + 250 − 52 = 298; по потокам: −10 200 + 400 − 52 + 10 100 + 50 = 298.
        var p = Calc(AssetKind.Bond, [
            B(100, LedgerEntryKind.Buy, -10_200m, 10, 200m),
            B(50, LedgerEntryKind.Income, 400m),
            B(50, LedgerEntryKind.Tax, -52m),
        ], 10, 10_100m, 50m);

        p.Status.Should().Be(PositionIncomeStatus.Open);
        p.InvestedTotalRub.Should().Be(10_200m);
        p.CostRemainingRub.Should().Be(10_000m);
        p.UnrealizedPnlRub.Should().Be(100m);
        p.AccruedPaidRub.Should().Be(200m);
        p.NetCouponIncomeRub.Should().Be(250m);
        p.TaxesRub.Should().Be(-52m);
        p.TotalResultRub.Should().Be(298m);
        p.TotalReturnFraction.Should().Be(298m / 10_200m);
        p.HoldingDays.Should().Be(100);
        p.HasUnknownLots.Should().BeFalse();
        p.AccruedUnknown.Should().BeFalse();

        var expectedXirr = XirrCalculator.Calculate([
            new XirrCalculator.CashFlow(Today.AddDays(-100), -10_200m),
            new XirrCalculator.CashFlow(Today.AddDays(-50), 400m),
            new XirrCalculator.CashFlow(Today.AddDays(-50), -52m),
            new XirrCalculator.CashFlow(Today, 10_150m),
        ])!.Value.Rate;
        p.XirrFraction.Should().Be(expectedXirr);
    }

    [Fact]
    public void Etalon2_TwoLots_PartialSell_RealizedByAverageCost()
    {
        // Лоты: 5 × 990 = 4 950 и 5 × 1 010 = 5 050 (НКД 0) → 10 шт., себестоимость 10 000, средняя 1 000.
        // Продал 4 по 1 030 + НКД 40: сумма 4 160, чистая выручка 4 120; реализованный = 4 120 − 4×1 000 = 120.
        // Остаток 6 шт., себестоимость 6 000. Сейчас: чистая 6 240, НКД 30 → Unrealized = 240.
        // NetCoupon = 0 − 0 + 40 + 30 = 70. Total = 120 + 240 + 70 = 430; по потокам: −4 950 − 5 050 + 4 160 + 6 240 + 30 = 430.
        var p = Calc(AssetKind.Bond, [
            B(90, LedgerEntryKind.Buy, -4_950m, 5, 0m),
            B(80, LedgerEntryKind.Buy, -5_050m, 5, 0m),
            B(10, LedgerEntryKind.Sell, 4_160m, 4, 40m),
        ], 6, 6_240m, 30m);

        p.RealizedPnlRub.Should().Be(120m);
        p.CostRemainingRub.Should().Be(6_000m);
        p.UnrealizedPnlRub.Should().Be(240m);
        p.AccruedReceivedRub.Should().Be(40m);
        p.NetCouponIncomeRub.Should().Be(70m);
        p.TotalResultRub.Should().Be(430m);
        p.InvestedTotalRub.Should().Be(10_000m);
    }

    [Fact]
    public void Etalon3_AmortizationThenRedemption()
    {
        // Купил 10 шт. за 10 000 (НКД 0). Амортизация 300 → себестоимость 9 700, реализованный 0.
        // Погашение 9 800 → реализованный = 9 800 − 9 700 = 100; остаток 0, статус Redeemed.
        // Total = 100; по потокам: −10 000 + 300 + 9 800 = 100. PrincipalReturned = 10 100.
        var entries = new List<LedgerEntry>
        {
            B(200, LedgerEntryKind.Buy, -10_000m, 10, 0m),
            B(100, LedgerEntryKind.PrincipalReturn, 300m),
        };
        var afterAmort = PositionIncomeCalculator.CalculateDetailed(AssetKind.Bond, 1, entries, 10, 9_700m, 0m, Today).Income;
        afterAmort.CostRemainingRub.Should().Be(9_700m);
        afterAmort.RealizedPnlRub.Should().Be(0m);
        afterAmort.PrincipalReturnedRub.Should().Be(300m);

        entries.Add(B(5, LedgerEntryKind.Redemption, 9_800m, 10));
        var p = Calc(AssetKind.Bond, entries, 0, 0m, 0m);
        p.Status.Should().Be(PositionIncomeStatus.Redeemed);
        p.RealizedPnlRub.Should().Be(100m);
        p.UnrealizedPnlRub.Should().BeNull();
        p.QuantityNow.Should().Be(0m);
        p.PrincipalReturnedRub.Should().Be(10_100m);
        p.TotalResultRub.Should().Be(100m);
        p.HoldingDays.Should().Be(195, "закрытая: от первой покупки до последней записи");
    }

    [Fact]
    public void Redemption_InJournal_OverridesStalePositionThatBrokerStillReports()
    {
        // Позиция ещё не удалена сверкой (qty 10, котировка 10 050 чистая), но погашение уже в журнале → стоимость 0, приток учтён.
        var p = Calc(AssetKind.Bond, [
            B(200, LedgerEntryKind.Buy, -10_000m, 10, 0m),
            B(1, LedgerEntryKind.Redemption, 10_000m, 10),
        ], 10, 10_050m, 25m);
        p.Status.Should().Be(PositionIncomeStatus.Redeemed);
        p.MarketValueCleanRub.Should().Be(0m);
        p.AccruedNowRub.Should().Be(0m);
        p.TotalResultRub.Should().Be(0m);
    }

    [Fact]
    public void AmortizationBeyondCost_ExcessIsRealized()
    {
        // Себестоимость 100, возврат тела 300 → себестоимость 0, реализованный = 200.
        var p = Calc(AssetKind.Bond, [
            B(100, LedgerEntryKind.Buy, -100m, 1, 0m),
            B(50, LedgerEntryKind.PrincipalReturn, 300m),
        ], 1, 0m, 0m);
        p.CostRemainingRub.Should().Be(0m);
        p.RealizedPnlRub.Should().Be(200m);
        p.TotalResultRub.Should().Be(200m);
    }

    [Fact]
    public void Etalon4_AccruedUnknown_FullAmountGoesToCost()
    {
        // НКД операции не отдан (null): себестоимость = весь amount 10 200, AccruedUnknown.
        // Сейчас: чистая 10 100, НКД 50 → Unrealized = −100; NetCoupon = 0 + 50 = 50; Total = −50; по потокам: −10 200 + 10 150 = −50.
        var p = Calc(AssetKind.Bond, [B(100, LedgerEntryKind.Buy, -10_200m, 10, null)], 10, 10_100m, 50m);
        p.AccruedUnknown.Should().BeTrue();
        p.CostRemainingRub.Should().Be(10_200m);
        p.AccruedPaidRub.Should().Be(0m);
        p.UnrealizedPnlRub.Should().Be(-100m);
        p.NetCouponIncomeRub.Should().Be(50m);
        p.TotalResultRub.Should().Be(-50m);
    }

    [Theory]
    [InlineData(10_200)] // НКД = |amount|
    [InlineData(20_000)] // НКД > |amount|
    public void Etalon5_AccruedNotLessThanAmount_IsIgnored(int accrued)
    {
        var p = Calc(AssetKind.Bond, [B(100, LedgerEntryKind.Buy, -10_200m, 10, accrued)], 10, 10_100m, 50m);
        p.AccruedUnknown.Should().BeTrue();
        p.AccruedPaidRub.Should().Be(0m, "допущение «Payment включает НКД» нарушено — НКД не вычитаем");
        p.CostRemainingRub.Should().Be(10_200m);
    }

    [Fact]
    public void Etalon6_SellMoreThanKnown_UnknownPartGivesNoResult()
    {
        // Известно 5 шт. за 5 000 (средняя 1 000); продано 8 за 8 400 (НКД 0) → 1 050 за шт.
        // Известные 5: реализованный = 5×1 050 − 5×1 000 = 250. Неизвестные 3 (куплены до начала истории): выручка 3 150 —
        // себестоимость не известна, результата нет (раньше она вся шла в «прибыль» как при себестоимости 0).
        // Total = −5 000 + 8 400 − 3 150 = 250.
        var p = Calc(AssetKind.Bond, [
            B(100, LedgerEntryKind.Buy, -5_000m, 5, 0m),
            B(10, LedgerEntryKind.Sell, 8_400m, 8, 0m),
        ], 0, 0m, 0m);
        p.HasUnknownLots.Should().BeTrue();
        p.Status.Should().Be(PositionIncomeStatus.Closed);
        p.CostRemainingRub.Should().Be(0m);
        p.RealizedPnlRub.Should().Be(250m);
        p.UnknownCostProceedsRub.Should().Be(3_150m);
        p.TotalResultRub.Should().Be(250m);
    }

    [Fact]
    public void SellWithoutAnyKnownBuy_IsNotProfit()
    {
        // Продажа 3 шт. за 3 000 без покупок в журнале: вся выручка — неизвестная себестоимость, результат 0.
        var p = Calc(AssetKind.Bond, [B(10, LedgerEntryKind.Sell, 3_000m, 3, 0m)], 0, 0m, 0m);
        p.RealizedPnlRub.Should().Be(0m);
        p.UnknownCostProceedsRub.Should().Be(3_000m);
        p.TotalResultRub.Should().Be(0m);
        p.HasUnknownLots.Should().BeTrue();
    }

    [Fact]
    public void RedemptionOfBondBoughtBeforeHistory_IsNotProfit()
    {
        // Покупки в журнале нет (до начала истории), пришло погашение 10 000 — это возврат неизвестной себестоимости, не прибыль.
        var p = Calc(AssetKind.Bond, [B(5, LedgerEntryKind.Redemption, 10_000m)], 0, 0m, 0m);
        p.Status.Should().Be(PositionIncomeStatus.Redeemed);
        p.RealizedPnlRub.Should().Be(0m);
        p.UnknownCostProceedsRub.Should().Be(10_000m);
        p.TotalResultRub.Should().Be(0m);
        p.HasUnknownLots.Should().BeTrue();
    }

    [Fact]
    public void MixedPool_AmortizationAndRedemption_SplitByKnownShare()
    {
        // 5 шт. куплены за 5 000 (известны) + 5 введены без цены. Амортизация 2 000 на весь пул: 1 000 гасит себестоимость
        // известных (5 000 → 4 000), 1 000 — неизвестных. Погашение 8 000: известным 4 000 − 4 000 = 0, неизвестным 4 000.
        var p = Calc(AssetKind.Bond, [
            B(100, LedgerEntryKind.Buy, -5_000m, 5, 0m),
            B(90, LedgerEntryKind.SecurityIn, 0m, 5),
            B(50, LedgerEntryKind.PrincipalReturn, 2_000m),
            B(5, LedgerEntryKind.Redemption, 8_000m),
        ], 0, 0m, 0m);
        p.RealizedPnlRub.Should().Be(0m);
        p.UnknownCostProceedsRub.Should().Be(5_000m);
        p.TotalResultRub.Should().Be(0m);
    }

    [Fact]
    public void HeldBeyondJournal_UnknownPartExcludedFromUnrealized()
    {
        // Журнал: 5 шт. за 5 000; на счёте 10 шт. по 1 000 (MV 10 000) — 5 пришли не покупкой.
        // Бумажная прибыль считается по известным 5: 5 000 − 5 000 = 0; стоимость неизвестных 5 000 — вне итога.
        var p = Calc(AssetKind.Bond, [B(40, LedgerEntryKind.Buy, -5_000m, 5, 0m)], 10, 10_000m, 0m);
        p.UnrealizedPnlRub.Should().Be(0m);
        p.UnknownCostHoldingsRub.Should().Be(5_000m);
        p.TotalResultRub.Should().Be(0m);
        p.MarketValueCleanRub.Should().Be(10_000m, "стоимость позиции показывается целиком");
    }

    [Fact]
    public void Etalon7_Fund_DistributionsTaxAndPrice()
    {
        // Фонд: 100 паёв по 100 = −10 000. Три выплаты по 120 = 360 брутто, налог −46.8. Цена 101 → стоимость 10 100, НКД 0.
        // Unrealized = 100; NetCoupon = 360; Taxes = −46.8; Total = 100 + 360 − 46.8 = 413.2;
        // по потокам: −10 000 + 360 − 46.8 + 10 100 = 413.2. НКД у фонда — не «неизвестен».
        var p = Calc(AssetKind.Fund, [
            E(AssetKind.Fund, 150, LedgerEntryKind.Buy, -10_000m, 100),
            E(AssetKind.Fund, 120, LedgerEntryKind.Income, 120m),
            E(AssetKind.Fund, 90, LedgerEntryKind.Income, 120m),
            E(AssetKind.Fund, 60, LedgerEntryKind.Income, 120m),
            E(AssetKind.Fund, 60, LedgerEntryKind.Tax, -46.8m),
        ], 100, 10_100m, 0m);
        p.UnrealizedPnlRub.Should().Be(100m);
        p.IncomeGrossRub.Should().Be(360m);
        p.NetCouponIncomeRub.Should().Be(360m);
        p.TaxesRub.Should().Be(-46.8m);
        p.TotalResultRub.Should().Be(413.2m);
        p.AccruedUnknown.Should().BeFalse();
        p.XirrFraction.Should().NotBeNull();
    }

    [Fact]
    public void Fees_AreSigned_AndIncludedInTotal()
    {
        // Покупка −1 000 (НКД 0), комиссия −3, цена сейчас 1 010 → Total = 10 − 3 = 7 (= −1 000 − 3 + 1 010).
        var p = Calc(AssetKind.Bond, [
            B(40, LedgerEntryKind.Buy, -1_000m, 1, 0m),
            B(40, LedgerEntryKind.Fee, -3m),
        ], 1, 1_010m, 0m);
        p.FeesRub.Should().Be(-3m);
        p.TotalResultRub.Should().Be(7m);
    }

    [Fact]
    public void ClosedWithJournalLeftover_WritesOffCostAsRealizedLoss()
    {
        // Куплено 10 за 10 000, продаж/погашения в журнале нет, позиции нет: невозвращённая себестоимость → реализованный убыток.
        var p = Calc(AssetKind.Bond, [B(100, LedgerEntryKind.Buy, -10_000m, 10, 0m)], 0, 0m, 0m);
        p.Status.Should().Be(PositionIncomeStatus.Closed);
        p.HasUnknownLots.Should().BeTrue();
        p.CostRemainingRub.Should().Be(0m);
        p.RealizedPnlRub.Should().Be(-10_000m);
        p.TotalResultRub.Should().Be(-10_000m);
    }

    [Fact]
    public void SecurityIn_RaisesQuantity_FlagsUnknownLots_NoCost()
    {
        var p = Calc(AssetKind.Bond, [B(40, LedgerEntryKind.SecurityIn, 0m, 10)], 10, 10_000m, 0m);
        p.HasUnknownLots.Should().BeTrue();
        p.CostRemainingRub.Should().Be(0m);
        p.QuantityNow.Should().Be(10m);
        p.UnrealizedPnlRub.Should().Be(0m, "введённые бумаги без цены не дают бумажной прибыли");
        p.TotalResultRub.Should().Be(0m);
    }

    [Fact]
    public void JournalQuantityMismatch_FlagsUnknownLots()
    {
        var p = Calc(AssetKind.Bond, [B(40, LedgerEntryKind.Buy, -5_000m, 5, 0m)], 10, 10_000m, 0m);
        p.HasUnknownLots.Should().BeTrue();
    }

    [Fact]
    public void ShortHolding_HasNoXirr()
    {
        var p = Calc(AssetKind.Bond, [B(10, LedgerEntryKind.Buy, -1_000m, 1, 0m)], 1, 1_010m, 0m);
        p.XirrFraction.Should().BeNull("владение < 30 дней");
    }

    [Fact]
    public void Rows_TrackQuantityAverageCostAndRealizedPerOperation()
    {
        var r = PositionIncomeCalculator.CalculateDetailed(AssetKind.Bond, 1, [
            B(90, LedgerEntryKind.Buy, -4_950m, 5, 0m),
            B(80, LedgerEntryKind.Buy, -5_050m, 5, 0m),
            B(10, LedgerEntryKind.Sell, 4_160m, 4, 40m),
        ], 6, 6_240m, 30m, Today);
        r.Rows.Select(x => x.QuantityAfter).Should().Equal(5m, 10m, 6m);
        r.Rows.Select(x => x.AvgCostAfterRub).Should().Equal(990m, 1_000m, 1_000m);
        r.Rows.Select(x => x.RealizedPnlRub).Should().Equal(0m, 0m, 120m);
        r.Rows[2].AccruedUsedRub.Should().Be(40m);
    }

    [Fact]
    public void EmptyJournal_WithPosition_IsAllUnknown()
    {
        var p = PositionIncomeCalculator.Calculate(AssetKind.Bond, 1, [], 5, 5_000m, 0m, Today);
        p.HasUnknownLots.Should().BeTrue();
        // Себестоимость неизвестна целиком: стоимость позиции — не заработок (раньше ошибочно показывалось +5 000).
        p.UnknownCostHoldingsRub.Should().Be(5_000m);
        p.TotalResultRub.Should().Be(0m);
    }
}

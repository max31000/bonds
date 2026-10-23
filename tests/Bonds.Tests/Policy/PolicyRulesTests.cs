using Bonds.Core.Capital;
using Bonds.Core.Fees;
using Bonds.Core.Policy;
using FluentAssertions;
using Xunit;
using static Bonds.Tests.Policy.PolicyTestKit;
using BrokerIds = Bonds.Core.Models.Brokers;

namespace Bonds.Tests.Policy;

/// <summary>
/// Задача 63: правила планов (P1/P2/P3b/P4/P5/P7, выход из корзины) — чистые функции, эталоны посчитаны руками. Деньги — рубли/доллары, доли — ДОЛИ.
/// Заявок нет нигде: проверяются только черновики планов.
/// </summary>
public class PolicyRulesTests
{
    private static readonly PolicyQuote Tmon = Fund("TMON@", 166.57m, 166.59m); // mid 166.58, шаг 0.01 → покупка 166.58
    private static readonly PolicyQuote Tsav = Fund("TSAV", 999.99m, 1000.01m); // mid 1000.00

    private static CapitalBasket Liquidity(bool autoBuy = true, bool autoSell = false, decimal? target = 0.15m, params CapitalBasketMember[] extra) =>
        Basket("liquidity", target, tier: 0, fill: BasketFillRules.Fixed, autoBuy: autoBuy, autoSell: autoSell,
            members: [Member("TMON@", 0.5m), Member("TSAV", 0.5m), .. extra]);

    private static CapitalBasket Bonds() =>
        Basket("bonds", 0.85m, tier: 2, fill: BasketFillRules.Engine, members: [ClassMember("bond")]);

    // ─── P1 ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void P1_Etalon_7340Rub_TmonTsav_HalfHalf_LotsAndLeftover()
    {
        // 7 340 ₽: бюджет члена 3 670. TMON@ 166.58 → 22 лота (3 664.76), TSAV 1 000.00 → 3 лота (3 000), остаток 675.24.
        // Остаток добирается по одному лоту тому, кому больше не хватает и кто помещается: TSAV (1 000) не влезает, TMON@ — 4 лота (4×166.58=666.32).
        // Итого TMON@ 26 лотов = 4 331.08, TSAV 3 = 3 000, остаток 7 340 − 7 331.08 = 8.92.
        var input = Input([Liquidity()], [], 7_340m, [Tmon, Tsav]);

        var outcome = SweepRule.Evaluate(input);

        outcome.Plan.Should().NotBeNull();
        var plan = outcome.Plan!;
        plan.Rule.Should().Be(PlanRules.P1Sweep);
        plan.Lines.Should().HaveCount(2);
        var tmon = plan.Lines.Single(l => l.Ticker == "TMON@");
        tmon.Side.Should().Be(PlanSides.Buy);
        tmon.QuantityLots.Should().Be(26);
        tmon.LimitPrice.Should().Be(166.58m);
        tmon.EstAmount.Should().Be(4_331.08m);
        var tsav = plan.Lines.Single(l => l.Ticker == "TSAV");
        tsav.QuantityLots.Should().Be(3);
        tsav.LimitPrice.Should().Be(1000.00m);
        plan.Summary.CashBefore.Should().Be(7_340m);
        plan.Summary.BuyTotal.Should().Be(7_331.08m);
        plan.Summary.CashAfter.Should().Be(8.92m);
        plan.Status.Should().Be(PlanStatuses.AutoApproved, "корзина auto_buy, заявки ≤ 25 000, день ≤ 50 000");
        plan.Summary.ExecutionNote.Should().Contain("dry-run");
    }

    [Fact]
    public void P1_PerOrderLimit_25000_MakesPlanPending()
    {
        // 60 000 ₽ → по 30 000 на члена: заявка TMON@ 180 лотов = 29 984.40 > 25 000 → Pending.
        var input = Input([Liquidity()], [], 60_000m, [Tmon, Tsav]);

        var plan = SweepRule.Evaluate(input).Plan!;

        plan.Lines.Max(l => l.EstAmount).Should().BeGreaterThan(25_000m);
        plan.Status.Should().Be(PlanStatuses.PendingApproval);
    }

    [Fact]
    public void P1_DayLimit_50000_MakesPlanPending_AndLimitComesFromOptions()
    {
        // Уже авто-подтверждено за сутки 40 000, новый план ~15 000 → 55 000 > 50 000.
        var pending = SweepRule.Evaluate(Input([Liquidity()], [], 15_000m, [Tmon, Tsav], autoToday: 40_000m)).Plan!;
        pending.Status.Should().Be(PlanStatuses.PendingApproval);

        // Лимиты — из ExecutionLimitsOptions (не из БД): поднимем дневной до 100 000 — снова авто.
        var raised = SweepRule.Evaluate(Input([Liquidity()], [], 15_000m, [Tmon, Tsav], autoToday: 40_000m,
            limits: new ExecutionLimitsOptions { AutoPerOrderRub = 25_000m, AutoPerDayRub = 100_000m })).Plan!;
        raised.Status.Should().Be(PlanStatuses.AutoApproved);
    }

    [Fact]
    public void P1_BasketWithoutAutoBuy_IsPending()
    {
        var plan = SweepRule.Evaluate(Input([Liquidity(autoBuy: false)], [], 7_340m, [Tmon, Tsav])).Plan!;
        plan.Status.Should().Be(PlanStatuses.PendingApproval);
    }

    [Fact]
    public void P1_ThirdPartyFundWithoutKnownCommission_IsSkippedWithReason()
    {
        var lqdt = Fund("LQDT", 1.99m, 2.01m, known: false);
        var basket = Basket("liquidity", 0.15m, autoBuy: true, members: [Member("TMON@", 0.5m), Member("LQDT", 0.5m)]);

        var plan = SweepRule.Evaluate(Input([basket], [], 7_340m, [Tmon, lqdt])).Plan!;

        plan.Lines.Select(l => l.Ticker).Should().Equal("TMON@");
        plan.Summary.Skipped.Should().ContainSingle(s => s.Ticker == "LQDT" && s.Reason.Contains("комисси"));
    }

    [Fact]
    public void P1_OutsideTradingHours_NoPlan_ButPreviewModeMarksPricesStale()
    {
        var closed = SweepRule.Evaluate(Input([Liquidity()], [], 7_340m, [Tmon, Tsav], now: ClosedNowUtc()));
        closed.Plan.Should().BeNull();
        closed.Notes.Should().Contain(n => n.Contains("рынок закрыт"));

        var preview = SweepRule.Evaluate(Input([Liquidity()], [], 7_340m, [Tmon, Tsav], now: ClosedNowUtc(), ignoreHours: true)).Plan!;
        preview.Summary.PricesPossiblyStale.Should().BeTrue();
    }

    [Fact]
    public void P1_CashBelowOneLot_NoPlan()
    {
        SweepRule.Evaluate(Input([Liquidity()], [], 100m, [Tmon, Tsav])).Plan.Should().BeNull();
    }

    [Fact]
    public void P1_FeeIsIncludedInBudget()
    {
        // Комиссия 0.3% входит в стоимость лота: на 1 000 ₽ при цене 999.99–1000.01 лот TSAV не помещается (1 003 > 1 000).
        var basket = Basket("liquidity", 0.15m, autoBuy: true, members: [Member("TSAV", 1m)]);
        SweepRule.Evaluate(Input([basket], [], 1_000m, [Tsav], feeRate: 0.003m)).Plan.Should().BeNull();

        var plan = SweepRule.Evaluate(Input([basket], [], 1_003m, [Tsav], feeRate: 0.003m)).Plan!;
        plan.Lines.Single().EstFee.Should().Be(3m);
        plan.Lines.Single().QuantityLots.Should().Be(1);
    }

    // ─── Цена ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Price_BuyRoundsDown_SellRoundsUp_ToStep()
    {
        var quote = Fund("X", 100.00m, 100.01m, step: 0.01m); // mid 100.005

        LinePricer.TryPrice(quote, PlanSides.Buy, out _)!.PricePerPiece.Should().Be(100.00m);
        LinePricer.TryPrice(quote, PlanSides.Sell, out _)!.PricePerPiece.Should().Be(100.01m);
        LinePricer.RoundToStep(100.00m, 0.05m, PlanSides.Sell).Should().Be(100.00m, "ровно на шаге — без смещения");
        LinePricer.RoundToStep(100.03m, 0.05m, PlanSides.Buy).Should().Be(100.00m);
        LinePricer.RoundToStep(100.03m, 0.05m, PlanSides.Sell).Should().Be(100.05m);
    }

    [Fact]
    public void Price_Bond_GridInPoints_RublesFromNominal()
    {
        // bid 99.94 / ask 99.97 пункта → mid 99.955; шаг 0.01 пункта: покупка 99.95, продажа 99.96. Номинал 1000 → 999.50 ₽ чистая, + НКД 10.
        var bond = Bond("RU000A0", 99.94m, 99.97m);

        var buy = LinePricer.TryPrice(bond, PlanSides.Buy, out _)!;
        buy.Points.Should().Be(99.95m);
        buy.PricePerPiece.Should().Be(999.50m);
        buy.DirtyPerPiece.Should().Be(1009.50m);
        LinePricer.TryPrice(bond, PlanSides.Sell, out _)!.Points.Should().Be(99.96m);
    }

    [Fact]
    public void Price_BondWithForeignNominal_UsesPointsGrid_AndReferencePriceForRubles()
    {
        var foreign = Bond("XS0", 100.00m, 100.02m, stepPts: 0.01m) with { NominalRub = null, ReferenceCleanPricePerPiece = 8_000m };

        var priced = LinePricer.TryPrice(foreign, PlanSides.Buy, out _)!;

        priced.Points.Should().Be(100.01m, "лимит — по сетке в пунктах");
        priced.PricePerPiece.Should().Be(8_000m, "рубли для суммы — оценка по последней известной цене");

        LinePricer.TryPrice(foreign with { ReferenceCleanPricePerPiece = null }, PlanSides.Buy, out _).Should().BeNull();
    }

    [Fact]
    public void Price_NoBook_NoPrice()
    {
        LinePricer.TryPrice(Fund("X", 0m, 10m), PlanSides.Buy, out var why).Should().BeNull();
        why.Should().Contain("стакан");
    }

    [Fact]
    public void TradingHours_MoexWindow_Weekdays10To1840Msk()
    {
        var wed = OpenNowUtc().Date; // среда 00:00 UTC
        var options = new PolicyOptions();
        TradingHours.IsOpen(PolicyMarket.Moex, wed.AddHours(6).AddMinutes(59), options).Should().BeFalse("09:59 МСК");
        TradingHours.IsOpen(PolicyMarket.Moex, wed.AddHours(7), options).Should().BeTrue("10:00 МСК");
        TradingHours.IsOpen(PolicyMarket.Moex, wed.AddHours(15).AddMinutes(39), options).Should().BeTrue("18:39 МСК");
        TradingHours.IsOpen(PolicyMarket.Moex, wed.AddHours(15).AddMinutes(40), options).Should().BeFalse("18:40 МСК");
        TradingHours.IsOpen(PolicyMarket.Moex, wed.AddDays(3).AddHours(9), options).Should().BeFalse("суббота");
        TradingHours.MarketOf(BrokerIds.TInvest, "LQDT", "SPBRU").Should().Be(PolicyMarket.SpbRu);
        TradingHours.MarketOf(BrokerIds.Freedom, "VUAA.EU", null).Should().Be(PolicyMarket.FreedomEu);
    }

    // ─── P2 ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void P2_SplitsByDeficit_BondPartMakesWholePlanPending()
    {
        // Портфель 1 000 000: ликвидность 100 000 (цель 0.15 → дефицит 50 000), облигации 800 000 (цель 0.85 → дефицит 50 000), свободно 100 000.
        var baskets = new[] { Liquidity(), Bonds() };
        var positions = new[] { Pos("TMON@", "fund", 100_000m), Pos("RU000A", "bond", 800_000m) };
        var input = Input(baskets, positions, 100_000m, [Tmon, Tsav], candidates:
        [
            Candidate("RU000C1", 0.12m, 0.13m, "Корпоративные"),
            Candidate("RU000C2", 0.11m, 0.12m, "Муниципальные"),
        ]);

        var plan = InflowRule.Evaluate(input, 100_000m, "Пополнение").Plan!;

        plan.Rule.Should().Be(PlanRules.P2Inflow);
        // Ликвидность 50 000: по 25 000 на члена → TMON@ 150 лотов (24 987), TSAV 25 лотов (25 000).
        plan.Lines.Single(l => l.Ticker == "TMON@").QuantityLots.Should().Be(150);
        plan.Lines.Single(l => l.Ticker == "TSAV").QuantityLots.Should().Be(25);
        // Облигации — по 1 лоту на бумагу: 99.90…100.10 → mid 100.00 пунктов → 1000 + НКД 10 = 1010 за штуку.
        plan.Lines.Where(l => l.Ticker.StartsWith("RU000C")).Should().HaveCount(2).And.OnlyContain(l => l.EstAmount == 1_010m);
        plan.Lines.First(l => l.Ticker == "RU000C1").LimitPricePoints.Should().Be(100.00m);
        plan.Status.Should().Be(PlanStatuses.PendingApproval, "облигационная часть — всегда на подтверждение");
        plan.Summary.BuyTotal.Should().BeLessThanOrEqualTo(100_000m);
    }

    [Fact]
    public void P2_OnlyFixedBaskets_WithAutoBuy_IsAutoApproved()
    {
        // Только ликвидность, 20 000 ₽: по 10 000 → TMON@ 60 лотов, TSAV 10 лотов.
        var input = Input([Liquidity(target: 1m)], [], 20_000m, [Tmon, Tsav]);

        var plan = InflowRule.Evaluate(input, 20_000m, "Пополнение").Plan!;

        plan.Lines.Single(l => l.Ticker == "TMON@").QuantityLots.Should().Be(60);
        plan.Lines.Single(l => l.Ticker == "TSAV").QuantityLots.Should().Be(10);
        plan.Status.Should().Be(PlanStatuses.AutoApproved);
    }

    [Fact]
    public void P2_AmountIsCappedByFreeCash_HoldBasketsGetNothing()
    {
        var hold = Basket("temporary", 0.1m, fill: BasketFillRules.Hold, members: [Member("TPAY")]);
        var outcome = InflowRule.Evaluate(Input([Liquidity(target: 0.5m), hold], [], 5_000m, [Tmon, Tsav, Fund("TPAY", 9m, 11m)]), 50_000m, "t");

        outcome.Plan!.Lines.Should().NotContain(l => l.Ticker == "TPAY");
        outcome.Plan.Summary.CashBefore.Should().Be(5_000m);
        outcome.Plan.Summary.BuyTotal.Should().BeLessThanOrEqualTo(5_000m);
        outcome.Notes.Should().Contain(n => n.Contains("ограничена"));
    }

    // ─── P3b ───────────────────────────────────────────────────────────────────────────────

    private static PolicyInput P3bInput(IReadOnlyList<PolicyBondCandidate> candidates, decimal? fundYield = 0.10m)
    {
        // Ликвидность 1 800 шт TMON@ × 166.58 = 299 844 при цели 0.15 (портфель 1 000 000): вес ≈ 0.30 > 0.20, HardAbove; излишек 149 844.
        var baskets = new[] { Liquidity(target: 0.15m, extra: []), Bonds() };
        var positions = new[] { Pos("TMON@", "fund", 299_844m), Pos("RU000A", "bond", 700_156m) };
        return Input(baskets, positions, 0m, [Tmon, Tsav], holdings: [Holding("TMON@", 1_800m, 299_844m, 150m, "liquidity")],
            candidates: candidates, fundYield: fundYield);
    }

    [Fact]
    public void P3b_PremiumFilter_1p5Points_KeepsOnlyBondsAtOrAboveFundPlusPremium()
    {
        // Фонд 10.0% после налога + премия 1.5 п.п. = порог 11.5%. A 12.0% — проходит, B 11.4% — нет, C ровно 11.5% — проходит.
        var input = P3bInput([Candidate("RU000A1", 0.120m, 0.13m), Candidate("RU000B1", 0.114m, 0.14m), Candidate("RU000C1", 0.115m, 0.11m)]);

        var plan = LiquidityToBondsRule.Evaluate(input).Plan!;

        plan.Rule.Should().Be(PlanRules.P3bLiqToBonds);
        plan.Status.Should().Be(PlanStatuses.PendingApproval);
        var sell = plan.Lines.Single(l => l.Side == PlanSides.Sell);
        sell.Ticker.Should().Be("TMON@");
        // 149 844 / 166.58 = 899.5 → 899 лотов = 149 755.42; налог 13% × (16.58 × 899) = 1 937.70.
        sell.QuantityLots.Should().Be(899);
        sell.EstAmount.Should().Be(149_755.42m);
        sell.EstTax.Should().BeApproximately(1_937.7046m, 0.001m);
        plan.Lines.Where(l => l.Side == PlanSides.Buy).Select(l => l.Ticker).Should().BeEquivalentTo("RU000A1", "RU000C1");
        plan.Summary.Skipped.Should().Contain(s => s.Ticker == "RU000B1" && s.Reason.Contains("ниже порога"));
        plan.Trigger.Should().Contain("-W");
    }

    [Fact]
    public void P3b_NoCandidateBeatsThePremium_NoPlan_ReplacementNotFound()
    {
        var outcome = LiquidityToBondsRule.Evaluate(P3bInput([Candidate("RU000B1", 0.114m, 0.14m)]));

        outcome.Plan.Should().BeNull();
        outcome.Notes.Should().Contain(n => n.Contains("замены нет"));
    }

    [Fact]
    public void P3b_LiquidityInsideBand_OrNoFundYield_NoPlan()
    {
        var baskets = new[] { Liquidity(target: 0.15m), Bonds() };
        var ok = Input(baskets, [Pos("TMON@", "fund", 150_000m), Pos("RU000A", "bond", 850_000m)], 0m, [Tmon], fundYield: 0.10m);
        LiquidityToBondsRule.Evaluate(ok).Plan.Should().BeNull();

        LiquidityToBondsRule.Evaluate(P3bInput([Candidate("RU000A1", 0.12m, 0.13m)], fundYield: null)).Plan.Should().BeNull();
    }

    // ─── P4 ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void P4_HardBelow_BuysDeficitFromFreeCash_AlwaysPending()
    {
        // Облигации 800 000 + свободно 200 000; ликвидность 0 при цели 0.15 → дефицит 150 000, HardBelow.
        var input = Input([Liquidity(), Bonds()], [Pos("RU000A", "bond", 800_000m)], 200_000m, [Tmon, Tsav]);

        var plan = RebalanceRule.Evaluate(input).Plan!;

        plan.Rule.Should().Be(PlanRules.P4Rebalance);
        plan.Status.Should().Be(PlanStatuses.PendingApproval);
        plan.Lines.Should().OnlyContain(l => l.Side == PlanSides.Buy);
        plan.Summary.BuyTotal.Should().BeInRange(149_000m, 150_000m);
    }

    [Fact]
    public void P4_HardAbove_SellsOnlyWhereAutoSellEnabled_ThenBuysDeficit()
    {
        var holdings = new[] { Holding("TMON@", 1_800m, 299_844m, 150m, "liquidity") };
        var positions = new[] { Pos("TMON@", "fund", 299_844m), Pos("RU000A", "bond", 700_156m) };
        var candidates = new[] { Candidate("RU000A1", 0.12m, 0.13m) };

        var withSell = RebalanceRule.Evaluate(Input([Liquidity(autoSell: true), Bonds()], positions, 0m, [Tmon, Tsav], holdings, candidates)).Plan!;
        withSell.Lines.Should().Contain(l => l.Side == PlanSides.Sell && l.Ticker == "TMON@");
        withSell.Lines.Should().Contain(l => l.Side == PlanSides.Buy && l.Ticker == "RU000A1");

        var noSell = RebalanceRule.Evaluate(Input([Liquidity(autoSell: false), Bonds()], positions, 0m, [Tmon, Tsav], holdings, candidates));
        noSell.Plan.Should().BeNull("auto_sell выключен, свободных денег нет — продавать и покупать не на что");
        noSell.Notes.Should().Contain(n => n.Contains("auto_sell"));
    }

    [Fact]
    public void P4_HoldBasketsAreNeverTouched_AndInsideBandNoPlan()
    {
        var hold = Basket("temporary", 0.5m, fill: BasketFillRules.Hold, members: [Member("TPAY")]);
        RebalanceRule.Evaluate(Input([hold], [], 1_000m, [Fund("TPAY", 9m, 11m)])).Plan.Should().BeNull();
        RebalanceRule.Evaluate(Input([Liquidity(target: 0.15m), Bonds()], [Pos("TMON@", "fund", 150_000m), Pos("RU000A", "bond", 850_000m)], 0m, [Tmon])).Plan.Should().BeNull();
    }

    // ─── P5 ────────────────────────────────────────────────────────────────────────────────

    private static PolicyInput WithdrawInput(decimal freeCash = 0m, decimal feeRate = 0m, IReadOnlyList<PolicyHolding>? holdings = null)
    {
        var temporary = Basket("temporary", null, tier: 1, fill: BasketFillRules.Hold, members: [Member("TPAY")]);
        var baskets = new[] { Liquidity(), Bonds(), temporary };
        return Input(baskets, [Pos("TMON@", "fund", 16_658m), Pos("TSAV", "fund", 10_000m)], freeCash, [Tmon, Tsav, Fund("TPAY", 99.99m, 100.01m)],
            holdings ??
            [
                Holding("TMON@", 100m, 16_658m, 150m, "liquidity"),   // прибыль 16.58 на штуку
                Holding("TSAV", 10m, 10_000m, 1_100m, "liquidity"),   // убыток 100 на штуку
                Holding("TPAY", 500m, 50_000m, 100m, "temporary"),
            ], feeRate: feeRate);
    }

    [Fact]
    public void P5_SellsLossMakingFirstWithinTier_AndStopsWhenAmountIsReached()
    {
        // Вывод 5 000 ₽: в тире 0 сначала убыточный TSAV (налог 0) → 5 лотов по 1 000; TMON@ (с прибылью) не трогаем.
        var outcome = WithdrawRule.Evaluate(WithdrawInput(), 5_000m, null, null);

        var plan = outcome.Plan!;
        plan.Rule.Should().Be(PlanRules.P5Withdraw);
        plan.Status.Should().Be(PlanStatuses.PendingApproval);
        plan.Lines.Should().ContainSingle();
        plan.Lines[0].Ticker.Should().Be("TSAV");
        plan.Lines[0].QuantityLots.Should().Be(5);
        plan.Lines[0].EstTax.Should().Be(0m);
        plan.Summary.RequestedNet.Should().Be(5_000m);
        plan.Summary.NetToWithdraw.Should().Be(5_000m);
        plan.Summary.Shortfall.Should().BeNull();
    }

    [Fact]
    public void P5_TopsUpAcrossPositions_WithTaxOnTheGainingOne()
    {
        // 12 000: TSAV целиком (10 лотов = 10 000, налога нет) + остаток 2 000 из TMON@: чистая выручка с штуки 166.58 − 13%×16.58 = 164.4246 →
        // 12 штук дают 1 973.10 (< 2 000), 13 штук — 2 137.52 (≥ 2 000). Налог 13 штук: 13% × (13×166.58 − 13×150) = 28.0202.
        var plan = WithdrawRule.Evaluate(WithdrawInput(), 12_000m, null, null).Plan!;

        plan.Lines.Select(l => (l.Ticker, l.QuantityLots)).Should().Equal(("TSAV", 10L), ("TMON@", 13L));
        plan.Lines[1].EstTax.Should().BeApproximately(28.0202m, 0.0001m);
        plan.Summary.TaxTotal.Should().BeApproximately(28.0202m, 0.0001m);
        plan.Summary.NetToWithdraw.Should().BeApproximately(12_137.5198m, 0.001m);
    }

    [Fact]
    public void P5_IncludesCommissionInTheNetTarget()
    {
        // Комиссия 0.3%: один лот TSAV даёт 1 000 − 3 = 997 < 1 000 → нужны 2 лота (чистыми 1 994).
        var plan = WithdrawRule.Evaluate(WithdrawInput(feeRate: 0.003m), 1_000m, null, null).Plan!;

        plan.Lines.Single().QuantityLots.Should().Be(2);
        plan.Lines.Single().EstFee.Should().Be(6m);
        plan.Summary.NetToWithdraw.Should().Be(1_994m);
        plan.Summary.FeeTotal.Should().Be(6m);
    }

    [Fact]
    public void P5_FreeCashGoesFirst_NoSalesWhenItCoversTheAmount()
    {
        WithdrawRule.Evaluate(WithdrawInput(freeCash: 8_000m), 5_000m, null, null).Plan.Should().BeNull("хватает свободных денег");

        var plan = WithdrawRule.Evaluate(WithdrawInput(freeCash: 3_000m), 5_000m, null, null).Plan!;
        plan.Lines.Single().QuantityLots.Should().Be(2, "нужно продать на 2 000: 2 лота TSAV");
    }

    [Fact]
    public void P5_HoldAndTemporary_OnlyWhenBasketIsExplicitlyIncluded()
    {
        // Вне liquidity и bonds в продаже нечего: 100 000 > 26 658 → нехватка; временная корзина (hold) не трогается.
        var denied = WithdrawRule.Evaluate(WithdrawInput(), 60_000m, null, null).Plan!;
        denied.Lines.Should().NotContain(l => l.Ticker == "TPAY");
        denied.Summary.Shortfall.Should().BePositive();
        denied.Summary.Warnings.Should().Contain(w => w.Contains("Не хватает"));

        var allowed = WithdrawRule.Evaluate(WithdrawInput(), 60_000m, null, ["temporary"]).Plan!;
        allowed.Lines.Should().Contain(l => l.Ticker == "TPAY");
        allowed.Summary.Shortfall.Should().BeNull();
    }

    [Fact]
    public void P5_Fraction_OfPortfolioTotal_AndFreedomWarning()
    {
        // 10% от портфеля 26 658 ₽ = 2 665.80 → 3 лота TSAV (чистыми 3 000).
        var plan = WithdrawRule.Evaluate(WithdrawInput(), null, 0.1m, null).Plan!;
        plan.Summary.RequestedNet.Should().Be(2_665.80m);
        plan.Lines.Single().QuantityLots.Should().Be(3);

        var usdBasket = Basket("global_core", 1m, CapitalPortfolios.Usd, 1, members: [Member("VUAA.EU", 1m, BrokerIds.Freedom)]);
        var etf = FreedomEtf("VUAA.EU", 100.00m, 100.02m);
        var input = Input([usdBasket], [Pos("VUAA.EU", "etf", 5_000m, BrokerIds.Freedom)], 0m, [etf],
            [Holding("VUAA.EU", 50m, 5_000m, 90m, "global_core") with { Broker = BrokerIds.Freedom }], portfolio: CapitalPortfolios.Usd);
        var freedom = WithdrawRule.Evaluate(input, 1_000m, null, null).Plan!;
        freedom.Summary.Warnings.Should().Contain(w => w.Contains("$20"));
        freedom.Summary.Currency.Should().Be("USD");
    }

    [Fact]
    public void P5_UnknownCostBasis_TaxIsEstimatedFromAbove_WithWarning()
    {
        var holdings = new[] { Holding("TMON@", 100m, 16_658m, null, "liquidity", unknownCost: true) };
        var plan = WithdrawRule.Evaluate(WithdrawInput(holdings: holdings), 1_000m, null, null).Plan!;

        plan.Lines.Single().EstTax.Should().BePositive();
        plan.Summary.Warnings.Should().Contain(w => w.Contains("оценён сверху"));
    }

    // ─── Выход из корзины ──────────────────────────────────────────────────────────────────

    [Fact]
    public void ExitBasket_SellsAllMembersInWholeLots_Pending()
    {
        var temporary = Basket("temporary", null, tier: 1, fill: BasketFillRules.Hold, members: [Member("TPAY")]);
        var input = Input([temporary], [Pos("TPAY", "fund", 50_000m)], 0m, [Fund("TPAY", 99.99m, 100.01m)],
            [Holding("TPAY", 500m, 50_000m, 100m, "temporary")]);

        var plan = ExitBasketRule.Evaluate(input, "temporary").Plan!;

        plan.Status.Should().Be(PlanStatuses.PendingApproval);
        plan.Lines.Single().QuantityLots.Should().Be(500);
        plan.Lines.Single().Side.Should().Be(PlanSides.Sell);
        ExitBasketRule.Evaluate(input, "nope").Plan.Should().BeNull();
    }

    // ─── P7 ────────────────────────────────────────────────────────────────────────────────

    private static PolicyInput P7Input(decimal usd, decimal spymBid = 72.00m, decimal spymAsk = 72.02m)
    {
        var core = Basket("global_core", 1m, CapitalPortfolios.Usd, 1, members: [Member("VUAA.EU", 0.8m, BrokerIds.Freedom), Member("SPYM.US", 0.2m, BrokerIds.Freedom)]);
        return Input([core], [], usd, [FreedomEtf("SPYM.US", spymBid, spymAsk), FreedomEtf("VUAA.EU", 110.00m, 110.04m)],
            portfolio: CapitalPortfolios.Usd, now: FreedomOpenUtc());
    }

    /// <summary>Среда 12:00 МСК: Европа (11:00–18:30) открыта, США (17:30–23:00) ещё закрыты — тесты, где важны часы, не игнорируют их явно.</summary>
    private static DateTime FreedomOpenUtc() => OpenNowUtc();

    [Theory]
    [InlineData("49.99", false)]
    [InlineData("50", true)]
    public void P7_Threshold50Usd(string cash, bool plan)
    {
        // SPYM.US по 40.01 — один лот помещается в $50; порог проверяется ДО подбора лотов.
        var input = P7Input(decimal.Parse(cash, System.Globalization.CultureInfo.InvariantCulture), 40.00m, 40.02m) with { IgnoreTradingHours = true };
        (FreedomTopUpRule.Evaluate(input).Plan is not null).Should().Be(plan);
    }

    [Theory]
    [InlineData("315.00", "SPYM.US")]
    [InlineData("315.78", "SPYM.US")]
    [InlineData("315.79", "VUAA.EU")]
    [InlineData("316", "VUAA.EU")]
    [InlineData("400", "VUAA.EU")]
    public void P7_BreakEvenBoundary_315_PicksTheCheaperRoute(string cash, string ticker)
    {
        // Точка безубыточности 1.2 / (0.005 − 0.0012) = 315.789…: до неё ITS-фонды (SPYM.US), дальше Europe (VUAA.EU).
        var usd = decimal.Parse(cash, System.Globalization.CultureInfo.InvariantCulture);
        var plan = FreedomTopUpRule.Evaluate(P7Input(usd) with { IgnoreTradingHours = true }).Plan!;

        plan.Lines.Single().Ticker.Should().Be(ticker);
        plan.Status.Should().Be(PlanStatuses.PendingApproval, "авто на Freedom = 0");
    }

    [Fact]
    public void P7_Etalons_Spym315_And_Vuaa400()
    {
        // $315: SPYM.US mid 72.01 → покупка 72.01; 4 лота = 288.04, комиссия ITS 0.5% = 1.44 (5 лотов 360.05 + 1.80 не помещаются).
        var spym = FreedomTopUpRule.Evaluate(P7Input(315m) with { IgnoreTradingHours = true }).Plan!.Lines.Single();
        spym.QuantityLots.Should().Be(4);
        spym.EstAmount.Should().Be(288.04m);
        spym.EstFee.Should().Be(1.44m);
        spym.Currency.Should().Be("USD");

        // $400: VUAA.EU mid 110.02; 3 лота = 330.06, комиссия 0.12% + $1.2 = 1.60 (4 лота 440.08 > 400).
        var vuaa = FreedomTopUpRule.Evaluate(P7Input(400m) with { IgnoreTradingHours = true }).Plan!.Lines.Single();
        vuaa.QuantityLots.Should().Be(3);
        vuaa.EstAmount.Should().Be(330.06m);
        vuaa.EstFee.Should().Be(1.60m);
    }

    [Fact]
    public void P7_OutsideFreedomHours_NoPlan()
    {
        var input = P7Input(100m); // NYSE по МСК 17:30–23:00: 12:00 МСК — закрыто
        FreedomTopUpRule.Evaluate(input).Plan.Should().BeNull();
    }

    // ─── Прочее ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Plan_ExpiresAtMostAtSessionClose_AndWithinTtl()
    {
        var input = Input([Liquidity()], [], 7_340m, [Tmon, Tsav]);
        var plan = SweepRule.Evaluate(input).Plan!;
        plan.ExpiresAtUtc.Should().Be(input.NowUtc + TimeSpan.FromMinutes(30));

        // За 10 минут до закрытия Мосбиржи (18:40 МСК = 15:40 UTC) план живёт только до закрытия.
        var late = Input([Liquidity()], [], 7_340m, [Tmon, Tsav], now: OpenNowUtc().Date.AddHours(15).AddMinutes(30));
        SweepRule.Evaluate(late).Plan!.ExpiresAtUtc.Should().Be(OpenNowUtc().Date.AddHours(15).AddMinutes(40));
    }

    [Fact]
    public void ExecutionOptions_DryRunIsAlwaysEffectiveInPhase2()
    {
        new ExecutionOptions { DryRun = false }.EffectiveDryRun.Should().BeTrue();
        new ExecutionOptions().DryRun.Should().BeTrue();
        new ExecutionLimitsOptions().Should().BeEquivalentTo(new ExecutionLimitsOptions { AutoPerOrderRub = 25_000m, AutoPerDayRub = 50_000m, FreedomAutoUsd = 0m });
        FreedomFeeModel.CheaperRoute(315m).Should().Be(FreedomMarket.KazakhstanItsFunds);
    }
}

using Bonds.Core.Capital;
using Bonds.Core.Fees;
using Bonds.Core.Policy;
using Bonds.Infrastructure.Policy;
using FluentAssertions;
using Xunit;
using static Bonds.Tests.Policy.PolicyTestKit;
using BrokerIds = Bonds.Core.Models.Brokers;

namespace Bonds.Tests.Policy;

/// <summary>
/// Задача 63, фиксы по ревью: суточный лимит внутри цикла, фонды УК без комиссии, маршрут P7 по реальным комиссиям, единая валюта комиссий и резерва,
/// рублёвые облигации в автоподборе, санити-проверка стакана, P1 не льёт в переполненную корзину, лимит эмитента внутри одного плана.
/// </summary>
public class PolicyReviewFixesTests
{
    private static readonly PolicyQuote Tmon = Fund("TMON@", 166.57m, 166.59m);
    private static readonly PolicyQuote Tsav = Fund("TSAV", 999.99m, 1000.01m);

    private static CapitalBasket Liquidity(decimal target = 0.15m) =>
        Basket("liquidity", target, tier: 0, fill: BasketFillRules.Fixed, autoBuy: true, members: [Member("TMON@", 0.5m), Member("TSAV", 0.5m)]);

    // ─── 1. Суточный лимит внутри одного цикла ─────────────────────────────────────────────

    [Fact]
    public void RunnerCarryForward_AutoApprovedPlan_RaisesDayCounter_SoSecondAutoPlanOfSameCycleBecomesPending()
    {
        // Свободно 60 000 ₽. P2 на 40 000 → авто (по 20 000 на заявку ≤ 25 000, день 40 000 ≤ 50 000).
        var input = Input([Liquidity()], [], 60_000m, [Tmon, Tsav]);
        var p2 = InflowRule.Evaluate(input, 40_000m, "Пополнение 40 000").Plan!;
        p2.Status.Should().Be(PlanStatuses.AutoApproved);

        // Без переноса счётчика (прежнее поведение) P1 на остаток ~20 000 тоже стал бы авто — 40 000 + ~20 000 > 50 000 дневной лимит был бы обойдён.
        var carried = PolicyRunner.CarryForward(input, p2, p2.Status);
        carried.AutoApprovedTodayRub.Should().Be(p2.Lines.Where(l => l.Side == PlanSides.Buy).Sum(l => l.EstAmount));
        carried.AutoApprovedTodayRub.Should().BeApproximately(40_000m, 400m);
        carried.FreeCash.Should().Be(60_000m - p2.Summary.ReservedBuy);
        carried.ReservedCash.Should().Be(p2.Summary.ReservedBuy);

        SweepRule.Evaluate(carried with { AutoApprovedTodayRub = 0m }).Plan!.Status.Should().Be(PlanStatuses.AutoApproved, "контроль: без переноса счётчика второй план проскочил бы");
        var p1 = SweepRule.Evaluate(carried).Plan!;
        p1.Status.Should().Be(PlanStatuses.PendingApproval, "40 000 + остаток ~20 000 > 50 000 ₽ за день");
    }

    [Fact]
    public void RunnerCarryForward_PendingPlan_ReservesCash_ButDoesNotTouchDayCounter()
    {
        var input = Input([Liquidity()], [], 60_000m, [Tmon, Tsav], autoToday: 5m);
        var pending = SweepRule.Evaluate(Input([Liquidity(autoBuy: false)], [], 60_000m, [Tmon, Tsav], autoToday: 5m)).Plan!;
        pending.Status.Should().Be(PlanStatuses.PendingApproval);

        var carried = PolicyRunner.CarryForward(input, pending, pending.Status);

        carried.AutoApprovedTodayRub.Should().Be(5m);
        carried.FreeCash.Should().BeLessThan(60_000m);
    }

    private static CapitalBasket Liquidity(bool autoBuy) =>
        Basket("liquidity", 0.15m, tier: 0, fill: BasketFillRules.Fixed, autoBuy: autoBuy, members: [Member("TMON@", 0.5m), Member("TSAV", 0.5m)]);

    // ─── 2. Фонды УК брокера: комиссия 0 ──────────────────────────────────────────────────

    [Fact]
    public void P1_OwnFund_HasZeroFee_SoFeeRate0003_StillBuysMaxLots()
    {
        // TMON@ — фонд УК брокера: комиссия 0 при ставке 0.3%. 10 000 / 166.58 = 60.03 → 60 лотов (9 994.80), комиссии нет.
        var basket = Basket("liquidity", 0.15m, autoBuy: true, members: [Member("TMON@", 1m)]);

        var line = SweepRule.Evaluate(Input([basket], [], 10_000m, [Tmon], feeRate: 0.003m)).Plan!.Lines.Single();

        line.QuantityLots.Should().Be(60);
        line.EstAmount.Should().Be(9_994.80m);
        line.EstFee.Should().Be(0m);
    }

    [Fact]
    public void FeeModel_OwnFundsAreCaseAndAtSignInsensitive_ThirdPartyPaysRate()
    {
        var fees = new PlanFeeModel(0.003m, PlanFeeModel.DefaultFreedomTariffId);
        fees.Estimate(Tmon, 1_000m, 6m).Should().Be(0m);
        fees.Estimate(Fund("tsav", 1m, 1.01m), 1_000m, 1m).Should().Be(0m);
        fees.Estimate(Fund("LQDT", 1m, 1.01m), 1_000m, 1m).Should().Be(3m, "сторонний фонд — по ставке");
        new PlanFeeModel(0.003m, 1431, new HashSet<string> { "LQDT" }).Estimate(Fund("LQDT@", 1m, 1.01m), 1_000m, 1m).Should().Be(0m, "список из опций");
    }

    // ─── 3. P7: маршрут по реальным комиссиям и тарифу ────────────────────────────────────

    private static PolicyInput P7Input(decimal usd, int tariff) =>
        Input([Basket("global_core", 1m, CapitalPortfolios.Usd, 1, members: [Member("VUAA.EU", 0.8m, BrokerIds.Freedom), Member("SPYM.US", 0.2m, BrokerIds.Freedom)])],
                [], usd, [FreedomEtf("SPYM.US", 72.00m, 72.02m), FreedomEtf("VUAA.EU", 110.00m, 110.04m)], portfolio: CapitalPortfolios.Usd) with
        {
            Fees = new PlanFeeModel(0m, tariff),
            IgnoreTradingHours = true,
        };

    [Fact]
    public void P7_Investor400Usd_PicksItsFunds_StandardPicksEurope()
    {
        // $400. Standard: SPYM 5 шт → 0.5% = 2.00; VUAA 3 шт → 0.12% + 1.2 = 1.68 → Europe дешевле.
        FreedomTopUpRule.Evaluate(P7Input(400m, FreedomFeeModel.StandardTariffId)).Plan!.Lines.Single().Ticker.Should().Be("VUAA.EU");

        // Investor: ITS 0.5% + 0.012/шт = 2.06; Europe 0.5% + 1.2 + 0.012/шт = 3.24 → ITS дешевле при ЛЮБОЙ сумме (CheaperRoute знает только Standard).
        var investor = FreedomTopUpRule.Evaluate(P7Input(400m, FreedomFeeModel.InvestorTariffId)).Plan!;
        investor.Lines.Single().Ticker.Should().Be("SPYM.US");
        investor.Summary.Notes.Should().Contain(n => n.Contains("1430"));
    }

    // ─── 4. Единая валюта ─────────────────────────────────────────────────────────────────

    [Fact]
    public void P7_Vuaa_QuotedInUsd_FeeInUsd_ReservedBuyInPortfolioCurrency()
    {
        var input = P7Input(400m, FreedomFeeModel.StandardTariffId);

        var plan = FreedomTopUpRule.Evaluate(input).Plan!;

        // 3 лота по 110.02 = 330.06 USD, комиссия 0.12% × 330.06 + 1.2 = 1.5961 → 1.60 USD.
        var line = plan.Lines.Single();
        line.Currency.Should().Be("USD");
        line.EstAmount.Should().Be(330.06m);
        line.EstFee.Should().Be(1.60m);
        plan.Summary.Currency.Should().Be("USD");
        plan.Summary.BuyTotal.Should().Be(330.06m);
        plan.Summary.ReservedBuy.Should().Be(331.66m);
        plan.Summary.CashAfter.Should().Be(400m - 331.66m);
    }

    [Fact]
    public void P7_Vuaa_QuotedInEur_FeeConvertedToPriceCurrency_ReservedBuyInUsd()
    {
        // VUAA.EU в евро, курс 1 EUR = 1.1 USD. 3 лота по 100.02 EUR = 300.06 EUR = 330.066 USD; комиссия Freedom в USD: 0.12% × 330.066 + 1.2 = 1.5961 → 1.60 USD
        // = 1.6/1.1 EUR в валюте цены строки. 4 лота = 440.088 USD > 400. В валюте портфеля (USD): покупка 330.066 + комиссия 1.60 = 331.666.
        var eur = FreedomEtf("VUAA.EU", 100.00m, 100.04m) with { Currency = "EUR", FxToPortfolio = 1.1m };
        var input = Input([Basket("global_core", 1m, CapitalPortfolios.Usd, 1, members: [Member("VUAA.EU", 1m, BrokerIds.Freedom)])],
            [], 400m, [eur], portfolio: CapitalPortfolios.Usd);

        var plan = FreedomTopUpRule.Evaluate(input).Plan!;

        var line = plan.Lines.Single();
        line.Currency.Should().Be("EUR");
        line.QuantityLots.Should().Be(3);
        line.EstAmount.Should().Be(300.06m);
        line.EstFee.Should().BeApproximately(1.60m / 1.1m, 0.0000001m, "комиссия переведена из USD в валюту цены (EUR)");
        plan.Summary.Currency.Should().Be("USD");
        plan.Summary.BuyTotal.Should().BeApproximately(330.066m, 0.0000001m);
        plan.Summary.FeeTotal.Should().BeApproximately(1.60m, 0.0000001m);
        plan.Summary.ReservedBuy.Should().BeApproximately(331.666m, 0.0000001m, "резерв — в валюте портфеля, не EUR+USD вперемешку");
        plan.Summary.CashAfter.Should().BeApproximately(400m - 331.666m, 0.0000001m);
    }

    // ─── 5. Нерублёвые облигации — не в автоподбор ────────────────────────────────────────

    private static PolicyInput P3bInput(IReadOnlyList<PolicyBondCandidate> candidates)
    {
        var baskets = new[]
        {
            Basket("liquidity", 0.15m, tier: 0, members: [Member("TMON@", 0.5m), Member("TSAV", 0.5m)]),
            Basket("bonds", 0.85m, tier: 2, fill: BasketFillRules.Engine, members: [ClassMember("bond")]),
        };
        var positions = new[] { Pos("TMON@", "fund", 299_844m), Pos("RU000A", "bond", 700_156m) };
        return Input(baskets, positions, 0m, [Tmon, Tsav], holdings: [Holding("TMON@", 1_800m, 299_844m, 150m, "liquidity")],
            candidates: candidates, fundYield: 0.10m);
    }

    [Fact]
    public void P3b_BondWithForeignNominal_IsExcludedFromAutomaticSelection_WithReason()
    {
        var good = Candidate("RU000A1", 0.12m, 0.13m);
        var foreignNominal = Candidate("XS0001", 0.20m, 0.22m);
        foreignNominal = foreignNominal with { Quote = foreignNominal.Quote! with { NominalRub = null, ReferenceCleanPricePerPiece = 1_000m } };
        var usdQuoted = Candidate("XS0002", 0.20m, 0.22m);
        usdQuoted = usdQuoted with { Quote = usdQuoted.Quote! with { Currency = "USD" } };

        var plan = LiquidityToBondsRule.Evaluate(P3bInput([good, foreignNominal, usdQuoted])).Plan!;

        plan.Lines.Where(l => l.Side == PlanSides.Buy).Select(l => l.Ticker).Should().Equal("RU000A1");
        plan.Summary.Skipped.Should().Contain(s => s.Ticker == "XS0001" && s.Reason.Contains("не в рублях"));
        plan.Summary.Skipped.Should().Contain(s => s.Ticker == "XS0002" && s.Reason.Contains("не в рублях"));
    }

    // ─── 6. Санити-проверка стакана ───────────────────────────────────────────────────────

    [Fact]
    public void Price_InvertedBook_IsSkipped()
    {
        LinePricer.TryPrice(Fund("X", 101m, 100m), PlanSides.Buy, out var why).Should().BeNull();
        why.Should().Contain("ask < bid");
    }

    [Theory]
    [InlineData("100.00", "102.00", true)]   // спред 1.98% < 2%
    [InlineData("100.00", "102.10", false)]  // 2.08% > 2%
    public void Price_FundSpreadAbove2Percent_IsSkipped(string bid, string ask, bool priced)
    {
        var quote = Fund("X", decimal.Parse(bid, System.Globalization.CultureInfo.InvariantCulture), decimal.Parse(ask, System.Globalization.CultureInfo.InvariantCulture));

        var result = LinePricer.TryPrice(quote, PlanSides.Buy, out var why);

        (result is not null).Should().Be(priced);
        if (!priced) why.Should().Contain("спред");
    }

    [Fact]
    public void Price_BondSpreadLimitIs3Percent_NotTwo()
    {
        // 98.6–101.4 пунктов: спред 2.8% — для облигации допустимо (3%), для фонда нет (2%).
        LinePricer.TryPrice(Bond("RU1", 98.6m, 101.4m), PlanSides.Buy, out _).Should().NotBeNull();
        LinePricer.TryPrice(Bond("RU1", 98.0m, 101.5m), PlanSides.Buy, out var why).Should().BeNull();
        why.Should().Contain("спред");
    }

    [Fact]
    public void Price_MidFarFromLast_IsSkipped_ButUnknownLastIsFine()
    {
        var quote = Fund("X", 100.00m, 100.02m);

        LinePricer.TryPrice(quote with { Last = 100.5m }, PlanSides.Buy, out _).Should().NotBeNull();
        LinePricer.TryPrice(quote with { Last = 90m }, PlanSides.Buy, out var why).Should().BeNull();
        why.Should().Contain("последней сделки");
        LinePricer.TryPrice(quote with { Last = null }, PlanSides.Buy, out _).Should().NotBeNull();
    }

    [Fact]
    public void Price_ThresholdsComeFromOptions()
    {
        var quote = Fund("X", 100.00m, 103m); // спред 2.96%
        LinePricer.TryPrice(quote, PlanSides.Buy, out _).Should().BeNull();
        LinePricer.TryPrice(quote, PlanSides.Buy, out _, new PolicyOptions { MaxSpreadFraction = 0.05m }).Should().NotBeNull();
    }

    [Fact]
    public void P1_FundWithWideSpread_IsSkippedWithReason()
    {
        var wide = Fund("TMON@", 160m, 170m); // 6%
        var basket = Basket("liquidity", 0.15m, autoBuy: true, members: [Member("TMON@", 1m)]);

        var outcome = SweepRule.Evaluate(Input([basket], [], 10_000m, [wide]));

        outcome.Plan.Should().BeNull();
        outcome.Notes.Should().Contain(n => n.Contains("спред"));
    }

    // ─── 9. P1 не льёт в переполненную корзину ───────────────────────────────────────────

    [Theory]
    [InlineData(299_844, false)] // вес ≈ 0.30 при цели 0.15 → HardAbove
    [InlineData(150_000, true)]  // внутри коридора
    public void P1_DoesNotParkCashIntoLiquidityBasketAboveTarget(int liquidityValue, bool plan)
    {
        var baskets = new[] { Liquidity(), Basket("bonds", 0.85m, tier: 2, fill: BasketFillRules.Engine, members: [ClassMember("bond")]) };
        var positions = new[] { Pos("TMON@", "fund", liquidityValue), Pos("RU000A", "bond", 1_000_000m - liquidityValue) };
        var input = Input(baskets, positions, 7_340m, [Tmon, Tsav]);

        var outcome = SweepRule.Evaluate(input);

        (outcome.Plan is not null).Should().Be(plan);
        if (!plan) outcome.Notes.Should().Contain(n => n.Contains("выше цели") && n.Contains("свободным"));
    }

    // ─── 11. Лимит эмитента — и внутри одного плана ──────────────────────────────────────

    [Fact]
    public void P3b_IssuerLimit_CountsEarlierCandidatesOfTheSameIssuerWithinThePlan()
    {
        // База 700 156 ₽ облигаций, лимит 25% = 175 039. Эмитент уже держится на 174 000: остаток 1 039 — на ОДИН лот (~1 010), но не на два.
        // Раньше каждый кандидат проверялся отдельно и оба проходили.
        var a = Candidate("RU000A1", 0.12m, 0.13m, sector: "Корпоративные") with { IssuerKey = "id:1", IssuerHeldValueRub = 174_000m };
        var b = Candidate("RU000B1", 0.12m, 0.13m, sector: "Банки") with { IssuerKey = "id:1", IssuerHeldValueRub = 174_000m };

        var plan = LiquidityToBondsRule.Evaluate(P3bInput([a, b])).Plan!;

        plan.Lines.Count(l => l.Side == PlanSides.Buy).Should().Be(1, "второй бумаге того же эмитента места в лимите нет");
        plan.Summary.Skipped.Should().Contain(s => s.Reason.Contains("лимит эмитента"));
    }
}

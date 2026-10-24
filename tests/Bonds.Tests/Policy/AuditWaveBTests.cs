using Bonds.Core.Capital;
using Bonds.Core.Policy;
using FluentAssertions;
using Xunit;
using static Bonds.Tests.Policy.PolicyTestKit;

namespace Bonds.Tests.Policy;

/// <summary>
/// Задача 67: решения владельца по волне A (A2) и волна B (корректность) — чистые функции правил. Деньги — рубли (валюта портфеля), доли — ДОЛИ.
/// </summary>
public class AuditWaveBTests
{
    private static readonly PolicyQuote Tmon = Fund("TMON@", 166.57m, 166.59m);
    private static readonly PolicyQuote Tsav = Fund("TSAV", 999.99m, 1000.01m);

    private static CapitalBasket Liquidity(params CapitalBasketMember[] members) =>
        Basket("liquidity", 0.15m, tier: 0, fill: BasketFillRules.Fixed, autoBuy: true, autoSell: true,
            members: members.Length == 0 ? [Member("TMON@", 0.5m), Member("TSAV", 0.5m)] : members);

    // ─── A2 (a): вывод целиком из свободных денег — план «только удержание» ──────────────────

    [Fact]
    public void T67A2_P5_FullyCoveredByFreeCash_HoldOnlyPlan_NoLines_Pending()
    {
        var input = Input([Liquidity()], [], 40_000m, [Tmon, Tsav]);

        var outcome = WithdrawRule.Evaluate(input, 30_000m, null, null);

        outcome.Plan.Should().NotBeNull("свободные деньги под вывод всё равно удерживаются");
        var plan = outcome.Plan!;
        plan.Rule.Should().Be(PlanRules.P5Withdraw);
        plan.Lines.Should().BeEmpty();
        plan.Status.Should().Be(PlanStatuses.PendingApproval);
        plan.Summary.CashHold.Should().Be(30_000m);
        plan.ExpiresAtUtc.Should().BeAfter(input.NowUtc);
    }

    // ─── #8: облигация с номиналом не в рублях в продажах ─────────────────────────────────────

    /// <summary>CNY-облигация: стакан в пунктах, номинал не в рублях (NominalRub = null), ориентир чистой цены 1 250 ₽ (= 100 CNY при Fx 12.5), НКД 1 CNY.</summary>
    private static PolicyQuote CnyBond() => Bond("RU000CNY", 99.9m, 100.1m, accrued: 1m) with
    {
        Currency = "CNY", NominalRub = null, ReferenceCleanPricePerPiece = 1_250m, FxToPortfolio = 12.5m,
    };

    [Fact]
    public void T67_NonRubNominalBond_Sell_PricedInPriceCurrency_PieceCountCorrect()
    {
        // 100 бумаг × (100 + 1) CNY × 12.5 = 126 250 ₽. Вывод 25 250 ₽ (без комиссии и налога: цена входа = текущей) → ровно 20 бумаг. До исправления
        // ориентир в рублях считался ценой в CNY и умножался на курс ещё раз: «бумага» стоила 15 637.5 ₽ → продавалось 2 бумаги.
        var bonds = Basket("bonds", 0.85m, tier: 1, fill: BasketFillRules.Fixed, members: [Member("RU000CNY")]);
        var holding = Holding("RU000CNY", 100m, 126_250m, 101m, "bonds");
        var input = Input([Liquidity(), bonds], [Pos("RU000CNY", "bond", 126_250m)], 0m, [Tmon, Tsav, CnyBond()], [holding]);

        var plan = WithdrawRule.Evaluate(input, 25_250m, null, null).Plan!;

        var line = plan.Lines.Single();
        line.Currency.Should().Be("CNY");
        (line.QuantityLots * line.LotSize).Should().Be(20m);
        line.LimitPrice.Should().Be(100m, "чистая цена за штуку в валюте цены: 1 250 ₽ ÷ 12.5");
        line.EstAmount.Should().Be(2_020m, "20 × 101 CNY");
        plan.Summary.SellTotal.Should().Be(25_250m, "в валюте портфеля — один раз × 12.5");

        var exit = ExitBasketRule.Evaluate(input, "bonds").Plan!;
        exit.Summary.SellTotal.Should().Be(126_250m);
    }

    // ─── #12: лимиты авто — рубли (× курс) и с комиссией ──────────────────────────────────────

    [Fact]
    public void T67_AutoPerOrderLimit_IncludesCommission()
    {
        // LQDT по 100 ₽, комиссия 0.3%: 250 лотов = 25 000 ₽ + 75 ₽ комиссии = 25 075 ₽ > лимита заявки 25 000 ₽ → PendingApproval. До исправления
        // сравнивалась сумма без комиссии (25 000 ≤ 25 000 → AutoApproved).
        var lqdt = Fund("LQDT", 99.99m, 100.01m);
        var input = Input([Liquidity(Member("LQDT"))], [], 25_100m, [lqdt], feeRate: 0.003m);

        var plan = SweepRule.Evaluate(input).Plan!;

        plan.Lines.Single().EstAmount.Should().Be(25_000m);
        plan.Lines.Single().EstFee.Should().Be(75m);
        plan.Status.Should().Be(PlanStatuses.PendingApproval);

        SweepRule.Evaluate(Input([Liquidity(Member("LQDT"))], [], 20_000m, [lqdt], feeRate: 0.003m)).Plan!.Status
            .Should().Be(PlanStatuses.AutoApproved, "контроль: заявка с комиссией в лимите");
    }

    [Fact]
    public void T67_AutoLimits_CompareRubles_ForLineInForeignPriceCurrency()
    {
        // Фонд в юанях (Fx 12.5): 2 400 CNY = 30 000 ₽ > 25 000 ₽ → PendingApproval. До исправления: «2 400 ≤ 25 000» → AutoApproved.
        var cny = Fund("CNYF", 9.99m, 10.01m) with { Currency = "CNY", FxToPortfolio = 12.5m };
        var input = Input([Liquidity(Member("CNYF"))], [], 30_000m, [cny]);

        var plan = SweepRule.Evaluate(input).Plan!;

        plan.Lines.Single().EstAmount.Should().Be(2_400m);
        plan.Status.Should().Be(PlanStatuses.PendingApproval);
    }

    [Fact]
    public void T67_AutoPerDayLimit_IncludesCommission()
    {
        // Сегодня уже 30 000 ₽ авто; новый план 19 900 ₽ + 59.70 ₽ комиссии = 19 959.70 ₽ → 49 959.70 ≤ 50 000 — авто; на 19 950 ₽ + 59.85 → 50 009.85 > 50 000 — нет.
        var lqdt = Fund("LQDT", 99.99m, 100.01m);
        SweepRule.Evaluate(Input([Liquidity(Member("LQDT"))], [], 19_960m, [lqdt], feeRate: 0.003m, autoToday: 30_000m)).Plan!.Status
            .Should().Be(PlanStatuses.AutoApproved);
        var over = Fund("LQDT", 99.99m, 100.01m, step: 0.01m) with { Bid = 49.99m, Ask = 50.01m };
        SweepRule.Evaluate(Input([Liquidity(Member("LQDT"))], [], 20_010m, [over], feeRate: 0.003m, autoToday: 30_000m)).Plan!.Status
            .Should().Be(PlanStatuses.PendingApproval);
    }

    // ─── NIT: mid меньше шага — без бесконечного цикла ─────────────────────────────────────────

    [Fact(Timeout = 5000)]
    public async Task T67_MidBelowStep_NoInfiniteLoop_LineSkippedWithReason()
    {
        // Фонд с ценой 0.004 ₽ при шаге 0.01: покупка округляется вниз до 0 — раньше «бесплатный» лот зацикливал добор лотов в BuyMembers.
        var penny = Fund("PENNY", 0.00400m, 0.00401m, step: 0.01m);
        var input = Input([Liquidity(Member("PENNY"))], [], 10_000m, [penny]);

        var outcome = await Task.Run(() => SweepRule.Evaluate(input));

        outcome.Plan.Should().BeNull();
        outcome.Notes.Should().Contain(n => n.Contains(LinePricer.PriceBelowStepReason));
    }

    // ─── NIT: P3b «рейтинг не ниже A» ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(null, true)]
    public void T67_P3b_RequiresIssuerRatingAtLeastA(bool? ratingA, bool planned)
    {
        // Дизайн §4 + решение владельца (T-67D): свежий рейтинг (≤ 18 мес.) должен быть не ниже A; рейтинга нет — кандидат допускается (прочие фильтры
        // остаются), а в причине строки — «рейтинг не указан».
        var bonds = Basket("bonds", 0.85m, tier: 2, fill: BasketFillRules.Engine, members: [ClassMember("bond")]);
        var positions = new[] { Pos("TMON@", "fund", 299_844m), Pos("RU000A", "bond", 700_156m) };
        var input = Input([Liquidity(), bonds], positions, 0m, [Tmon, Tsav], holdings: [Holding("TMON@", 1_800m, 299_844m, 150m, "liquidity")],
            candidates: [Candidate("RU000A1", 0.14m, 0.15m) with { IssuerRatingAtLeastA = ratingA }], fundYield: 0.10m);

        var outcome = LiquidityToBondsRule.Evaluate(input);

        if (planned)
        {
            var line = outcome.Plan!.Lines.Should().Contain(l => l.Ticker == "RU000A1").Subject;
            if (ratingA is null) line.Reason.Should().Contain("рейтинг не указан");
            else line.Reason.Should().NotContain("рейтинг не указан");
        }
        else
        {
            outcome.Plan.Should().BeNull();
            outcome.Notes.Should().Contain(n => n.Contains("замены нет"));
        }
    }

    [Fact]
    public void T67_RatingAtLeastA_WorstFreshRating_StaleIgnored()
    {
        var today = new DateOnly(2026, 10, 24);
        static Bonds.Core.Models.IssuerRating R(string agency, string rating, DateOnly date) => new() { Inn = "7700000000", Agency = agency, Rating = rating, RatingDate = date };

        Bonds.Infrastructure.Policy.PolicyBondCandidateProvider.RatingAtLeastA([R("АКРА", "A(RU)", today.AddMonths(-2))], today).Should().BeTrue();
        Bonds.Infrastructure.Policy.PolicyBondCandidateProvider.RatingAtLeastA(
            [R("АКРА", "AA(RU)", today.AddMonths(-1)), R("Эксперт", "ruBBB+", today.AddMonths(-3))], today).Should().BeFalse("берётся самый низкий свежий");
        Bonds.Infrastructure.Policy.PolicyBondCandidateProvider.RatingAtLeastA([R("АКРА", "AAA(RU)", today.AddMonths(-20))], today).Should().BeNull("рейтинг протух");
        Bonds.Infrastructure.Policy.PolicyBondCandidateProvider.RatingAtLeastA([], today).Should().BeNull();
    }
}

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
}

using Bonds.Core.Capital;
using Bonds.Core.Policy;
using FluentAssertions;
using Xunit;
using static Bonds.Tests.Policy.PolicyTestKit;

namespace Bonds.Tests.Policy;

/// <summary>
/// Задача 67, волна A: белый список авто-исполнения в коде (#1), удержание денег под вывод (#3), неполные/устаревшие данные останавливают правила (#7).
/// Чистые функции правил; деньги — рубли, доли — ДОЛИ.
/// </summary>
public class AuditWaveATests
{
    private static readonly PolicyQuote Tmon = Fund("TMON@", 166.57m, 166.59m);
    private static readonly PolicyQuote Tsav = Fund("TSAV", 999.99m, 1000.01m);

    private static CapitalBasket Liquidity(params CapitalBasketMember[] members) =>
        Basket("liquidity", 0.15m, tier: 0, fill: BasketFillRules.Fixed, autoBuy: true, autoSell: true,
            members: members.Length == 0 ? [Member("TMON@", 0.5m), Member("TSAV", 0.5m)] : members);

    // ─── #1 белый список авто ──────────────────────────────────────────────────────────────

    [Fact]
    public void T67_P2_RuEquityWithAutoBuy_IsPending()
    {
        // Корзина не «liquidity» с включённым auto_buy (как будто владелец включил флаг в БД): авто всё равно запрещено кодом.
        var equityFund = Fund("TMOS", 6.99m, 7.01m);
        var ruEquity = Basket("ru_equity", 1m, tier: 1, fill: BasketFillRules.Fixed, autoBuy: true, members: [Member("TMOS", 1m)]);

        var plan = InflowRule.Evaluate(Input([ruEquity], [], 10_000m, [equityFund]), 10_000m, "Пополнение").Plan!;

        plan.Lines.Should().NotBeEmpty();
        plan.Status.Should().Be(PlanStatuses.PendingApproval);
    }

    [Fact]
    public void T67_P1_StockInLiquidity_IsPending()
    {
        var sber = Fund("SBER", 299.9m, 300.1m) with { Kind = PolicyAssetKind.Stock };
        var plan = SweepRule.Evaluate(Input([Liquidity(Member("SBER", 1m))], [], 3_000m, [sber])).Plan!;

        plan.Lines.Should().ContainSingle(l => l.Ticker == "SBER");
        plan.Status.Should().Be(PlanStatuses.PendingApproval);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(null)]
    public void T67_P1_FundWithDistributionsOrUnknown_IsPending(bool? pays)
    {
        var tmon = Fund("TMON@", 166.57m, 166.59m, pays: pays);
        var plan = SweepRule.Evaluate(Input([Liquidity(Member("TMON@", 1m))], [], 3_000m, [tmon])).Plan!;
        plan.Status.Should().Be(PlanStatuses.PendingApproval);
    }

    [Fact]
    public void T67_P1_ConfigWhitelistAllowsFundWithUnknownDistributions()
    {
        var tmon = Fund("TMON@", 166.57m, 166.59m, pays: null);
        var input = Input([Liquidity(Member("TMON@", 1m))], [], 3_000m, [tmon]) with { Options = new PolicyOptions { AutoFundWhitelist = ["tmon"] } };
        SweepRule.Evaluate(input).Plan!.Status.Should().Be(PlanStatuses.AutoApproved);
    }

    [Fact]
    public void T67_P1_LiquidityFundsWithoutDistributions_StillAutoApproved()
    {
        SweepRule.Evaluate(Input([Liquidity()], [], 7_340m, [Tmon, Tsav])).Plan!.Status.Should().Be(PlanStatuses.AutoApproved);
    }

    // ─── #3 удержание денег под вывод ──────────────────────────────────────────────────────

    [Fact]
    public void T67_P1_CashHoldActive_ParksNothing()
    {
        var input = Input([Liquidity()], [], 40_000m, [Tmon, Tsav]) with { CashHoldActive = true };

        var outcome = SweepRule.Evaluate(input);

        outcome.Plan.Should().BeNull();
        outcome.Notes.Should().Contain(n => n.Contains("вывод"));
    }

    [Fact]
    public void T67_P5_SummaryCashHold_IsAmountToWithdraw()
    {
        // Свободно 40 000, вывод 100 000: 40 000 из денег, остальное — продажа TMON@. Удержание = сумма к выводу (100 000), а не только свободный остаток.
        var holding = Holding("TMON@", 1_000m, 166_580m, 150m, "liquidity");
        var input = Input([Liquidity()], [Pos("TMON@", "fund", 166_580m)], 40_000m, [Tmon, Tsav], holdings: [holding]);

        var plan = WithdrawRule.Evaluate(input, 100_000m, null, null).Plan!;

        plan.Summary.Shortfall.Should().BeNull();
        plan.Summary.CashHold.Should().Be(100_000m);
    }

    [Fact]
    public void T67_ExitBasket_SummaryCashHold_IsNetProceeds()
    {
        var temp = Basket("temporary", null, fill: BasketFillRules.Hold, members: [Member("TPAY")]);
        var tpay = Fund("TPAY", 99.9m, 100.1m);
        var input = Input([temp], [Pos("TPAY", "fund", 1_000m)], 0m, [tpay], holdings: [Holding("TPAY", 10m, 1_000m, 100m, "temporary")]);

        var plan = ExitBasketRule.Evaluate(input, "temporary").Plan!;

        plan.Summary.CashHold.Should().Be(plan.Summary.SellTotal - plan.Summary.FeeTotal - plan.Summary.TaxTotal).And.BeGreaterThan(0m);
    }

    [Fact]
    public void T67_WithdrawHold_OpenPlan_Done_WithinThreeBusinessDays_Released()
    {
        // Пятница 15:00 МСК: исполненный P5 держит деньги до среды 15:00 МСК (3 рабочих дня, выходные не считаются).
        var friday = OpenNowUtc().AddDays(2).AddHours(3); // среда 09:00 UTC + 2 дня = пятница 12:00 UTC = 15:00 МСК
        var plan = new TradePlan
        {
            Rule = PlanRules.P5Withdraw, Status = PlanStatuses.Done, ExpiresAt = friday, SummaryJson = "{\"currency\":\"RUB\",\"cashHold\":100000}",
        };

        WithdrawHold.IsActive(plan, friday.AddDays(5).AddHours(-1), 3).Should().BeTrue("среда 14:00 МСК — ещё в пределах 3 рабочих дней");
        WithdrawHold.IsActive(plan, friday.AddDays(5).AddHours(1), 3).Should().BeFalse("среда 16:00 МСК — удержание истекло");

        plan.Status = PlanStatuses.PendingApproval;
        plan.ExpiresAt = friday.AddMinutes(30);
        WithdrawHold.IsActive(plan, friday, 3).Should().BeTrue("открытый план вывода держит деньги");
        WithdrawHold.IsActive(plan, friday.AddHours(1), 3).Should().BeFalse("просроченный неисполненный план — нет");

        plan.Status = PlanStatuses.Done;
        plan.CashHoldReleasedAt = friday.AddHours(1);
        WithdrawHold.IsActive(plan, friday.AddHours(2), 3).Should().BeFalse("снято командой /withdrawn или кнопкой «Деньги выведены»");

        plan.CashHoldReleasedAt = null;
        plan.Status = PlanStatuses.Failed;
        WithdrawHold.IsActive(plan, friday.AddHours(2), 3).Should().BeFalse("ничего не продано — держать нечего");
    }

    // ─── #7 неполные или устаревшие данные ─────────────────────────────────────────────────

    private static CapitalBasket Bonds() => Basket("bonds", 0.85m, tier: 2, fill: BasketFillRules.Engine, members: [ClassMember("bond")]);

    private static PolicyInput Incomplete(PolicyInput input, params string[] baskets) =>
        input with { DataIncomplete = true, IncompleteBaskets = new HashSet<string>(baskets, StringComparer.Ordinal) };

    private static PolicyInput Stale(PolicyInput input) => input with { StaleSince = input.NowUtc.AddHours(-30) };

    private static PolicyInput P3bInput()
    {
        var positions = new[] { Pos("TMON@", "fund", 299_844m), Pos("RU000A", "bond", 700_156m) };
        return Input([Liquidity(), Bonds()], positions, 0m, [Tmon, Tsav], holdings: [Holding("TMON@", 1_800m, 299_844m, 150m, "liquidity")],
            candidates: [Candidate("RU000A1", 0.14m, 0.15m)], fundYield: 0.10m);
    }

    private static PolicyInput P4Input() => Input([Liquidity(), Bonds()], [Pos("RU000A", "bond", 800_000m)], 200_000m, [Tmon, Tsav]);

    [Fact]
    public void T67_P3b_And_P4_NotBuilt_WhenDataIncompleteOrStale()
    {
        LiquidityToBondsRule.Evaluate(P3bInput()).Plan.Should().NotBeNull("контроль: на полных данных план есть");
        RebalanceRule.Evaluate(P4Input()).Plan.Should().NotBeNull("контроль: на полных данных план есть");

        foreach (var input in new[] { Incomplete(P3bInput(), "bonds"), Stale(P3bInput()) })
        {
            var outcome = LiquidityToBondsRule.Evaluate(input);
            outcome.Plan.Should().BeNull();
            outcome.Notes.Should().Contain(n => n.Contains("P3b") && (n.Contains("неполн") || n.Contains("устарел")));
        }

        foreach (var input in new[] { Incomplete(P4Input(), "bonds"), Stale(P4Input()) })
        {
            var outcome = RebalanceRule.Evaluate(input);
            outcome.Plan.Should().BeNull();
            outcome.Notes.Should().Contain(n => n.Contains("P4") && (n.Contains("неполн") || n.Contains("устарел")));
        }
    }

    [Fact]
    public void T67_P2_DeficitSplit_NotBuilt_WhenDataIncompleteOrStale()
    {
        var input = Input([Liquidity()], [], 20_000m, [Tmon, Tsav]);
        InflowRule.Evaluate(input, 20_000m, "Пополнение").Plan.Should().NotBeNull("контроль");

        InflowRule.Evaluate(Incomplete(input, "bonds"), 20_000m, "Пополнение").Notes.Should().Contain(n => n.Contains("неполн"));
        InflowRule.Evaluate(Incomplete(input, "bonds"), 20_000m, "Пополнение").Plan.Should().BeNull();
        InflowRule.Evaluate(Stale(input), 20_000m, "Пополнение").Plan.Should().BeNull();
    }

    [Fact]
    public void T67_P1_StopsOnStaleOrLiquidityIncomplete_ButNotOnOtherBasketIncomplete()
    {
        var input = Input([Liquidity(), Bonds()], [Pos("RU000A", "bond", 800_000m)], 7_340m, [Tmon, Tsav]);
        SweepRule.Evaluate(input).Plan.Should().NotBeNull("контроль");

        SweepRule.Evaluate(Stale(input)).Plan.Should().BeNull();
        SweepRule.Evaluate(Incomplete(input, "liquidity")).Plan.Should().BeNull();
        SweepRule.Evaluate(Incomplete(input, "cash")).Plan.Should().BeNull("сумма свободных денег не определена (нет курса валюты)");
        SweepRule.Evaluate(Incomplete(input, "liquidity")).Notes.Should().Contain(n => n.Contains("P1") && n.Contains("неполн"));
        SweepRule.Evaluate(Incomplete(input, "bonds")).Plan.Should().NotBeNull("неполнота другой корзины не мешает парковке");
    }
}

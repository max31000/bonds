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
}

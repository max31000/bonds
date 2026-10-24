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
}

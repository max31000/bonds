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
}

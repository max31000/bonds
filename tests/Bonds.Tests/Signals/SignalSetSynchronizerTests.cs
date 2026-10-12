using Bonds.Core.Models;
using Bonds.Core.Signals;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Signals;

/// <summary>Синхронизация «набор движка» ↔ «строки БД» (plan/40 A.1): новое/существующее/исчезнувшее, ack не воскрешается.</summary>
public class SignalSetSynchronizerTests
{
    private static Signal Event(string key, SignalSeverity severity = SignalSeverity.Info, ulong id = 0) => new()
    {
        Id = id,
        AccountId = 1,
        EventKey = key,
        Type = SignalType.UpcomingRedemption,
        Severity = severity,
        Subject = "Бумага",
    };

    private static Signal Stored(string key, ulong id, DateTime? resolvedAt = null, DateTime? ackAt = null) =>
        new()
        {
            Id = id, AccountId = 1, EventKey = key, Type = SignalType.UpcomingRedemption,
            ResolvedAt = resolvedAt, AcknowledgedAt = ackAt,
        };

    [Fact]
    public void NewKey_IsInserted()
    {
        var plan = SignalSetSynchronizer.Plan([Event("a")], []);

        plan.ToInsert.Should().ContainSingle(s => s.EventKey == "a");
        plan.ToRefresh.Should().BeEmpty();
        plan.ToResolve.Should().BeEmpty();
    }

    [Fact]
    public void ExistingKey_IsRefreshed_NotInserted()
    {
        var plan = SignalSetSynchronizer.Plan([Event("a", SignalSeverity.Warning)], [Stored("a", 7)]);

        plan.ToInsert.Should().BeEmpty();
        var (existing, fresh) = plan.ToRefresh.Should().ContainSingle().Subject;
        existing.Id.Should().Be(7ul);
        fresh.Severity.Should().Be(SignalSeverity.Warning);
        plan.ToResolve.Should().BeEmpty();
    }

    [Fact]
    public void DisappearedKey_IsResolved_OnlyIfStillActive()
    {
        var already = Stored("old", 2, resolvedAt: DateTime.UtcNow.AddDays(-3));
        var active = Stored("gone", 3);

        var plan = SignalSetSynchronizer.Plan([], [already, active]);

        plan.ToResolve.Should().ContainSingle().Which.Id.Should().Be(3ul);
        plan.ToInsert.Should().BeEmpty();
    }

    [Fact]
    public void RepeatedRun_OnSameSet_InsertsNothing_NoDuplicates()
    {
        var produced = new[] { Event("a"), Event("b") };
        var stored = new[] { Stored("a", 1), Stored("b", 2) };

        var plan = SignalSetSynchronizer.Plan(produced, stored);

        plan.ToInsert.Should().BeEmpty();
        plan.ToRefresh.Should().HaveCount(2);
        plan.ToResolve.Should().BeEmpty();
    }

    [Fact]
    public void Acknowledged_IsNeverResurrected_PlanDoesNotTouchAckOrCreateSecondRow()
    {
        var acked = Stored("a", 5, ackAt: DateTime.UtcNow.AddHours(-1));

        var plan = SignalSetSynchronizer.Plan([Event("a")], [acked]);

        // Ключ существует (даже подтверждённый) -> только refresh той же строки, а не новая вставка.
        plan.ToInsert.Should().BeEmpty();
        plan.ToRefresh.Should().ContainSingle().Which.Existing.Should().BeSameAs(acked);
        acked.IsAcknowledged.Should().BeTrue();
    }

    [Fact]
    public void ResolvedKeyThatReappears_IsRefreshed_SameRow_AckKept()
    {
        var resolvedAcked = Stored("a", 9, resolvedAt: DateTime.UtcNow.AddDays(-1), ackAt: DateTime.UtcNow.AddDays(-2));

        var plan = SignalSetSynchronizer.Plan([Event("a")], [resolvedAcked]);

        plan.ToInsert.Should().BeEmpty();
        plan.ToRefresh.Should().ContainSingle().Which.Existing.Id.Should().Be(9ul);
    }
}

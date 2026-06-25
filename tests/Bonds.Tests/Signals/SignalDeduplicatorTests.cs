using Bonds.Core.Models;
using Bonds.Core.Signals;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Signals;

/// <summary>Тесты чистой дедупликации (plan/07 Часть A "Дедупликация") изолированно от движка правил.</summary>
public class SignalDeduplicatorTests
{
    private static Signal MakeSignal(SignalType type = SignalType.UpcomingCoupon, ulong? positionId = 1, ulong? instrumentId = 1, DateOnly? date = null) => new()
    {
        Type = type,
        PositionId = positionId,
        InstrumentId = instrumentId,
        Date = date ?? new DateOnly(2026, 7, 1),
    };

    [Fact]
    public void FilterNew_NoExisting_ReturnsAllCandidates()
    {
        var candidates = new List<Signal> { MakeSignal() };

        var result = SignalDeduplicator.FilterNew(candidates, []);

        result.Should().BeEquivalentTo(candidates);
    }

    [Fact]
    public void FilterNew_ExactKeyMatch_FiltersOutCandidate()
    {
        var candidate = MakeSignal();
        var existing = MakeSignal();

        var result = SignalDeduplicator.FilterNew([candidate], [existing]);

        result.Should().BeEmpty();
    }

    [Fact]
    public void FilterNew_DifferentDate_KeepsCandidate()
    {
        var candidate = MakeSignal(date: new DateOnly(2026, 8, 1));
        var existing = MakeSignal(date: new DateOnly(2026, 7, 1));

        var result = SignalDeduplicator.FilterNew([candidate], [existing]);

        result.Should().ContainSingle();
    }

    [Fact]
    public void FilterNew_DifferentType_KeepsCandidate()
    {
        var candidate = MakeSignal(type: SignalType.UpcomingAmortization);
        var existing = MakeSignal(type: SignalType.UpcomingCoupon);

        var result = SignalDeduplicator.FilterNew([candidate], [existing]);

        result.Should().ContainSingle();
    }

    [Fact]
    public void FilterNew_DifferentPositionId_KeepsCandidate()
    {
        var candidate = MakeSignal(positionId: 2);
        var existing = MakeSignal(positionId: 1);

        var result = SignalDeduplicator.FilterNew([candidate], [existing]);

        result.Should().ContainSingle();
    }

    [Fact]
    public void FilterNew_NullPositionAndInstrument_MatchesOnTypeAndDate()
    {
        // Сигналы без привязки к позиции (напр. UninvestedCashThreshold/DurationDriftFromTarget) —
        // ключ дедупликации работает и при PositionId/InstrumentId == null с обеих сторон.
        var candidate = MakeSignal(type: SignalType.UninvestedCashThreshold, positionId: null, instrumentId: null);
        var existing = MakeSignal(type: SignalType.UninvestedCashThreshold, positionId: null, instrumentId: null);

        var result = SignalDeduplicator.FilterNew([candidate], [existing]);

        result.Should().BeEmpty();
    }
}

using Bonds.Core.Models;

namespace Bonds.Core.Signals;

/// <summary>План синхронизации набора событий с таблицей (результат <see cref="SignalSetSynchronizer.Plan"/>).</summary>
public sealed record SignalSyncPlan
{
    /// <summary>Ключ ещё не встречался — вставить (кандидаты на уведомление, если Critical).</summary>
    public IReadOnlyList<Signal> ToInsert { get; init; } = [];

    /// <summary>Ключ уже есть — освежить текст/severity/last_seen_at (и вернуть в активные, если был закрыт). Ack сохраняется.</summary>
    public IReadOnlyList<(Signal Existing, Signal Fresh)> ToRefresh { get; init; } = [];

    /// <summary>Активные в БД, но движок их больше не производит — закрыть (<c>resolved_at = now</c>).</summary>
    public IReadOnlyList<Signal> ToResolve { get; init; } = [];
}

/// <summary>
/// Чистая синхронизация «полный набор от движка» ↔ «строки в БД» по ключу
/// <see cref="Signal.EventKey"/> (plan/40 A.1). Не знает про I/O — репозиторий исполняет план.
/// Инварианты: ack никогда не сбрасывается (план его не трогает); повторный прогон на том же
/// наборе ничего не вставляет (нет дублей); закрытое событие, ключ которого появился снова, —
/// refresh (возвращается в активные, прежний ack сохраняется, чтобы не «воскрешать» прочитанное).
/// </summary>
public static class SignalSetSynchronizer
{
    /// <param name="produced">Полный набор активных событий от <see cref="SignalsEngine.Evaluate"/>.</param>
    /// <param name="existing">ВСЕ строки счёта (активные и закрытые, ещё не вычищенные).</param>
    public static SignalSyncPlan Plan(IReadOnlyCollection<Signal> produced, IReadOnlyCollection<Signal> existing)
    {
        var existingByKey = existing
            .GroupBy(s => s.EventKey)
            .ToDictionary(g => g.Key, g => g.First());

        var producedKeys = produced.Select(p => p.EventKey).ToHashSet();

        var toInsert = new List<Signal>();
        var toRefresh = new List<(Signal, Signal)>();
        foreach (var fresh in produced)
        {
            if (existingByKey.TryGetValue(fresh.EventKey, out var current)) toRefresh.Add((current, fresh));
            else toInsert.Add(fresh);
        }

        var toResolve = existing
            .Where(e => !e.IsResolved && !producedKeys.Contains(e.EventKey))
            .ToList();

        return new SignalSyncPlan { ToInsert = toInsert, ToRefresh = toRefresh, ToResolve = toResolve };
    }
}

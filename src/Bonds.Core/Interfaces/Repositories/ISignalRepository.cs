using Bonds.Core.Models;

namespace Bonds.Core.Interfaces.Repositories;

public interface ISignalRepository
{
    /// <summary>События счёта: только активные (<c>resolved_at IS NULL</c>) либо, при <paramref name="includeResolved"/>, вместе с закрытыми (история).</summary>
    Task<IReadOnlyList<Signal>> GetByAccountIdAsync(ulong accountId, bool includeResolved = false);

    /// <summary>
    /// Вставка нового события. Идемпотентна по (account_id, event_key): при гонке уже существующая
    /// строка обновляется (текст/severity/last_seen_at, resolved_at → NULL), ack не трогается.
    /// </summary>
    Task<ulong> InsertAsync(Signal signal);

    /// <summary>Обновляет текст/severity/subject и <c>last_seen_at</c>; снимает <c>resolved_at</c> (событие снова активно). Ack не меняется.</summary>
    Task RefreshAsync(ulong id, SignalSeverity severity, string? subject, string? suggestedAction, DateTime seenAtUtc);

    /// <summary>Закрывает события (<c>resolved_at = resolvedAtUtc</c>), которые движок больше не производит.</summary>
    Task ResolveAsync(IReadOnlyCollection<ulong> ids, DateTime resolvedAtUtc);

    /// <summary>Подтверждает событие. false — события нет у этого счёта. Повторное подтверждение не меняет исходную метку.</summary>
    Task<bool> AcknowledgeAsync(ulong id, ulong accountId, DateTime nowUtc);

    /// <summary>Подтверждает все активные неподтверждённые события счёта; возвращает число затронутых.</summary>
    Task<int> AcknowledgeAllAsync(ulong accountId, DateTime nowUtc);

    /// <summary>Удаляет закрытые события, у которых <c>resolved_at &lt; cutoffUtc</c>; возвращает число удалённых.</summary>
    Task<int> PurgeResolvedBeforeAsync(ulong accountId, DateTime cutoffUtc);
}

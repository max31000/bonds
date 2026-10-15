using Bonds.Core.Models;

namespace Bonds.Core.Interfaces.Repositories;

/// <summary>
/// Задача 43: персистентный кэш карточек бумаг MOEX (<c>security_details</c>) — флаги дефолтов и т.п.
/// </summary>
public interface ISecurityDetailsRepository
{
    /// <summary>
    /// Батчевый идемпотентный upsert по SECID (multi-row INSERT ... ON DUPLICATE KEY UPDATE чанками):
    /// повторная запись той же бумаги обновляет строку, не дублирует.
    /// </summary>
    Task UpsertBatchAsync(IReadOnlyList<SecurityDetails> items, CancellationToken ct = default);

    /// <summary>Карточки по списку SECID (отсутствующие просто не возвращаются).</summary>
    Task<IReadOnlyList<SecurityDetails>> GetBySecidsAsync(IReadOnlyCollection<string> secids, CancellationToken ct = default);

    /// <summary>
    /// Очередь фонового обогащения: до <paramref name="limit"/> SECID, которым нужен запрос карточки.
    /// Приоритеты: (1) бумаги портфеля (позиции с ненулевым количеством) и watchlist — если карточки нет
    /// ИЛИ она старше <paramref name="staleBeforeUtc"/>; (2) бумаги банка без записи в
    /// <c>security_details</c>; (3) записи старше <paramref name="staleBeforeUtc"/>. Внутри приоритета —
    /// сначала не получавшиеся/самые старые, затем по SECID (детерминированный порядок).
    /// </summary>
    Task<IReadOnlyList<string>> GetEnrichmentQueueAsync(int limit, DateTime staleBeforeUtc, CancellationToken ct = default);

    /// <summary>
    /// Дефолт-история эмитентов одним SQL (GROUP BY emitent_id, без N+1): для каждого переданного
    /// <c>emitent_id</c>, по которому есть хотя бы одна карточка, — агрегат <see cref="EmitentDefaultHistory"/>.
    /// Эмитенты без единой карточки в результат не попадают.
    /// </summary>
    Task<IReadOnlyDictionary<int, EmitentDefaultHistory>> GetEmitentDefaultHistoryAsync(
        IReadOnlyCollection<int> emitentIds, CancellationToken ct = default);
}

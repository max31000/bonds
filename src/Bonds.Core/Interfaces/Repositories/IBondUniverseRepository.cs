using Bonds.Core.Models;

namespace Bonds.Core.Interfaces.Repositories;

public interface IBondUniverseRepository
{
    /// <summary>
    /// Батчевый upsert всего снимка (задача 26 часть B.3) — одна multi-row INSERT ... ON DUPLICATE
    /// KEY UPDATE команда (не N отдельных INSERT), чтобы полный refresh (~3000 строк) укладывался
    /// в единицы секунд. Бумаги, отсутствующие в новом снимке (погашены/выведены с торгов), НЕ
    /// удаляются — остаются с устаревшим <see cref="BondUniverseEntry.UpdatedAt"/> (простой дефолт:
    /// гигиенический фильтр отсеет их по MissingDurationOrPrice/NearMaturity на следующей выдаче
    /// сравнения с реальными датами; полная перестройка таблицы через TRUNCATE усложнила бы
    /// идемпотентность при частичном сбое середины батча без дополнительной выгоды на MVP).
    /// </summary>
    Task UpsertSnapshotBatchAsync(IReadOnlyList<BondUniverseEntry> entries, CancellationToken ct = default);

    /// <summary>Все строки текущего снимка — используется API-эндпоинтом (фильтрация/сортировка/
    /// пагинация выполняются в вызывающем коде поверх гигиенического фильтра, см. plan/26 часть D)
    /// и refresh-сервисом (перед записью дневного среза).</summary>
    Task<IReadOnlyList<BondUniverseEntry>> GetAllAsync(CancellationToken ct = default);

    /// <summary>Момент последнего upsert снимка (MAX(updated_at)) — null, если снимок ещё пуст.
    /// Используется хостед-сервисом ("снимок пуст или старше 6 часов — обновить сразу при старте")
    /// и статус-эндпоинтом.</summary>
    Task<DateTime?> GetLastRefreshUtcAsync(CancellationToken ct = default);

    Task<int> CountAsync(CancellationToken ct = default);

    /// <summary>True, если дневной срез за эту дату уже записан (идемпотентность — часть C.1:
    /// "одна запись в день").</summary>
    Task<bool> HasHistoryForDateAsync(DateOnly date, CancellationToken ct = default);

    /// <summary>
    /// Записывает дневной срез bond_universe_history за <paramref name="date"/> ИЗ ТЕКУЩЕГО
    /// содержимого bond_universe (INSERT ... SELECT одним запросом — не построчно из C#) и чистит
    /// строки старше <paramref name="retentionDays"/> дней. Идемпотентно — повторный вызов на ту
    /// же дату перезаписывает срез (ON DUPLICATE KEY UPDATE), не дублирует и не бросает исключение.
    /// </summary>
    Task AppendDailyHistorySnapshotAsync(DateOnly date, int retentionDays, CancellationToken ct = default);

    /// <summary>Число дней, за которые есть хотя бы одна строка в bond_universe_history —
    /// для GET /api/universe/status (historyDays).</summary>
    Task<int> GetHistoryDaysCountAsync(CancellationToken ct = default);
}

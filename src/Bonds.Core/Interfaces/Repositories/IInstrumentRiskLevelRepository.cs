using Bonds.Core.Models;

namespace Bonds.Core.Interfaces.Repositories;

/// <summary>
/// Задача 46: суточный кэш брокерской оценки риска облигаций T-Invest (<c>instrument_risk_levels</c>).
/// </summary>
public interface IInstrumentRiskLevelRepository
{
    /// <summary>Идемпотентный батч-upsert по ISIN (повтор обновляет уровень и время, не дублирует).</summary>
    Task UpsertBatchAsync(IReadOnlyList<InstrumentRiskLevel> items, CancellationToken ct = default);

    /// <summary>Все уровни по ISIN (регистр ключа не важен).</summary>
    Task<IReadOnlyDictionary<string, BrokerRiskLevel>> GetAllAsync(CancellationToken ct = default);

    /// <summary>Время последнего обновления кэша, UTC; null — таблица пуста (шаг ещё не выполнялся).</summary>
    Task<DateTime?> GetLastFetchedAtAsync(CancellationToken ct = default);
}

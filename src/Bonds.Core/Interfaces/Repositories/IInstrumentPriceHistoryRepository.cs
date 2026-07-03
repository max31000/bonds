using Bonds.Core.Models;

namespace Bonds.Core.Interfaces.Repositories;

/// <summary>Кэш дневной истории цены инструмента (plan/19 §A). См. doc-comment InstrumentPriceHistory.</summary>
public interface IInstrumentPriceHistoryRepository
{
    /// <summary>Все точки инструмента в окне [from; to] включительно, по возрастанию даты.</summary>
    Task<IReadOnlyList<InstrumentPriceHistory>> GetRangeAsync(ulong instrumentId, DateOnly from, DateOnly to);

    /// <summary>Самая поздняя дата, уже закэшированная для инструмента. Null — для инструмента ещё нет ни одной точки.</summary>
    Task<DateOnly?> GetLatestDateAsync(ulong instrumentId);

    /// <summary>Upsert пачки точек по уникальному ключу (instrument_id, date) — идемпотентная дозагрузка хвоста.</summary>
    Task UpsertManyAsync(ulong instrumentId, IEnumerable<InstrumentPriceHistory> points);
}

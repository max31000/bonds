using Bonds.Core.Models;

namespace Bonds.Core.Interfaces.Repositories;

public interface IPositionRepository
{
    Task<Position?> GetByIdAsync(ulong id, ulong accountId);
    Task<IEnumerable<Position>> GetByAccountIdAsync(ulong accountId);
    Task<Position?> GetByAccountAndInstrumentAsync(ulong accountId, ulong instrumentId);

    /// <summary>Upsert по (AccountId, InstrumentId) — синк позиций перезаписывает количество/cost basis.</summary>
    Task<ulong> UpsertAsync(Position position);

    Task DeleteAsync(ulong id, ulong accountId);

    /// <summary>
    /// Сверка с брокером: удаляет позиции счёта, чьих инструментов НЕТ в <paramref name="keepInstrumentIds"/>
    /// (погашены, проданы целиком, исполнена оферта, выведены). Каскадом уходят их проекции потоков
    /// (<c>projected_cash_flows</c>); сигналы отвязываются (FK SET NULL). Возвращает число удалённых.
    /// </summary>
    Task<int> DeleteAllExceptAsync(ulong accountId, IReadOnlyCollection<ulong> keepInstrumentIds);
}

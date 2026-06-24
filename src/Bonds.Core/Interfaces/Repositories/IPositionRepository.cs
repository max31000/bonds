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
}

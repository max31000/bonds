using Bonds.Core.Models;

namespace Bonds.Core.Interfaces.Repositories;

public interface IPortfolioValueSnapshotRepository
{
    Task<IEnumerable<PortfolioValueSnapshot>> GetByAccountIdAsync(ulong accountId, DateOnly? from = null, DateOnly? to = null);
    Task<PortfolioValueSnapshot?> GetLatestAsync(ulong accountId);

    /// <summary>Upsert по (AccountId, AsOf) — повторный снимок за тот же день перезаписывает, не дублирует.</summary>
    Task UpsertAsync(PortfolioValueSnapshot snapshot);
}

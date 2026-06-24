using Bonds.Core.Models;

namespace Bonds.Core.Interfaces.Repositories;

public interface IYieldCurveRepository
{
    Task<YieldCurveSnapshot?> GetLatestAsync();
    Task<YieldCurveSnapshot?> GetByDateAsync(DateOnly asOf);
    Task<IEnumerable<YieldCurveSnapshot>> GetHistoryAsync(DateOnly from, DateOnly to);

    /// <summary>Upsert по AsOf (unique) — повторная загрузка снимка на ту же дату не дублирует строку.</summary>
    Task UpsertAsync(YieldCurveSnapshot snapshot);
}

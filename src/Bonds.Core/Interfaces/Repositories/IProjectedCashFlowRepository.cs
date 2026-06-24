using Bonds.Core.Models;

namespace Bonds.Core.Interfaces.Repositories;

public interface IProjectedCashFlowRepository
{
    Task<IEnumerable<ProjectedCashFlow>> GetByPositionIdAsync(ulong positionId);
    Task<IEnumerable<ProjectedCashFlow>> GetByAccountIdAsync(ulong accountId, DateOnly? from = null, DateOnly? to = null);

    /// <summary>Полностью заменяет проекцию по позиции (пересчёт движком на этапе 06 идемпотентен по построению).</summary>
    Task ReplaceForPositionAsync(ulong positionId, IEnumerable<ProjectedCashFlow> flows);
}

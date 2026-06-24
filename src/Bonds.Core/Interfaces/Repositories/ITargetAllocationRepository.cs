using Bonds.Core.Models;

namespace Bonds.Core.Interfaces.Repositories;

public interface ITargetAllocationRepository
{
    Task<IEnumerable<TargetAllocation>> GetByAccountIdAsync(ulong accountId);
    Task<ulong> CreateAsync(TargetAllocation allocation);
    Task UpdateAsync(TargetAllocation allocation);
    Task DeleteAsync(ulong id, ulong accountId);
}

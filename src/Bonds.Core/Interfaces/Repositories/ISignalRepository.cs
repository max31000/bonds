using Bonds.Core.Models;

namespace Bonds.Core.Interfaces.Repositories;

public interface ISignalRepository
{
    Task<IEnumerable<Signal>> GetByAccountIdAsync(ulong accountId, bool? isRead = null);
    Task<ulong> CreateAsync(Signal signal);
    Task MarkReadAsync(ulong id, ulong accountId);
}

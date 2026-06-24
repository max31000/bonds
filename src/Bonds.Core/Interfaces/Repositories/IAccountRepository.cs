using Bonds.Core.Models;

namespace Bonds.Core.Interfaces.Repositories;

public interface IAccountRepository
{
    Task<Account?> GetByIdAsync(ulong id, ulong userId);
    Task<IEnumerable<Account>> GetByUserIdAsync(ulong userId);
    Task<ulong> CreateAsync(Account account);
    Task UpdateAsync(Account account);
}

using Bonds.Core.Models;

namespace Bonds.Core.Interfaces.Repositories;

public interface IUserRepository
{
    Task<User?> GetByTelegramIdAsync(long telegramId);
    Task<User?> GetByIdAsync(ulong id);
    Task<ulong> CreateAsync(User user);
    Task UpdateAsync(User user);
}

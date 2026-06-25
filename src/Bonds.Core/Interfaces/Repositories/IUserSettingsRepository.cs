using Bonds.Core.Models;

namespace Bonds.Core.Interfaces.Repositories;

public interface IUserSettingsRepository
{
    Task<UserSettings?> GetByUserIdAsync(ulong userId);

    /// <summary>Upsert по UserId — единственная строка настроек пользователя создаётся при первом PUT, далее обновляется.</summary>
    Task UpsertAsync(UserSettings settings);
}

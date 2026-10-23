using Bonds.Core.Models;

namespace Bonds.Core.Interfaces.Repositories;

public interface IUserSettingsRepository
{
    Task<UserSettings?> GetByUserIdAsync(ulong userId);

    /// <summary>Upsert по UserId — единственная строка настроек пользователя создаётся при первом PUT, далее обновляется.</summary>
    Task UpsertAsync(UserSettings settings);

    /// <summary>Флаг паузы (<c>/stop</c>): нет строки настроек — false.</summary>
    Task<bool> IsExecutionPausedAsync(ulong userId);

    /// <summary>Ставит/снимает паузу; строка настроек создаётся при необходимости (остальные поля не меняются).</summary>
    Task SetExecutionPausedAsync(ulong userId, bool paused);
}

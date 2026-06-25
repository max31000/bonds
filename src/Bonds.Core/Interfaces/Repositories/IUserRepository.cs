using Bonds.Core.Models;

namespace Bonds.Core.Interfaces.Repositories;

public interface IUserRepository
{
    Task<User?> GetByTelegramIdAsync(long telegramId);
    Task<User?> GetByIdAsync(ulong id);
    Task<ulong> CreateAsync(User user);
    Task UpdateAsync(User user);

    /// <summary>
    /// Id единственного владельца продукта (см. doc-comment <see cref="IAccountRepository.GetPrimaryAccountIdAsync"/>
    /// для того же паттерна с Account) — самый старый (по Id) существующий <see cref="User"/>.
    /// Используется не-HTTP кодом (этап 08: <c>ITInvestTokenProvider</c>), которому неоткуда
    /// взять UserId иначе, чем из единственной существующей записи. Null — если ни один
    /// пользователь ещё не залогинился (чистая БД до первого Telegram-логина).
    /// </summary>
    Task<ulong?> GetPrimaryUserIdAsync();
}

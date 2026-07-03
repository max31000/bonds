using Bonds.Core.Models;

namespace Bonds.Core.Interfaces.Repositories;

public interface IWatchlistItemRepository
{
    Task<IEnumerable<WatchlistItem>> GetByUserIdAsync(ulong userId);
    Task<WatchlistItem?> GetByIdAsync(ulong id, ulong userId);
    Task<WatchlistItem?> GetByUserIdAndIsinAsync(ulong userId, string isin);

    /// <summary>Создаёт запись watchlist. Уникальность (UserId, Isin) обеспечена на уровне БД —
    /// вызывающий слой (эндпоинт) должен сам проверить дубликат заранее, если нужен явный 409/400,
    /// а не полагаться на исключение уникального ключа.</summary>
    Task<ulong> CreateAsync(WatchlistItem item);

    Task DeleteAsync(ulong id, ulong userId);

    /// <summary>Все watchlist-записи всех пользователей — вход для шага синка (SyncCycleService),
    /// который не имеет HTTP-контекста/конкретного UserId (тот же паттерн, что
    /// IAccountRepository.GetPrimaryAccountIdAsync для single-user продукта).</summary>
    Task<IEnumerable<WatchlistItem>> GetAllAsync();
}

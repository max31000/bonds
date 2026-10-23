using Bonds.Core.Capital;

namespace Bonds.Core.Interfaces.Repositories;

/// <summary>Корзины целевой структуры капитала (задача 62). Все методы привязаны к <c>userId</c> — чужие корзины недоступны.</summary>
public interface ICapitalBasketRepository
{
    /// <summary>Корзины пользователя с членами (по портфелю и <c>sort_order</c>); <paramref name="portfolio"/> = null — оба портфеля.</summary>
    Task<IReadOnlyList<CapitalBasket>> GetByUserAsync(ulong userId, string? portfolio = null);

    /// <summary>
    /// Сидит стартовые корзины, если у пользователя их ещё нет (гонка двух первых запросов безопасна: проигравший ловит дубль ключа и ничего не делает).
    /// Возвращает true, если посев выполнил именно этот вызов.
    /// </summary>
    Task<bool> SeedIfEmptyAsync(ulong userId, IReadOnlyList<CapitalBasket> defaults);

    /// <summary>Заменяет корзины портфеля стартовыми (удаляет прежние вместе с членами) — «сброс к умолчаниям».</summary>
    Task ReplacePortfolioAsync(ulong userId, string portfolio, IReadOnlyList<CapitalBasket> defaults);

    /// <summary>
    /// Обновляет редактируемые поля (title, target, band, hard_band, tier, fill_rule, auto_buy/sell, min_trade) нескольких корзин ОДНОЙ транзакцией.
    /// code/portfolio/sort_order не меняются. Чужие id игнорируются.
    /// </summary>
    Task UpdateManyAsync(ulong userId, IReadOnlyList<CapitalBasket> baskets);

    /// <summary>Добавляет члена; возвращает его id. Корзина должна принадлежать пользователю, иначе — 0.</summary>
    Task<ulong> AddMemberAsync(ulong userId, ulong basketId, CapitalBasketMember member);

    /// <summary>Удаляет члена корзины пользователя; false — такого члена у него нет.</summary>
    Task<bool> RemoveMemberAsync(ulong userId, ulong basketId, ulong memberId);
}

namespace Bonds.Core.Interfaces.Repositories;

/// <summary>Служебное хранилище ключ-значение Telegram-бота (<c>bot_state</c>, миграция 034): offset getUpdates и т.п.</summary>
public interface IBotStateRepository
{
    Task<string?> GetAsync(string key);
    Task SetAsync(string key, string value);

    /// <summary>
    /// Задача 67 (#16): атомарно поднимает числовую «отметку» ключа до <paramref name="value"/>, только если сохранённое значение меньше (нет — 0).
    /// true — поднято (событие новое), false — уже было такое или более позднее (повтор).
    /// </summary>
    Task<bool> TryAdvanceAsync(string key, long value);
}

namespace Bonds.Core.Interfaces.Repositories;

/// <summary>Служебное хранилище ключ-значение Telegram-бота (<c>bot_state</c>, миграция 034): offset getUpdates и т.п.</summary>
public interface IBotStateRepository
{
    Task<string?> GetAsync(string key);
    Task SetAsync(string key, string value);
}

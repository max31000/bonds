namespace Bonds.Core.Models;

/// <summary>
/// Единственный пользователь сервиса — владелец (см. spec §2: single-user, не мультитенантный продукт).
/// Таблица допускает несколько строк физически, но allowlist (Telegram:OwnerId) гарантирует,
/// что реально создаётся и используется только запись владельца.
/// </summary>
public class User
{
    public ulong Id { get; set; }
    public long TelegramId { get; set; }
    public string? Username { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }

    /// <summary>Базовая валюта пользователя. MVP — портфель рублёвый (spec §11).</summary>
    public string BaseCurrency { get; set; } = "RUB";

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

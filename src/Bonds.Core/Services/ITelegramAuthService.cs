namespace Bonds.Core.Services;

/// <summary>
/// Данные, которые Telegram Login Widget передаёт после авторизации пользователя.
/// </summary>
public record TelegramAuthData
{
    public long Id { get; init; }
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public string? Username { get; init; }
    public string? PhotoUrl { get; init; }
    public long AuthDate { get; init; }
    public string Hash { get; init; } = string.Empty;
}

public interface ITelegramAuthService
{
    /// <summary>
    /// Проверяет HMAC-подпись данных из Telegram Login Widget.
    /// Возвращает true, если данные подлинные и не старше 24 часов.
    /// </summary>
    bool ValidateAuthData(TelegramAuthData data);
}

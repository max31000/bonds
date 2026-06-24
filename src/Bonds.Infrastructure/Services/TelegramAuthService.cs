using System.Security.Cryptography;
using System.Text;
using Bonds.Core.Services;
using Microsoft.Extensions.Configuration;

namespace Bonds.Infrastructure.Services;

/// <summary>
/// Проверяет подпись данных Telegram Login Widget.
///
/// Алгоритм верификации (официальная документация Telegram):
/// 1. Из полученных данных убираем поле hash.
/// 2. Оставшиеся поля сортируем по алфавиту и объединяем через \n.
/// 3. secret_key = SHA256(bot_token).
/// 4. Проверяем: HMAC-SHA256(data_check_string, secret_key) == hash.
/// 5. Проверяем, что auth_date не старше 24 часов.
/// </summary>
public class TelegramAuthService : ITelegramAuthService
{
    private readonly byte[] _secretKey;

    public TelegramAuthService(IConfiguration configuration)
    {
        var botToken = configuration["Telegram:BotToken"]
            ?? throw new InvalidOperationException("Telegram:BotToken не настроен");

        // secret_key = SHA256(bot_token) — именно так требует Telegram
        _secretKey = SHA256.HashData(Encoding.UTF8.GetBytes(botToken));
    }

    public bool ValidateAuthData(TelegramAuthData data)
    {
        // 1. Проверяем свежесть данных (не старше 24 часов)
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (now - data.AuthDate > 86400)
            return false;

        // 2. Строим data_check_string: все поля кроме hash, отсортированные по ключу, через \n
        var fields = new SortedDictionary<string, string>
        {
            ["id"] = data.Id.ToString(),
            ["auth_date"] = data.AuthDate.ToString(),
        };

        if (!string.IsNullOrEmpty(data.FirstName)) fields["first_name"] = data.FirstName;
        if (!string.IsNullOrEmpty(data.LastName)) fields["last_name"] = data.LastName;
        if (!string.IsNullOrEmpty(data.Username)) fields["username"] = data.Username;
        if (!string.IsNullOrEmpty(data.PhotoUrl)) fields["photo_url"] = data.PhotoUrl;

        var dataCheckString = string.Join("\n", fields.Select(kv => $"{kv.Key}={kv.Value}"));

        // 3. Вычисляем HMAC-SHA256
        var computedHash = Convert.ToHexString(
            HMACSHA256.HashData(_secretKey, Encoding.UTF8.GetBytes(dataCheckString))
        ).ToLowerInvariant();

        // 4. Сравниваем через time-constant comparison
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(computedHash),
            Encoding.UTF8.GetBytes(data.Hash.ToLowerInvariant())
        );
    }
}

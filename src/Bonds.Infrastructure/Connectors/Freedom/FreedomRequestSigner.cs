using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Bonds.Infrastructure.Connectors.Freedom;

/// <summary>
/// Подпись Tradernet API v3: <c>hex(lowercase(HMAC-SHA256(key = privateKey UTF-8, msg = body + timestamp)))</c>,
/// где body — ровно та строка, что уходит в теле запроса. Timestamp — unix-секунды.
/// </summary>
public static class FreedomRequestSigner
{
    public static string Sign(string privateKey, string body, long unixSeconds)
    {
        var message = Encoding.UTF8.GetBytes(body + unixSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(privateKey), message);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Компактный JSON параметров (<c>{"a":1}</c>, без пробелов, порядок ключей как передан; пусто → <c>{}</c>).
    /// Значения: string, целые, bool; остальное — через ToString (InvariantCulture не нужен: используются только эти типы).
    /// </summary>
    public static string BuildBody(IReadOnlyList<KeyValuePair<string, object>>? parameters)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            foreach (var (key, value) in parameters ?? [])
            {
                switch (value)
                {
                    case string s: w.WriteString(key, s); break;
                    case int i: w.WriteNumber(key, i); break;
                    case long l: w.WriteNumber(key, l); break;
                    case bool b: w.WriteBoolean(key, b); break;
                    default: throw new ArgumentException($"Неподдерживаемый тип параметра {key}: {value.GetType().Name}");
                }
            }

            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}

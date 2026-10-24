namespace Bonds.Infrastructure.Connectors.Freedom;

/// <summary>
/// Ошибка Freedom API (HTTP не-2xx либо поле <c>errMsg</c>/<c>error</c> в теле). Сообщение НЕ содержит ключей и подписи.
/// </summary>
public sealed class FreedomApiException : Exception
{
    public string Command { get; }

    public FreedomApiException(string command, string message, Exception? inner = null)
        : base($"Freedom API {command}: {message}", inner)
    {
        Command = command;
    }

    /// <summary>
    /// Временный сбой, а не отказ в ключе/заявке: сеть/таймаут, HTTP 5xx, 408 (таймаут запроса), 429 (лимит частоты) и тексты брокера о перегрузке
    /// (<see cref="IsTransientText"/>). Такой сбой НИКОГДА не делает ключ невалидным — повтор на следующем тике/попытке.
    /// </summary>
    public bool IsTransient =>
        InnerException is HttpRequestException or TaskCanceledException or TimeoutException
        || Message.Contains("HTTP 5", StringComparison.Ordinal)
        || Message.Contains("HTTP 408", StringComparison.Ordinal)
        || Message.Contains("HTTP 429", StringComparison.Ordinal)
        || IsTransientText(Message);

    /// <summary>Брокер отверг ключ (HTTP 401/403) — ключ невалиден (задача 67, #16), в отличие от <see cref="IsTransient"/>.</summary>
    public bool IsAuthRejected =>
        Message.Contains("HTTP 401", StringComparison.Ordinal) || Message.Contains("HTTP 403", StringComparison.Ordinal);

    /// <summary>Текст брокера о перегрузке/лимите частоты («too many requests», «rate limit», «слишком много запросов», «try again later»).</summary>
    public static bool IsTransientText(string? text) =>
        !string.IsNullOrWhiteSpace(text)
        && (text.Contains("too many requests", StringComparison.OrdinalIgnoreCase)
            || text.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
            || text.Contains("слишком много запросов", StringComparison.OrdinalIgnoreCase)
            || text.Contains("try again later", StringComparison.OrdinalIgnoreCase));
}

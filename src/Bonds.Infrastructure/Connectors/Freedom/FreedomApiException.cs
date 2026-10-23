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
}

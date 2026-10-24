using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Bonds.Infrastructure.Connectors.Freedom;

/// <summary>
/// Подписанный запрос Tradernet API v3 (общий транспорт клиента на чтение <see cref="FreedomApiClient"/> и торгового шлюза исполнителя): <c>POST /api/{cmd}</c>, тело —
/// компактный JSON параметров (<see cref="FreedomRequestSigner.BuildBody"/>), заголовки <c>X-NtApi-PublicKey</c> / <c>X-NtApi-Timestamp</c> / <c>X-NtApi-Sig</c>.
/// Возвращает HTTP-код и текст ответа как есть; сетевые исключения (<see cref="HttpRequestException"/>, таймаут) НЕ перехватывает — классификацию (ошибка чтения или
/// «исход заявки неизвестен») делает вызывающий. Ключи, подпись и тело не логируются. Повторов нет (у HttpClient «freedom» нет политики ретраев — заявка не задублируется).
/// </summary>
public static class FreedomSignedHttp
{
    public static async Task<(HttpStatusCode Status, string Body)> SendAsync(
        HttpClient http, TimeProvider time, FreedomCredentials credentials, string cmd, IReadOnlyList<KeyValuePair<string, object>>? parameters, CancellationToken ct)
    {
        var body = FreedomRequestSigner.BuildBody(parameters);
        var timestamp = time.GetUtcNow().ToUnixTimeSeconds();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/{cmd}")
        {
            Content = new StringContent(body, Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
        };
        request.Headers.TryAddWithoutValidation("X-NtApi-PublicKey", credentials.PublicKey);
        request.Headers.TryAddWithoutValidation("X-NtApi-Timestamp", timestamp.ToString(CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("X-NtApi-Sig", FreedomRequestSigner.Sign(credentials.PrivateKey, body, timestamp));

        using var response = await http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        return (response.StatusCode, text);
    }
}

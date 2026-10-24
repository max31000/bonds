using System.Globalization;
using System.Text.Json;
using Bonds.Infrastructure.Connectors.Freedom.Models;
using Microsoft.Extensions.Logging;

namespace Bonds.Infrastructure.Connectors.Freedom;

/// <summary>
/// HTTP-реализация <see cref="IFreedomApiClient"/> (Tradernet API v3). Именованный клиент <see cref="HttpClientName"/>
/// (BaseAddress = <see cref="FreedomOptions.BaseUrl"/>, регистрация — DependencyInjection). Запрос:
/// <c>POST {BaseUrl}/api/{cmd}</c>, тело — компактный JSON параметров, заголовки
/// <c>X-NtApi-PublicKey</c> / <c>X-NtApi-Timestamp</c> / <c>X-NtApi-Sig</c> (см. <see cref="FreedomRequestSigner"/>).
/// Ключи, подпись и заголовки не логируются (LogDebug — только имя команды).
/// </summary>
public sealed class FreedomApiClient : IFreedomApiClient
{
    public const string HttpClientName = "freedom";

    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private readonly ILogger? _logger;

    public FreedomApiClient(IHttpClientFactory httpClientFactory, TimeProvider? time = null, ILogger<FreedomApiClient>? logger = null)
    {
        _http = httpClientFactory.CreateClient(HttpClientName);
        _time = time ?? TimeProvider.System;
        _logger = logger;
    }

    public async Task<FreedomUserInfo> GetUserInfoAsync(FreedomCredentials credentials, CancellationToken ct = default) =>
        Parse("GetAllUserTexInfo", await SendAsync(credentials, "GetAllUserTexInfo", null, ct), FreedomParser.ParseUserInfo);

    public async Task<FreedomPortfolio> GetPortfolioAsync(FreedomCredentials credentials, CancellationToken ct = default) =>
        Parse("getPositionJson", await SendAsync(credentials, "getPositionJson", null, ct), FreedomParser.ParsePortfolio);

    public async Task<IReadOnlyList<FreedomQuote>> GetQuotesAsync(
        FreedomCredentials credentials, IReadOnlyCollection<string> tickers, CancellationToken ct = default)
    {
        if (tickers.Count == 0) return [];
        var p = new List<KeyValuePair<string, object>> { new("tickers", string.Join(",", tickers)) };
        return Parse("getStockQuotesJson", await SendAsync(credentials, "getStockQuotesJson", p, ct), FreedomParser.ParseQuotes);
    }

    public async Task<IReadOnlyList<FreedomTrade>> GetTradesAsync(
        FreedomCredentials credentials, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var p = new List<KeyValuePair<string, object>>
        {
            new("beginDate", from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            new("endDate", to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            new("max", 1000),
        };
        return Parse("getTradesHistory", await SendAsync(credentials, "getTradesHistory", p, ct), r => FreedomParser.ParseTrades(r, _logger));
    }

    public async Task<IReadOnlyList<FreedomCashFlow>> GetCashFlowsAsync(FreedomCredentials credentials, int take, CancellationToken ct = default)
    {
        var p = new List<KeyValuePair<string, object>> { new("take", take) };
        return Parse("getUserCashFlows", await SendAsync(credentials, "getUserCashFlows", p, ct), FreedomParser.ParseCashFlows);
    }

    public async Task<IReadOnlyList<FreedomInstrument>> FindAsync(FreedomCredentials credentials, string text, CancellationToken ct = default)
    {
        var p = new List<KeyValuePair<string, object>> { new("text", text) };
        return Parse("tickerFinder", await SendAsync(credentials, "tickerFinder", p, ct), FreedomParser.ParseFound);
    }

    private static T Parse<T>(string cmd, (JsonDocument Doc, FreedomCredentials Creds) response, Func<JsonElement, T> parser)
    {
        using (response.Doc)
        {
            FreedomParser.ThrowIfError(cmd, response.Doc.RootElement, response.Creds.PublicKey, response.Creds.PrivateKey);
            try
            {
                return parser(response.Doc.RootElement);
            }
            catch (Exception ex) when (ex is FormatException or InvalidOperationException or KeyNotFoundException or OverflowException or JsonException)
            {
                throw new FreedomApiException(cmd, "неожиданный формат ответа", ex);
            }
        }
    }

    private async Task<(JsonDocument Doc, FreedomCredentials Creds)> SendAsync(
        FreedomCredentials credentials, string cmd, IReadOnlyList<KeyValuePair<string, object>>? parameters, CancellationToken ct)
    {
        _logger?.LogDebug("Freedom API: {Cmd}", cmd);
        try
        {
            var (status, text) = await FreedomSignedHttp.SendAsync(_http, _time, credentials, cmd, parameters, ct);
            if ((int)status is < 200 or > 299)
            {
                throw new FreedomApiException(cmd, $"HTTP {(int)status}");
            }

            try
            {
                return (JsonDocument.Parse(text), credentials);
            }
            catch (JsonException ex)
            {
                throw new FreedomApiException(cmd, "ответ не является JSON", ex);
            }
        }
        catch (HttpRequestException ex)
        {
            throw new FreedomApiException(cmd, "сетевая ошибка", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new FreedomApiException(cmd, "таймаут запроса", ex);
        }
    }
}

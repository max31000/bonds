using System.Net;
using System.Text.Json;
using Bonds.Core.Models;

namespace Bonds.Infrastructure.Connectors.GirBo;

/// <summary>
/// HTTP-реализация <see cref="IGirBoClient"/> через <see cref="IHttpClientFactory"/> (именованный клиент
/// <see cref="HttpClientName"/>, регистрация — DependencyInjection.AddInfrastructure; BaseAddress
/// https://bo.nalog.gov.ru, User-Agent и Accept — там же). Домен bo.nalog.ru отвечает 302 на этот хост.
/// Контракт ошибок — в doc-comment <see cref="IGirBoClient"/>.
/// </summary>
public sealed class GirBoClient : IGirBoClient
{
    public const string HttpClientName = "girbo";

    private readonly HttpClient _http;

    public GirBoClient(IHttpClientFactory httpClientFactory) => _http = httpClientFactory.CreateClient(HttpClientName);

    public async Task<GirBoOrganization?> FindOrganizationAsync(string inn, CancellationToken ct = default)
    {
        var json = await GetStringAsync($"/advanced-search/organizations/search?query={Uri.EscapeDataString(inn)}&page=0", ct);
        return json is null ? null : Parse(() => GirBoParser.ParseSearch(json, inn), "search");
    }

    public async Task<IReadOnlyList<GirBoFiling>> GetFilingsAsync(long organizationId, CancellationToken ct = default)
    {
        var json = await GetStringAsync($"/nbo/organizations/{organizationId}/bfo/", ct);
        return json is null ? [] : Parse(() => GirBoParser.ParseFilings(json), "bfo list");
    }

    public async Task<IReadOnlyList<IssuerFinancials>> GetAnnualFinancialsAsync(
        long bfoId, string inn, int period, DateTime fetchedAtUtc, CancellationToken ct = default)
    {
        var json = await GetStringAsync($"/nbo/bfo/{bfoId}/details", ct);
        return json is null ? [] : Parse(() => GirBoParser.ParseAnnualFinancials(json, inn, period, fetchedAtUtc), "details");
    }

    /// <summary>GET → тело; null на 404 («не найдено» — нормально); недоступность → <see cref="GirBoUnavailableException"/>.</summary>
    private async Task<string?> GetStringAsync(string url, CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(url, ct);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            if (!response.IsSuccessStatusCode)
            {
                throw new GirBoUnavailableException($"ГИР БО вернул HTTP {(int)response.StatusCode} для {url}");
            }

            return await response.Content.ReadAsStringAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (GirBoUnavailableException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            // Сеть/таймаут HttpClient (TaskCanceledException без отмены вызывающего).
            throw new GirBoUnavailableException($"ГИР БО недоступен для {url}: {ex.Message}", ex);
        }
    }

    private static T Parse<T>(Func<T> parse, string what)
    {
        try
        {
            return parse();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            throw new GirBoUnavailableException($"ГИР БО: нечитаемый ответ ({what})", ex);
        }
    }
}

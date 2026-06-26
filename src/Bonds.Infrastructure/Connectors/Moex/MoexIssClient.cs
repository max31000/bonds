using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bonds.Core.Models;
using Microsoft.Extensions.Logging;

namespace Bonds.Infrastructure.Connectors.Moex;

/// <summary>
/// HTTP-реализация <see cref="IMoexIssClient"/> через <see cref="IHttpClientFactory"/>
/// (именованный клиент "moex", регистрируется в DependencyInjection.AddInfrastructure).
/// Базовый адрес — https://iss.moex.com. Сетевые/HTTP-ошибки логируются и превращаются
/// в null/пустой результат (а не бросаются наружу) — источник справочный и не должен валить
/// синк целиком при недоступности (plan/04 Часть C "устойчив к частичным сбоям").
/// </summary>
public sealed class MoexIssClient : IMoexIssClient
{
    public const string HttpClientName = "moex";

    private readonly HttpClient _http;
    private readonly ILogger<MoexIssClient> _logger;

    public MoexIssClient(IHttpClientFactory httpClientFactory, ILogger<MoexIssClient> logger)
    {
        _http = httpClientFactory.CreateClient(HttpClientName);
        _logger = logger;
    }

    public async Task<string?> ResolveSecidByIsinAsync(string isin, CancellationToken ct = default)
    {
        var url = $"/iss/securities.json?q={Uri.EscapeDataString(isin)}&iss.meta=off";
        var json = await GetStringSafeAsync(url, ct);
        if (json is null) return null;

        try
        {
            return MoexSecuritiesParser.ParseSecidFromSearch(json, isin);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse MOEX ISIN search response for {Isin}", isin);
            return null;
        }
    }

    public async Task<MoexSecurityInfo?> GetSecurityInfoAsync(string secid, CancellationToken ct = default)
    {
        var url = $"/iss/engines/stock/markets/bonds/securities/{Uri.EscapeDataString(secid)}.json?iss.meta=off";
        var json = await GetStringSafeAsync(url, ct);
        if (json is null) return null;

        try
        {
            return MoexSecuritiesParser.Parse(json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse MOEX securities response for {Secid}", secid);
            return null;
        }
    }

    /// <summary>Размер страницы ISS для блоков bondization (coupons/amortizations/offers).
    /// ISS отдаёт ровно 20 строк на страницу и капит limit — пагинируем через start.</summary>
    private const int IssPageSize = 20;

    /// <summary>Предохранитель от бесконечного цикла: 50 страниц × 20 = 1000 строк — с запасом
    /// перекрывает любой реальный купонный график. При достижении лимита помечаем неполноту.</summary>
    private const int IssMaxPages = 50;

    public async Task<MoexBondizationResult> GetBondizationAsync(string secid, CancellationToken ct = default)
    {
        // N-1: ISS пагинирует каждый блок по 20 строк. Дочитываем каждый блок отдельным запросом
        // iss.only={block}&start={n}, аккумулируем строки и собираем единый JSON для парсера —
        // тогда DetectIncompleteCoupons видит полный график, а не обрезанный.
        var merged = new JsonObject();
        var dataIncomplete = false;

        foreach (var block in new[] { "coupons", "amortizations", "offers" })
        {
            var (columns, data, blockIncomplete) = await FetchAllBlockRowsAsync(secid, block, ct);
            dataIncomplete |= blockIncomplete;

            // Блок с null-колонками означает, что даже первая страница не доехала (источник недоступен).
            // Для coupons это критично — без купонов считать нечего; помечаем неполноту.
            if (columns is null)
            {
                if (block == "coupons") dataIncomplete = true;
                continue;
            }

            merged[block] = new JsonObject
            {
                ["columns"] = columns,
                ["data"] = data,
            };
        }

        if (!merged.ContainsKey("coupons"))
        {
            // Источник недоступен/не нашёл бумагу — не падаем, отдаём пустой результат с флагом
            // неполноты (spec §4.4): вызывающий код решит, помечать ли инструмент DataIncomplete.
            return new MoexBondizationResult { Secid = secid, DataIncomplete = true };
        }

        try
        {
            var parsed = MoexBondizationParser.Parse(secid, merged.ToJsonString());
            if (dataIncomplete && !parsed.DataIncomplete)
            {
                // Парсер не нашёл «дырки» внутри полученного, но дочитывание оборвалось/упёрлось в лимит —
                // честно помечаем неполноту, а не молчим (§4.4).
                return new MoexBondizationResult
                {
                    Secid = parsed.Secid,
                    Coupons = parsed.Coupons,
                    Amortizations = parsed.Amortizations,
                    Offers = parsed.Offers,
                    DataIncomplete = true,
                };
            }

            return parsed;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse MOEX bondization response for {Secid}", secid);
            return new MoexBondizationResult { Secid = secid, DataIncomplete = true };
        }
    }

    /// <summary>
    /// Дочитывает все страницы одного блока bondization (coupons/amortizations/offers) через start-пагинацию.
    /// Возвращает колонки (из первой непустой страницы), накопленные строки и флаг неполноты
    /// (true, если дочитывание оборвалось не на первой странице или упёрлось в предохранитель).
    /// </summary>
    private async Task<(JsonNode? Columns, JsonArray Data, bool Incomplete)> FetchAllBlockRowsAsync(
        string secid, string blockName, CancellationToken ct)
    {
        JsonNode? columns = null;
        var allData = new JsonArray();
        var incomplete = false;

        for (var page = 0; page < IssMaxPages; page++)
        {
            var start = page * IssPageSize;
            var url = $"/iss/statistics/engines/stock/markets/bonds/bondization/{Uri.EscapeDataString(secid)}.json"
                      + $"?iss.only={blockName}&start={start}&iss.meta=off";
            var json = await GetStringSafeAsync(url, ct);
            if (json is null)
            {
                // Сбой на середине дочитывания (не первая страница) — данные неполны, помечаем (§4.4).
                if (page > 0) incomplete = true;
                break;
            }

            JsonNode? blockNode;
            try
            {
                blockNode = JsonNode.Parse(json)?[blockName];
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to parse MOEX bondization page for {Secid} block {Block} start {Start}", secid, blockName, start);
                if (page > 0) incomplete = true;
                break;
            }

            if (columns is null && blockNode?["columns"] is JsonNode cols)
            {
                columns = cols.DeepClone();
            }

            var data = blockNode?["data"] as JsonArray;
            var pageCount = data?.Count ?? 0;
            if (data is not null)
            {
                foreach (var item in data.ToList())
                {
                    allData.Add(item is null ? null : item.DeepClone());
                }
            }

            // Короткая страница (меньше размера страницы ISS) — последняя; 0 строк — данных больше нет.
            if (pageCount < IssPageSize)
            {
                break;
            }

            if (page == IssMaxPages - 1)
            {
                // Упёрлись в предохранитель, а страница всё ещё полная — вероятно, что-то не дочитали.
                _logger.LogWarning("MOEX bondization paging hit page limit for {Secid} block {Block}", secid, blockName);
                incomplete = true;
            }
        }

        return (columns, allData, incomplete);
    }

    public async Task<YieldCurveSnapshot?> GetYieldCurveAsync(CancellationToken ct = default)
    {
        var url = "/iss/engines/stock/zcyc/securities.json?iss.meta=off";
        var json = await GetStringSafeAsync(url, ct);
        if (json is null) return null;

        try
        {
            return MoexGcurveParser.Parse(json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse MOEX zcyc (Gcurve) response");
            return null;
        }
    }

    public async Task<MoexSecuritySearch?> GetSecuritySearchAsync(string isin, CancellationToken ct = default)
    {
        var url = $"/iss/securities.json?q={Uri.EscapeDataString(isin)}&iss.meta=off";
        var json = await GetStringSafeAsync(url, ct);
        if (json is null) return null;
        try { return MoexSecuritiesParser.ParseSearchInfo(json, isin); }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to parse MOEX search for {Isin}", isin); return null; }
    }

    private async Task<string?> GetStringSafeAsync(string url, CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(url, ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Не логируем секреты — MOEX ISS не использует токены, url безопасен для лога целиком.
            _logger.LogWarning(ex, "MOEX ISS request failed: {Url}", url);
            return null;
        }
    }
}

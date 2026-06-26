using System.Net;
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

    public async Task<MoexBondizationResult> GetBondizationAsync(string secid, CancellationToken ct = default)
    {
        var url = $"/iss/statistics/engines/stock/markets/bonds/bondization/{Uri.EscapeDataString(secid)}.json"
                  + "?iss.only=coupons,amortizations,offers&iss.meta=off";
        var json = await GetStringSafeAsync(url, ct);
        if (json is null)
        {
            // Источник недоступен/не нашёл бумагу — не падаем, отдаём пустой результат с флагом
            // неполноты (spec §4.4): вызывающий код решит, помечать ли инструмент DataIncomplete.
            return new MoexBondizationResult { Secid = secid, DataIncomplete = true };
        }

        try
        {
            return MoexBondizationParser.Parse(secid, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse MOEX bondization response for {Secid}", secid);
            return new MoexBondizationResult { Secid = secid, DataIncomplete = true };
        }
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

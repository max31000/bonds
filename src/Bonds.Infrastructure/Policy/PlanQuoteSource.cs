using Bonds.Core.Credentials;
using Bonds.Infrastructure.Connectors.Freedom;
using Bonds.Infrastructure.Connectors.TInvest;
using Microsoft.Extensions.Logging;

namespace Bonds.Infrastructure.Policy;

/// <summary>
/// Стакан глубины 1 в единицах брокера: у облигаций T-Invest цены в ПУНКТАХ (процент номинала), у остальных — в валюте цены за штуку.
/// Любое поле может отсутствовать (рынок закрыт, бумага не торгуется).
/// </summary>
public sealed record BookQuote(decimal? Bid, decimal? Ask, decimal? Last);

/// <summary>
/// Источник котировок для планов (ТОЛЬКО ЧТЕНИЕ рыночных данных). Вынесен за интерфейс, чтобы тесты подставляли стакан без сети, а правила получали
/// готовые <see cref="BookQuote"/>. Отправки заявок здесь нет и быть не должно.
/// </summary>
public interface IPlanQuoteSource
{
    /// <summary>Стаканы T-Invest по FIGI (<c>GetOrderBook</c> глубина 1 + последняя цена). Ключ результата — FIGI.</summary>
    Task<IReadOnlyDictionary<string, BookQuote>> GetTInvestAsync(IReadOnlyCollection<string> figis, CancellationToken ct = default);

    /// <summary>Котировки Freedom (bid/ask/last) по тикерам пользователя. Ключ результата — тикер ВЕРХНИМ регистром. Нет ключей — пусто.</summary>
    Task<IReadOnlyDictionary<string, BookQuote>> GetFreedomAsync(ulong userId, IReadOnlyCollection<string> tickers, CancellationToken ct = default);
}

/// <summary>Реальная реализация поверх <see cref="ITInvestPortfolioClient.GetQuotesAsync"/> и <see cref="IFreedomApiClient.GetQuotesAsync"/> (оба — чтение).</summary>
public sealed class PlanQuoteSource : IPlanQuoteSource
{
    private readonly ITInvestPortfolioClient _tInvest;
    private readonly IFreedomApiClient _freedom;
    private readonly IUserCredentialStore _credentials;
    private readonly ILogger<PlanQuoteSource> _logger;

    public PlanQuoteSource(ITInvestPortfolioClient tInvest, IFreedomApiClient freedom, IUserCredentialStore credentials, ILogger<PlanQuoteSource> logger)
    {
        _tInvest = tInvest;
        _freedom = freedom;
        _credentials = credentials;
        _logger = logger;
    }

    public async Task<IReadOnlyDictionary<string, BookQuote>> GetTInvestAsync(IReadOnlyCollection<string> figis, CancellationToken ct = default)
    {
        var result = new Dictionary<string, BookQuote>(StringComparer.Ordinal);
        if (figis.Count == 0) return result;

        try
        {
            var quotes = await _tInvest.GetQuotesAsync(figis, ct);
            foreach (var (figi, q) in quotes) result[figi] = new BookQuote(q.BestBid, q.BestAsk, q.LastPrice);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Нет токена или сбой gRPC: планы без стакана не строятся (бумаги пропускаются «нет стакана»), цикл не падает.
            _logger.LogWarning(ex, "Plan quotes: T-Invest order books unavailable");
        }

        return result;
    }

    public async Task<IReadOnlyDictionary<string, BookQuote>> GetFreedomAsync(ulong userId, IReadOnlyCollection<string> tickers, CancellationToken ct = default)
    {
        var result = new Dictionary<string, BookQuote>(StringComparer.OrdinalIgnoreCase);
        if (tickers.Count == 0) return result;

        var secret = await _credentials.GetSecretResultAsync(userId, CredentialProviders.Freedom);
        if (secret.Outcome != CredentialReadOutcome.Ok
            || secret.Fields is null
            || !secret.Fields.TryGetValue("publicKey", out var publicKey)
            || !secret.Fields.TryGetValue("privateKey", out var privateKey)
            || string.IsNullOrWhiteSpace(publicKey)
            || string.IsNullOrWhiteSpace(privateKey))
        {
            return result;
        }

        try
        {
            var quotes = await _freedom.GetQuotesAsync(new FreedomCredentials(publicKey, privateKey), tickers, ct);
            foreach (var q in quotes) result[q.Ticker.ToUpperInvariant()] = new BookQuote(q.Bid, q.Ask, q.Last);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Plan quotes: Freedom quotes unavailable for user {UserId}", userId);
        }

        return result;
    }
}

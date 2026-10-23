using Bonds.Infrastructure.Connectors.Freedom.Models;

namespace Bonds.Infrastructure.Connectors.Freedom;

/// <summary>
/// Клиент Freedom (Tradernet API v3), ТОЛЬКО ЧТЕНИЕ (размещение и отмена заявок — фаза 3, отдельная задача; в коде их нет, это закреплено архитектурным тестом).
/// Без состояния: ключи приходят параметром. Любая ошибка брокера — <see cref="FreedomApiException"/>.
/// </summary>
public interface IFreedomApiClient
{
    /// <summary><c>GetAllUserTexInfo</c>: номер счёта и тариф.</summary>
    Task<FreedomUserInfo> GetUserInfoAsync(FreedomCredentials credentials, CancellationToken ct = default);

    /// <summary><c>getPositionJson</c>: остатки денег и позиции. Поле <c>currval</c> брокера намеренно не читается (устаревает).</summary>
    Task<FreedomPortfolio> GetPortfolioAsync(FreedomCredentials credentials, CancellationToken ct = default);

    /// <summary><c>getStockQuotesJson</c>, тикеры через запятую.</summary>
    Task<IReadOnlyList<FreedomQuote>> GetQuotesAsync(FreedomCredentials credentials, IReadOnlyCollection<string> tickers, CancellationToken ct = default);

    /// <summary><c>getTradesHistory</c> за период (даты включительно, до 1000 сделок).</summary>
    Task<IReadOnlyList<FreedomTrade>> GetTradesAsync(FreedomCredentials credentials, DateOnly from, DateOnly to, CancellationToken ct = default);

    /// <summary><c>getUserCashFlows</c>: последние <paramref name="take"/> движений денег.</summary>
    Task<IReadOnlyList<FreedomCashFlow>> GetCashFlowsAsync(FreedomCredentials credentials, int take, CancellationToken ct = default);

    /// <summary>
    /// <c>tickerFinder</c>. ВАЖНО: тикеры <c>*.ITS</c> для этого типа счёта ТОЛЬКО КОТИРОВОЧНЫЕ — торговля по ним
    /// отклоняется («instrument is not available for trading»). Торговать нужно <c>*.US</c>; маршрут на ITS
    /// выбирает брокер сам.
    /// </summary>
    Task<IReadOnlyList<FreedomInstrument>> FindAsync(FreedomCredentials credentials, string text, CancellationToken ct = default);
}

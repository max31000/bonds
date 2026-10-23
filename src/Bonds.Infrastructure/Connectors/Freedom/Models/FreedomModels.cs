namespace Bonds.Infrastructure.Connectors.Freedom.Models;

// ЕДИНИЦЫ (контракт Freedom, задача 59). Все деньги — decimal в ВАЛЮТЕ САМОЙ ЗАПИСИ, без пересчёта в рубли:
// цены/market_value позиции — в валюте бумаги (Currency), остатки — в своей валюте счёта, суммы cashflow —
// в своей валюте со знаком (+ приход / − расход). Курсы валют брокера (поле currval) НЕ читаются: у брокера
// оно бывает устаревшим, оценку делаем по курсам bonds (задача 60). Количество — штуки.

/// <summary>Профиль счёта (<c>GetAllUserTexInfo</c>). Тариф сравнивать по <see cref="TariffId"/>: имя бывает коротким ("Standard").</summary>
public sealed record FreedomUserInfo(string AccountNumber, int TariffId, string TariffName);

/// <summary>Остаток денег в одной валюте (<c>result.ps.acc[]</c>: <c>curr</c>, <c>s</c>).</summary>
public sealed record FreedomCashBalance(string Currency, decimal Amount);

/// <summary>
/// Позиция (<c>result.ps.pos[]</c>). <see cref="AveragePrice"/> (<c>bal_price_a</c>), <see cref="MarketPrice"/>
/// (<c>mkt_price</c>) и <see cref="MarketValue"/> (<c>market_value</c>) — в валюте бумаги <see cref="Currency"/>.
/// </summary>
public sealed record FreedomPosition(
    string Ticker,
    string Name,
    decimal Quantity,
    string Currency,
    decimal AveragePrice,
    decimal MarketPrice,
    decimal MarketValue);

public sealed record FreedomPortfolio(
    IReadOnlyList<FreedomCashBalance> Cash,
    IReadOnlyList<FreedomPosition> Positions);

/// <summary>Котировка (<c>result.q[]</c>). Цены — в валюте <see cref="Currency"/> (<c>x_curr</c>); null, если стакан пуст.</summary>
public sealed record FreedomQuote(
    string Ticker,
    decimal? Bid,
    decimal? Ask,
    decimal? Last,
    string? Currency,
    decimal? MinStep);

public enum FreedomTradeSide
{
    Buy,
    Sell,
}

/// <summary>
/// Сделка (<c>getTradesHistory</c>). Числа у брокера приходят строками — разобраны InvariantCulture.
/// <see cref="Price"/>/<see cref="Amount"/> — в валюте сделки <see cref="Currency"/>; <see cref="Commission"/> —
/// в <see cref="CommissionCurrency"/> (может отличаться от валюты сделки).
/// </summary>
public sealed record FreedomTrade(
    string Id,
    string OrderId,
    DateTime Date,
    string Ticker,
    FreedomTradeSide Side,
    decimal Quantity,
    decimal Price,
    decimal Amount,
    string Currency,
    decimal Commission,
    string CommissionCurrency);

/// <summary>
/// Движение денег (<c>getUserCashFlows</c>). <see cref="Amount"/> (<c>sumRaw</c>) — в <see cref="Currency"/> со знаком:
/// + приход / − расход. <see cref="TypeCode"/>: <c>bank</c>/<c>card</c> — пополнения, <c>commission_for_trades</c>,
/// <c>dividend</c>, <c>tax</c> и др.
/// </summary>
public sealed record FreedomCashFlow(
    string Id,
    string TypeCode,
    DateTime Date,
    string Currency,
    decimal Amount,
    string Comment);

/// <summary>Результат поиска (<c>tickerFinder</c>).</summary>
public sealed record FreedomInstrument(
    string Ticker,
    string Name,
    string? Market,
    string? CodeSub,
    string? Currency);

using Bonds.Core.Models;

namespace Bonds.Api.Endpoints.Dtos;

// Контракты /api/cash и /api/trading-meta (задача 61). ЕДИНИЦЫ: поля без суффикса Rub — в ВАЛЮТЕ СТРОКИ (поле currency);
// *Rub — рубли (рубль как есть, иная валюта — по курсу bonds), null = курса нет (тогда dataIncomplete). Знак суммы движения — как у брокера
// (плюс — пополнение, минус — вывод).

/// <summary>Остаток денег в одной валюте.</summary>
public sealed record CashBalanceDto
{
    public required string Currency { get; init; }

    /// <summary>В своей валюте.</summary>
    public required decimal Amount { get; init; }

    /// <summary>В рублях; null — нет курса валюты.</summary>
    public decimal? AmountRub { get; init; }
}

/// <summary>Пополнение или вывод денег.</summary>
public sealed record CashMovementDto
{
    public required DateTime Date { get; init; }

    /// <summary><c>deposit|withdrawal</c> (camelCase).</summary>
    public required CashMovementType Type { get; init; }

    /// <summary>В своей валюте СО ЗНАКОМ брокера.</summary>
    public required decimal Amount { get; init; }

    public required string Currency { get; init; }
    public decimal? AmountRub { get; init; }
}

/// <summary>Деньги одного брокера.</summary>
public sealed record BrokerCashSummaryDto
{
    /// <summary><c>tinvest|freedom</c>.</summary>
    public required string Broker { get; init; }

    public required IReadOnlyList<CashBalanceDto> Balances { get; init; }

    /// <summary>
    /// Свободные РУБЛИ без валюты (T-Invest: <c>cashRub − валюта в рублях</c>, не ниже 0; Freedom: остаток в RUB). null — рублёвого остатка/баланса нет.
    /// Именно эту сумму правила фазы 2 считают свободными деньгами в рублях.
    /// </summary>
    public decimal? CashRubOnly { get; init; }

    /// <summary>Все деньги в рублях, включая валюту (T-Invest — брокерский итог по деньгам); null — баланс не синкался.</summary>
    public decimal? TotalRub { get; init; }

    public DateTime? SyncedAt { get; init; }

    /// <summary>У части валют нет курса — рублёвые суммы занижены.</summary>
    public bool DataIncomplete { get; init; }

    /// <summary>Последние 10 пополнений и выводов, новые сверху.</summary>
    public required IReadOnlyList<CashMovementDto> RecentMovements { get; init; }
}

public sealed record CashOverviewDto
{
    public required IReadOnlyList<BrokerCashSummaryDto> Brokers { get; init; }
    public required string Disclaimer { get; init; }
}

/// <summary>
/// Торговые метаданные бумаги. <see cref="MinPriceIncrement"/> — шаг цены в ВАЛЮТЕ ЦЕНЫ за 1 бумагу (у облигаций T-Invest уже в РУБЛЯХ:
/// пункты / 100 × номинал); <see cref="MinPriceIncrementPoints"/> — сырой шаг в ПУНКТАХ (только облигации T-Invest).
/// </summary>
public sealed record TradingMetaDto
{
    public required string Broker { get; init; }

    /// <summary>ISIN, иначе тикер.</summary>
    public required string InstrumentKey { get; init; }

    public required string Ticker { get; init; }
    public string? Uid { get; init; }
    public string? Figi { get; init; }
    public string? ClassCode { get; init; }
    public required decimal Lot { get; init; }
    public required decimal MinPriceIncrement { get; init; }
    public decimal? MinPriceIncrementPoints { get; init; }
    public required string Currency { get; init; }
    public required bool ApiTradeAvailable { get; init; }
    public required bool BuyAvailable { get; init; }
    public required bool SellAvailable { get; init; }

    /// <summary>Фонды: были выплаты за 12 месяцев (null — неизвестно/не фонд).</summary>
    public bool? PaysDistributions { get; init; }

    /// <summary>Freedom: есть котировочный двойник <c>.ITS</c> (null — не проверялось).</summary>
    public bool? ItsTwin { get; init; }

    public required DateTime UpdatedAt { get; init; }
}

public sealed record TradingMetaResponseDto
{
    public required IReadOnlyList<TradingMetaDto> Items { get; init; }
}

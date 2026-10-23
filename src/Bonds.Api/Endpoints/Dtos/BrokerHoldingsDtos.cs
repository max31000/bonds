using Bonds.Core.Models;

namespace Bonds.Api.Endpoints.Dtos;

// Контракты /api/brokers/freedom/* (задача 60). ЕДИНИЦЫ: поля без суффикса Rub/Usd — в ВАЛЮТЕ СТРОКИ (поле currency), НЕ в рублях;
// *Rub — рубли по курсам bonds (ICurrencyRateLookup), null = курса нет (тогда dataIncomplete); доли — ДОЛИ (0.05 = 5%).

/// <summary>Позиция Freedom. Цены, стоимость и P&amp;L — в валюте бумаги <see cref="Currency"/>.</summary>
public sealed record BrokerPositionDto
{
    public required string Ticker { get; init; }
    public required string Name { get; init; }

    /// <summary><c>etf|stock|bond|fund|other</c> — эвристика по названию.</summary>
    public required string AssetClass { get; init; }

    public required string Currency { get; init; }
    public required decimal Quantity { get; init; }
    public required decimal AvgPrice { get; init; }
    public required decimal LastPrice { get; init; }
    public required decimal MarketValue { get; init; }

    /// <summary>Стоимость − средняя × количество, в валюте бумаги.</summary>
    public required decimal UnrealizedPnl { get; init; }

    /// <summary>P&amp;L к стоимости входа, ДОЛЯ; null — цены входа нет.</summary>
    public decimal? UnrealizedPnlFraction { get; init; }

    /// <summary>Стоимость в РУБЛЯХ; null — нет курса валюты.</summary>
    public decimal? MarketValueRub { get; init; }
}

/// <summary>Остаток денег: <see cref="Amount"/> — в своей валюте, <see cref="AmountRub"/> — рубли (null — нет курса).</summary>
public sealed record BrokerCashDto
{
    public required string Currency { get; init; }
    public required decimal Amount { get; init; }
    public decimal? AmountRub { get; init; }
}

public sealed record BrokerFeesDto
{
    /// <summary>Сколько комиссий уплачено за всё время, USD (положительное число).</summary>
    public required decimal FeesPaidUsd { get; init; }

    /// <summary>Часть комиссий не удалось перевести в USD (нет курса).</summary>
    public bool DataIncomplete { get; init; }
}

/// <summary>Ответ <c>GET /api/brokers/freedom/holdings</c>.</summary>
public sealed record BrokerHoldingsResponseDto
{
    public required IReadOnlyList<BrokerPositionDto> Positions { get; init; }
    public required IReadOnlyList<BrokerCashDto> Cash { get; init; }

    /// <summary>Σ позиций и денег, РУБЛИ (при <see cref="DataIncomplete"/> занижено).</summary>
    public required decimal TotalValueRub { get; init; }

    /// <summary>То же в долларах по курсу USD; null — курса USD нет.</summary>
    public decimal? TotalValueUsd { get; init; }

    public DateTime? SyncedAt { get; init; }
    public bool DataIncomplete { get; init; }
    public required BrokerFeesDto Fees { get; init; }
    public required string Disclaimer { get; init; }
}

public sealed record BrokerOperationDto
{
    public required string ExternalId { get; init; }
    public required DateTime Date { get; init; }

    /// <summary>camelCase: buy|sell|deposit|withdrawal|fee|dividend|tax|other.</summary>
    public required BrokerOperationType Type { get; init; }

    public string? Ticker { get; init; }
    public decimal? Quantity { get; init; }

    /// <summary>Цена за бумагу в валюте сделки.</summary>
    public decimal? Price { get; init; }

    /// <summary>Сумма в валюте операции СО ЗНАКОМ: плюс — деньги пришли, минус — ушли.</summary>
    public required decimal Amount { get; init; }

    public required string Currency { get; init; }
    public required string RawType { get; init; }
    public string? Comment { get; init; }
}

/// <summary>Ответ <c>GET /api/brokers/freedom/operations</c>: новые сверху.</summary>
public sealed record BrokerOperationsResponseDto
{
    public required IReadOnlyList<BrokerOperationDto> Operations { get; init; }
}

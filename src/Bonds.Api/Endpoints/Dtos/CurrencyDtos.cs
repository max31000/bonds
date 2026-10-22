namespace Bonds.Api.Endpoints;

/// <summary>
/// Контракт <c>GET /api/currencies</c> (задача 55). Деньги - РУБЛИ, <c>quantity</c> - ЕДИНИЦЫ валюты (юани, не лоты), курс/цены - РУБЛИ ЗА 1
/// ЕДИНИЦУ валюты; все доли (<c>unrealizedPnlFraction</c>, <c>totalReturnFraction</c>, <c>xirrFraction</c>, <c>rateChange</c>) - ДОЛИ
/// (0.12 = 12%; ×100 только во фронт-форматтерах). P&amp;L-поля - из <c>PositionIncomeCalculator</c> (average cost), та же методика, что у
/// страницы «Доходы»: курсовая разница = бумажная (<c>unrealizedPnlRub</c>) + реализованная (<c>realizedPnlRub</c>).
/// </summary>
public sealed record CurrenciesResponseDto
{
    public required DateOnly AsOf { get; init; }

    /// <summary>Валюты счёта, включая проданные (qty 0), если по ним есть операции.</summary>
    public required IReadOnlyList<CurrencyRowDto> Currencies { get; init; }

    public required CurrenciesTotalsDto Totals { get; init; }
    public required string Disclaimer { get; init; }
}

public sealed record CurrenciesTotalsDto
{
    /// <summary>Σ стоимости открытых валютных позиций, рубли.</summary>
    public required decimal MarketValueRub { get; init; }

    /// <summary>Σ себестоимости остатка (average cost), рубли.</summary>
    public required decimal InvestedRub { get; init; }

    public required decimal UnrealizedPnlRub { get; init; }
    public required decimal RealizedPnlRub { get; init; }
    public required decimal TotalResultRub { get; init; }

    /// <summary>У какой-либо открытой валюты нет курса вообще (стоимость взята как 0) - итоги занижены.</summary>
    public bool DataIncomplete { get; init; }
}

public sealed record CurrencyRowDto
{
    public required ulong CurrencyId { get; init; }

    /// <summary>ISO-код верхним регистром ("CNY").</summary>
    public required string IsoCode { get; init; }
    public string? Ticker { get; init; }
    public string? Name { get; init; }

    /// <summary>ЕДИНИЦЫ валюты (0 - проданная).</summary>
    public required decimal Quantity { get; init; }

    /// <summary>Курс, РУБЛИ за 1 единицу (позиция брокера, иначе последнее закрытие истории); null - курса нет.</summary>
    public decimal? CurrentPriceRub { get; init; }
    public DateTime? PriceAsOf { get; init; }

    public required decimal MarketValueRub { get; init; }

    /// <summary>Средняя цена остатка по журналу (average cost), РУБЛИ за 1 единицу; 0 при нулевом остатке.</summary>
    public required decimal AvgCostRub { get; init; }

    /// <summary>Себестоимость остатка, рубли.</summary>
    public required decimal InvestedRub { get; init; }

    public decimal? UnrealizedPnlRub { get; init; }

    /// <summary>ДОЛЯ от себестоимости остатка.</summary>
    public decimal? UnrealizedPnlFraction { get; init; }
    public required decimal RealizedPnlRub { get; init; }
    public required decimal TotalResultRub { get; init; }

    /// <summary>ДОЛЯ (итог / Σ покупок).</summary>
    public decimal? TotalReturnFraction { get; init; }

    /// <summary>ДОЛЯ годовых; null при владении &lt; 30 дней.</summary>
    public decimal? XirrFraction { get; init; }

    /// <summary>Изменение курса за 1/3/12 месяцев - ДОЛИ; окно null, если истории не хватает.</summary>
    public required CurrencyRateChangeDto RateChange { get; init; }

    /// <summary>История курса за последний год (рубли за 1 единицу), по возрастанию даты.</summary>
    public required IReadOnlyList<CurrencyRatePointDto> RateHistory { get; init; }

    public required bool HasUnknownLots { get; init; }
    public required bool DataIncomplete { get; init; }
}

public sealed record CurrencyRateChangeDto
{
    public decimal? M1 { get; init; }
    public decimal? M3 { get; init; }
    public decimal? M12 { get; init; }
}

public sealed record CurrencyRatePointDto
{
    public required DateOnly Date { get; init; }

    /// <summary>РУБЛИ за 1 единицу валюты.</summary>
    public required decimal CloseRub { get; init; }
}

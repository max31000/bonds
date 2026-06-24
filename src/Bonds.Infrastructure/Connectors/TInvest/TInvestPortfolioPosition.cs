namespace Bonds.Infrastructure.Connectors.TInvest;

/// <summary>
/// Холдинг портфеля, разобранный из <c>PortfolioResponse.Positions</c> (T-Invest gRPC,
/// см. README.md этого каталога — верификация контракта §12.2). Только облигации
/// интересны этому продукту (spec §3 "в скоупе"): другие типы инструментов отфильтровываются
/// на уровне <see cref="ITInvestPortfolioClient"/>.
/// </summary>
public sealed class TInvestPortfolioPosition
{
    public required string Figi { get; init; }
    public required string InstrumentUid { get; init; }
    public decimal Quantity { get; init; }

    /// <summary>Средневзвешенная цена покупки (cost basis), в валюте инструмента за одну облигацию.</summary>
    public decimal AveragePositionPrice { get; init; }

    /// <summary>Текущая чистая цена по данным брокера (T-Invest = "сейчас", plan/00 §4).</summary>
    public decimal? CurrentPrice { get; init; }

    /// <summary>Текущий НКД по позиции (T-Invest = первичный источник "сейчас" для движка, plan/00 §4).</summary>
    public decimal? CurrentNkd { get; init; }
}

/// <summary>
/// Запись журнала операций, разобранная из <c>GetOperationsByCursorResponse.Items</c>
/// (см. README.md — это более новый/детальный эндпоинт по сравнению с GetOperations,
/// несёт курсор для инкрементального синка). <see cref="Id"/> используется как
/// <see cref="Bonds.Core.Models.Operation.ExternalId"/> для идемпотентного upsert.
/// </summary>
public sealed class TInvestOperation
{
    public required string Id { get; init; }
    public required string OperationType { get; init; }
    public string? Figi { get; init; }
    public string? InstrumentUid { get; init; }
    public DateTime Date { get; init; }
    public decimal PaymentRub { get; init; }
    public decimal? Quantity { get; init; }

    /// <summary>НКД, относящийся к данной операции (если применимо, напр. покупка/продажа купонной бумаги).</summary>
    public decimal? AccruedInterest { get; init; }
}

/// <summary>Текущая котировка по открытой позиции (T-Invest MarketData, plan/00 §4 "T-Invest = сейчас").</summary>
public sealed class TInvestQuote
{
    public required string Figi { get; init; }
    public decimal? LastPrice { get; init; }

    /// <summary>Лучшая цена спроса/предложения — индикатор ликвидности (упрощённый "стакан").</summary>
    public decimal? BestBid { get; init; }
    public decimal? BestAsk { get; init; }
}

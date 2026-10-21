namespace Bonds.Infrastructure.Connectors.TInvest;

/// <summary>
/// Результат <c>Operations.GetPortfolio</c>: облигационные позиции + денежные итоги счёта.
/// Все суммы — РУБЛИ (запрос идёт с валютой портфеля по умолчанию = RUB).
/// </summary>
public sealed class TInvestPortfolio
{
    public required IReadOnlyList<TInvestPortfolioPosition> Bonds { get; init; }

    /// <summary>
    /// Задача 51: позиции в фондах (БПИФ/ETF, <c>InstrumentType == "etf"</c>) — отдельный домен, в облигационные
    /// <see cref="Bonds"/> не попадают. <see cref="TInvestPortfolioPosition.CurrentPrice"/> и
    /// <see cref="TInvestPortfolioPosition.AveragePositionPrice"/> у фонда — РУБЛИ ЗА ПАЙ (GetPortfolio, конверсии нет);
    /// <see cref="TInvestPortfolioPosition.CurrentNkd"/> у фонда не используется.
    /// </summary>
    public IReadOnlyList<TInvestPortfolioPosition> Funds { get; init; } = [];

    /// <summary>
    /// Свободные деньги на счёте (<c>TotalAmountCurrencies</c> — все валюты, пересчитанные брокером
    /// в рубли). Сюда, в частности, падают выплаты погашений/купонов/амортизаций. Null — брокер не отдал.
    /// </summary>
    public decimal? CashRub { get; init; }

    /// <summary>
    /// Полная стоимость счёта по оценке брокера (<c>TotalAmountPortfolio</c>: облигации + деньги +
    /// любые другие активы, которые продукт не анализирует). Для сверки с тем, что видно в приложении.
    /// </summary>
    public decimal? TotalPortfolioRub { get; init; }
}

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

    /// <summary>Текущая чистая цена по данным брокера (T-Invest = "сейчас").</summary>
    public decimal? CurrentPrice { get; init; }

    /// <summary>Текущий НКД по позиции (T-Invest = первичный источник "сейчас" для движка).</summary>
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

    /// <summary>
    /// Тип инструмента операции — имя enum T-Invest <c>InstrumentType</c> ("Bond", "Share", "Currency",
    /// "Etf", ...). Null — не указан (операции по счёту без инструмента). Облигационный журнал
    /// сохраняет Buy/Sell/Coupon/Redemption/Amortization/SecurityIn/Out только для <see cref="IsBond"/>.
    /// </summary>
    public string? InstrumentKind { get; init; }

    public bool IsBond => string.Equals(InstrumentKind, "Bond", StringComparison.OrdinalIgnoreCase);

    /// <summary>Задача 51: операция по фонду (БПИФ/ETF) — идёт в фондовый журнал, не в облигационный.</summary>
    public bool IsFund => string.Equals(InstrumentKind, "Etf", StringComparison.OrdinalIgnoreCase);

    /// <summary>НКД, относящийся к данной операции (если применимо, напр. покупка/продажа купонной бумаги).</summary>
    public decimal? AccruedInterest { get; init; }
}

/// <summary>Текущая котировка по открытой позиции (T-Invest MarketData, plan/00 §4 "T-Invest = сейчас").</summary>
public sealed class TInvestQuote
{
    public required string Figi { get; init; }

    /// <summary>
    /// <b>ЕДИНИЦЫ: пункты (% от номинала), НЕ рубли.</b> Официальная семантика T-Invest marketdata
    /// (<c>GetLastPrices</c>) для облигаций — цена в пунктах; рубли = пункты / 100 × номинал
    /// инструмента. Это ОТЛИЧАЕТСЯ от <see cref="TInvestPortfolioPosition.CurrentPrice"/>
    /// (<c>GetPortfolio</c>), который брокер уже конвертирует в рубли за бумагу. Конвертация —
    /// на стороне потребителя (см. <c>Bonds.Infrastructure.Quotes.LiveQuoteConverter</c>),
    /// не здесь: не путать эти два поля при добавлении новых потребителей этого класса —
    /// именно эта путаница была причиной продакшн-бага занижения стоимости портфеля в разы
    /// (LiveQuotesPollingService/BondSyncService часть 3 писали пункты как рубли напрямую).
    /// </summary>
    public decimal? LastPrice { get; init; }

    /// <summary>Лучшая цена спроса/предложения — индикатор ликвидности (упрощённый "стакан").</summary>
    public decimal? BestBid { get; init; }
    public decimal? BestAsk { get; init; }
}

/// <summary>
/// Справочные данные фонда из T-Invest (<c>EtfBy</c> + <c>GetAssetBy</c>). <see cref="TotalExpenseFraction"/> — ДОЛЯ годовых
/// (0.009 = 0.9%) после <see cref="TInvestNumeric.ExpenseRatioToFraction"/>; null — не отдан или не распознан.
/// </summary>
public sealed record TInvestFundInfo
{
    public required string Isin { get; init; }
    public string? Ticker { get; init; }
    public string? Name { get; init; }

    /// <summary><c>Etf.FocusType</c> как есть ("fixed_income", "equity", ...).</summary>
    public string? FocusType { get; init; }

    /// <summary>Управляющая компания (<c>Asset.Brand.Company</c>), если отдана.</summary>
    public string? ManagementCompany { get; init; }

    public decimal? TotalExpenseFraction { get; init; }
}

/// <summary>Выплата фонда на ОДИН пай (<c>Dividend</c>): <see cref="AmountPerShareRub"/> — рубли за пай (<c>DividendNet</c>).</summary>
public sealed record TInvestFundDistribution(DateOnly RecordDate, DateOnly? PaymentDate, decimal AmountPerShareRub);

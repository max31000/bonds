namespace Bonds.Core.Models;

/// <summary>
/// Фонд (БПИФ/ETF) — ОТДЕЛЬНЫЙ домен от облигаций (задача 51): не лежит в instruments/positions/operations,
/// не участвует в облигационном движке (YTM, скоркарта, XIRR облигаций). Справочник обновляется раз в сутки.
/// </summary>
public class Fund
{
    public ulong Id { get; set; }

    /// <summary>FIGI T-Invest (уникален).</summary>
    public string Figi { get; set; } = string.Empty;

    /// <summary>ISIN пая (уникален).</summary>
    public string Isin { get; set; } = string.Empty;

    public string? Ticker { get; set; }

    /// <summary>SECID на MOEX (для TPAY/TOFZ совпадает с тикером). Доска TQBR рынка shares.</summary>
    public string? Secid { get; set; }

    public string? Name { get; set; }
    public string? ManagementCompany { get; set; }

    /// <summary>Тип фокуса как отдал T-Invest (<c>Etf.FocusType</c>: "fixed_income", "equity" и т.п.), без нормализации.</summary>
    public string? FocusType { get; set; }

    /// <summary>
    /// Суммарные расходы фонда (TER) — <b>ДОЛЯ годовых</b> (0.009 = 0.9%). Из T-Invest <c>AssetEtf.TotalExpense</c>
    /// конвертируется ровно один раз на границе коннектора (<c>TInvestNumeric.ExpenseRatioToFraction</c>).
    /// </summary>
    public decimal? TotalExpenseFraction { get; set; }

    /// <summary>Способ прогноза доходности (настройка пользователя; Auto — выбирает аналитика задачи 53).</summary>
    public FundForecastMethod ForecastMethod { get; set; } = FundForecastMethod.Auto;

    /// <summary>Код индекса MOEX для <see cref="FundForecastMethod.IndexYield"/> (напр. "RGBI"); null = автовыбор.</summary>
    public string? BenchmarkIndex { get; set; }

    /// <summary>
    /// Ручная оценка МОДИФИЦИРОВАННОЙ дюрации фонда в <b>ГОДАХ</b> (задача 57), вводится владельцем в карточке фонда; null — не задана
    /// (тогда <c>FundDurationEstimator</c> пробует оценку по индексу). Суточный рефреш справочника это поле не трогает.
    /// </summary>
    public decimal? DurationOverrideYears { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>Момент последнего обновления строки справочника (UTC, ON UPDATE CURRENT_TIMESTAMP) — ворота «раз в сутки».</summary>
    public DateTime UpdatedAt { get; set; }
}

/// <summary>Способ прогноза доходности фонда.</summary>
public enum FundForecastMethod
{
    Auto,
    Distributions,
    IndexYield,
    TrailingReturn,
}

/// <summary>Позиция в фонде на счёте. Все цены — РУБЛИ ЗА ОДИН ПАЙ (не проценты номинала, в отличие от облигаций).</summary>
public class FundPosition
{
    public ulong Id { get; set; }
    public ulong AccountId { get; set; }
    public ulong FundId { get; set; }

    /// <summary>Количество паёв.</summary>
    public decimal Quantity { get; set; }

    /// <summary>Средневзвешенная цена покупки, рубли за пай (как отдал брокер, GetPortfolio.AveragePositionPrice).</summary>
    public decimal AvgPurchasePriceRub { get; set; }

    /// <summary>Текущая цена пая по оценке брокера, рубли за пай (GetPortfolio.CurrentPrice; конверсии нет).</summary>
    public decimal? CurrentPriceRub { get; set; }

    public DateTime? PriceAsOf { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>Операция по фонду. <see cref="AmountRub"/> — знак как отдал брокер (покупка −, выплата +), не переписывать.</summary>
public class FundOperation
{
    public ulong Id { get; set; }
    public ulong AccountId { get; set; }

    /// <summary>Null — FIGI фонда не удалось резолвить; операция сохраняется, чтобы не терять деньги.</summary>
    public ulong? FundId { get; set; }

    public FundOperationType Type { get; set; }
    public DateTime Date { get; set; }

    /// <summary>Сумма, рубли, со знаком брокера.</summary>
    public decimal AmountRub { get; set; }

    /// <summary>Количество паёв (где применимо).</summary>
    public decimal? Quantity { get; set; }

    /// <summary>Идентификатор операции T-Invest — ключ идемпотентности.</summary>
    public string ExternalId { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
}

public enum FundOperationType
{
    Buy,
    Sell,

    /// <summary>Выплата фонда владельцу пая (T-Invest Dividend / DividendTransfer / DivExt).</summary>
    Distribution,
    Tax,
    Fee,
    SecurityIn,
    SecurityOut,
}

/// <summary>Закрытие пая за торговый день. <see cref="CloseRub"/> — РУБЛИ за пай (MOEX TQBR, конверсии нет).</summary>
public sealed record FundPricePoint(DateOnly TradeDate, decimal CloseRub);

/// <summary>Выплата фонда на ОДИН пай (рыночный факт). <see cref="AmountPerShareRub"/> — рубли за пай (T-Invest DividendNet).</summary>
public sealed record FundDistribution(DateOnly RecordDate, DateOnly? PaymentDate, decimal AmountPerShareRub);

/// <summary>
/// Дневная точка облигационного индекса MOEX. <see cref="YieldFraction"/> — ДОЛЯ (ISS YIELD в % ÷ 100),
/// <see cref="DurationYears"/> — ГОДЫ (ISS DURATION в днях ÷ 365), <see cref="CloseValue"/> — значение индекса (пункты).
/// </summary>
public sealed record MarketIndexPoint(
    DateOnly TradeDate,
    decimal? CloseValue,
    decimal? YieldFraction,
    decimal? DurationYears);

namespace Bonds.Core.Models;

/// <summary>
/// Валюта как актив (юани и т.п., задача 55) — ОТДЕЛЬНЫЙ домен по образцу фондов: не лежит в instruments/positions/operations,
/// не участвует в облигационном движке. Справочник обновляется раз в сутки.
/// </summary>
public class Currency
{
    public ulong Id { get; set; }

    /// <summary>FIGI валютного инструмента T-Invest (уникален; тот же, что в <c>GetPortfolio</c> и операциях).</summary>
    public string Figi { get; set; } = string.Empty;

    /// <summary>ISO-код ВЕРХНИМ регистром ("CNY"); T-Invest отдаёт нижним ("cny") — нормализуется при записи (уникален).</summary>
    public string IsoCode { get; set; } = string.Empty;

    /// <summary>Тикер T-Invest, напр. "CNYRUB_TOM".</summary>
    public string? Ticker { get; set; }

    public string? Name { get; set; }

    /// <summary>SECID на MOEX (доска CETS рынка selt) — для истории курса, напр. "CNYRUB_TOM".</summary>
    public string? MoexSecid { get; set; }

    /// <summary>Лот T-Invest (<c>Currency.Lot</c>) — справочно: количества в БД всегда в ЕДИНИЦАХ валюты, не лотах.</summary>
    public decimal? Lot { get; set; }

    /// <summary>
    /// MOEX FACEVALUE: за сколько единиц валюты MOEX котирует <c>CLOSE</c> (CNY = 1, JPY/KZT = 100). Рубли за 1 единицу =
    /// CLOSE ÷ FaceValue (<see cref="Bonds.Core.Currencies.CurrencyRateUnits.RubPerUnit"/>). По умолчанию 1.
    /// </summary>
    public decimal FaceValue { get; set; } = 1m;

    public DateTime CreatedAt { get; set; }

    /// <summary>Момент последнего обновления строки справочника (UTC, ON UPDATE CURRENT_TIMESTAMP) — ворота «раз в сутки».</summary>
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// Позиция в валюте на счёте. <see cref="Quantity"/> — ЕДИНИЦЫ валюты (юани), НЕ лоты. Цены — РУБЛИ ЗА ОДНУ ЕДИНИЦУ валюты
/// (как отдал брокер в GetPortfolio, конверсии нет).
/// </summary>
public class CurrencyPosition
{
    public ulong Id { get; set; }
    public ulong AccountId { get; set; }
    public ulong CurrencyId { get; set; }

    public decimal Quantity { get; set; }

    /// <summary>Средневзвешенная цена покупки, РУБЛИ за 1 единицу (GetPortfolio.AveragePositionPrice); null — брокер не отдал.</summary>
    public decimal? AvgPurchasePriceRub { get; set; }

    /// <summary>Текущий курс по оценке брокера, РУБЛИ за 1 единицу (GetPortfolio.CurrentPrice).</summary>
    public decimal? CurrentPriceRub { get; set; }

    public DateTime? PriceAsOf { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>Операция с валютой. <see cref="AmountRub"/> — РУБЛИ со знаком брокера (покупка −, продажа +), не переписывать.</summary>
public class CurrencyOperation
{
    public ulong Id { get; set; }
    public ulong AccountId { get; set; }

    /// <summary>Null — FIGI валюты не удалось резолвить; операция сохраняется, чтобы не терять деньги.</summary>
    public ulong? CurrencyId { get; set; }

    public CurrencyOperationType Type { get; set; }
    public DateTime Date { get; set; }

    /// <summary>Сумма, РУБЛИ, со знаком брокера (после проверки валюты платежа — не-рублёвые платежи сюда не попадают как рубли).</summary>
    public decimal AmountRub { get; set; }

    /// <summary>Количество в ЕДИНИЦАХ валюты (где применимо).</summary>
    public decimal? Quantity { get; set; }

    /// <summary>Идентификатор операции T-Invest — ключ идемпотентности.</summary>
    public string ExternalId { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
}

public enum CurrencyOperationType
{
    Buy,
    Sell,
    Tax,
    Fee,

    /// <summary>Проценты на остаток валюты — только если брокер их отдаёт отдельной операцией по валюте (сейчас не мапится).</summary>
    Income,
}

/// <summary>Закрытие курса за торговый день. <see cref="CloseRub"/> — РУБЛИ ЗА 1 ЕДИНИЦУ валюты (уже поделено на FACEVALUE).</summary>
public sealed record CurrencyRatePoint(DateOnly TradeDate, decimal CloseRub);

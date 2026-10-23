namespace Bonds.Core.Models;

/// <summary>Идентификаторы брокеров в <c>accounts.broker</c> (задача 60).</summary>
public static class Brokers
{
    /// <summary>Основной счёт продукта (облигации/фонды/валюта в рублях). Значение по умолчанию колонки.</summary>
    public const string TInvest = "tinvest";

    /// <summary>Счёт Freedom (Tradernet): ETF и акции в USD/EUR/GBP, отдельный домен <c>broker_*</c>.</summary>
    public const string Freedom = "freedom";
}

/// <summary>Класс актива позиции брокера (<c>broker_positions.asset_class</c>).</summary>
public static class BrokerAssetClass
{
    public const string Etf = "etf";
    public const string Stock = "stock";
    public const string Bond = "bond";
    public const string Fund = "fund";
    public const string Other = "other";
}

/// <summary>
/// Позиция на не-T-Invest счёте (Freedom). ВСЕ цены и суммы — в ВАЛЮТЕ ПОЗИЦИИ (<see cref="Currency"/>), НЕ в рублях:
/// пересчёт в рубли — только на чтении по курсам bonds.
/// </summary>
public class BrokerPosition
{
    public ulong Id { get; set; }
    public ulong AccountId { get; set; }
    public string Ticker { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>См. <see cref="BrokerAssetClass"/>. Эвристика по названию (брокер класс не отдаёт).</summary>
    public string AssetClass { get; set; } = BrokerAssetClass.Other;

    /// <summary>ISO-код валюты бумаги ВЕРХНИМ регистром (RUR брокера нормализован в RUB).</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>Штуки.</summary>
    public decimal Quantity { get; set; }

    /// <summary>Средняя цена входа за 1 бумагу, в валюте позиции.</summary>
    public decimal AvgPrice { get; set; }

    /// <summary>Текущая цена за 1 бумагу, в валюте позиции.</summary>
    public decimal LastPrice { get; set; }

    /// <summary>Стоимость позиции по оценке брокера, в валюте позиции.</summary>
    public decimal MarketValue { get; set; }

    public DateTime SyncedAt { get; set; }
}

/// <summary>Остаток денег счёта в одной валюте (в СВОЕЙ валюте, не в рублях).</summary>
public class BrokerCash
{
    public ulong Id { get; set; }
    public ulong AccountId { get; set; }

    /// <summary>ISO-код валюты ВЕРХНИМ регистром (RUR → RUB).</summary>
    public string Currency { get; set; } = string.Empty;

    public decimal Amount { get; set; }
    public DateTime SyncedAt { get; set; }
}

public enum BrokerOperationType
{
    Buy,
    Sell,
    Deposit,
    Withdrawal,
    Fee,
    Dividend,
    Tax,
    Other,
}

/// <summary>
/// Операция на не-T-Invest счёте. <see cref="Amount"/> — в <see cref="Currency"/> СО ЗНАКОМ: плюс — деньги пришли на счёт,
/// минус — ушли (покупка, комиссия, налог, вывод). Идемпотентность — по (account_id, <see cref="ExternalId"/>).
/// </summary>
public class BrokerOperation
{
    public ulong Id { get; set; }
    public ulong AccountId { get; set; }
    public string ExternalId { get; set; } = string.Empty;
    public DateTime Date { get; set; }
    public BrokerOperationType Type { get; set; }
    public string? Ticker { get; set; }
    public decimal? Quantity { get; set; }

    /// <summary>Цена за 1 бумагу в валюте сделки (только Buy/Sell/fx).</summary>
    public decimal? Price { get; set; }

    public decimal Amount { get; set; }
    public string Currency { get; set; } = string.Empty;

    /// <summary>Исходный тип брокера (<c>type_code</c> движения денег, "trade" для сделки, "fx" для конвертации валют).</summary>
    public string RawType { get; set; } = string.Empty;

    public string? Comment { get; set; }
}

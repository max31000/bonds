namespace Bonds.Core.Models;

/// <summary>Тип движения денег по счёту (<c>account_cash_movements.type</c>).</summary>
public enum CashMovementType
{
    Deposit,
    Withdrawal,
}

/// <summary>
/// Пополнение или вывод денег по счёту T-Invest (задача 61). <see cref="Amount"/> — в СВОЕЙ валюте <see cref="Currency"/> СО ЗНАКОМ брокера
/// (плюс — пополнение, минус — вывод); <see cref="AmountRub"/> — та же сумма в РУБЛЯХ (рубль как есть, иная валюта — по курсу дня bonds),
/// null — курса на дату не нашлось. Идемпотентность — UNIQUE (account_id, external_id).
/// </summary>
public class AccountCashMovement
{
    public ulong Id { get; set; }
    public ulong AccountId { get; set; }
    public string ExternalId { get; set; } = string.Empty;
    public DateTime Date { get; set; }
    public CashMovementType Type { get; set; }
    public decimal? AmountRub { get; set; }

    /// <summary>ISO-код ВЕРХНИМ регистром (RUR нормализован в RUB).</summary>
    public string Currency { get; set; } = string.Empty;

    public decimal Amount { get; set; }
}

/// <summary>
/// Как и чем торгуется бумага у брокера (задача 61, <c>instrument_trading_meta</c>). Общая для всех пользователей: свойства бумаги, не счёта.
/// <para>
/// <b>Единицы.</b> <see cref="MinPriceIncrement"/> — шаг цены в ВАЛЮТЕ ЦЕНЫ за 1 бумагу (для облигаций T-Invest — УЖЕ в рублях:
/// <c>пункты / 100 × номинал</c>, см. <see cref="Bonds.Core.Trading.BondPriceStep"/>); <see cref="MinPriceIncrementPoints"/> — сырой шаг
/// в ПУНКТАХ (процент номинала), только у облигаций (сетка цен облигации — в пунктах, заявки по облигациям округлять в них). <see cref="Lot"/> — штук в лоте.
/// </para>
/// </summary>
public class InstrumentTradingMeta
{
    public ulong Id { get; set; }
    public string Broker { get; set; } = string.Empty;

    /// <summary>ISIN, иначе тикер.</summary>
    public string InstrumentKey { get; set; } = string.Empty;

    /// <summary>T-Invest instrument uid.</summary>
    public string? Uid { get; set; }

    public string? Figi { get; set; }
    public string Ticker { get; set; } = string.Empty;
    public string? ClassCode { get; set; }
    public decimal Lot { get; set; }
    /// <summary>Шаг цены в валюте цены за бумагу; у облигации T-Invest с нерублёвым номиналом — null (см. <see cref="MinPriceIncrementPoints"/>).</summary>
    public decimal? MinPriceIncrement { get; set; }
    public decimal? MinPriceIncrementPoints { get; set; }
    public string Currency { get; set; } = string.Empty;
    public bool ApiTradeAvailable { get; set; }
    public bool BuyAvailable { get; set; }
    public bool SellAvailable { get; set; }

    /// <summary>Фонды: были ли выплаты за последние 12 месяцев. Null — неизвестно или не фонд.</summary>
    public bool? PaysDistributions { get; set; }

    /// <summary>Freedom: существует котировочный двойник <c>base.ITS</c>. Null — не проверялось.</summary>
    public bool? ItsTwin { get; set; }

    public DateTime UpdatedAt { get; set; }
}

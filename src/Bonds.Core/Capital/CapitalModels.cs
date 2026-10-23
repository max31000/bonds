using Bonds.Core.Models;
using BrokerIds = Bonds.Core.Models.Brokers;

namespace Bonds.Core.Capital;

/// <summary>Идентификаторы двух самостоятельных портфелей (<c>baskets.portfolio</c>, задача 62).</summary>
public static class CapitalPortfolios
{
    /// <summary>T-Invest: облигации, фонды, рубли и валюта T-Invest. Валюта портфеля — РУБЛЬ.</summary>
    public const string Rub = "rub";

    /// <summary>Freedom: акции и ETF. Валюта портфеля — ДОЛЛАР США.</summary>
    public const string Usd = "usd";

    public static readonly IReadOnlyList<string> All = [Rub, Usd];

    /// <summary>ISO-код валюты, в которой измеряется портфель.</summary>
    public static string CurrencyOf(string portfolio) => portfolio == Usd ? "USD" : "RUB";

    /// <summary>Брокер, чьи позиции составляют портфель (<c>accounts.broker</c>).</summary>
    public static string BrokerOf(string portfolio) => portfolio == Usd ? BrokerIds.Freedom : BrokerIds.TInvest;
}

/// <summary>Правило наполнения корзины (<c>baskets.fill_rule</c>).</summary>
public static class BasketFillRules
{
    public const string Fixed = "fixed";
    public const string Engine = "engine";
    public const string Hold = "hold";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Fixed, Engine, Hold };
}

/// <summary>Как член корзины сопоставляется с позицией (<c>basket_members.match_rule</c>).</summary>
public static class BasketMatchRules
{
    /// <summary>Конкретная бумага: <c>instrument_key</c> = ISIN или тикер.</summary>
    public const string Instrument = "instrument";

    /// <summary>Все бумаги класса, не попавшие явным членом в другие корзины: <c>instrument_key</c> = класс актива (<see cref="CapitalAssetClasses"/>).</summary>
    public const string AssetClass = "asset_class";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Instrument, AssetClass };
}

/// <summary>Классы активов позиций для правила <see cref="BasketMatchRules.AssetClass"/> (Freedom — <see cref="BrokerAssetClass"/>: etf/stock/…).</summary>
public static class CapitalAssetClasses
{
    public const string Bond = "bond";
    public const string Fund = "fund";
    public const string Currency = "currency";
}

/// <summary>Коды системных (виртуальных, не хранимых) корзин: сюда попадает то, что не попало ни в одну пользовательскую.</summary>
public static class SystemBaskets
{
    /// <summary>Позиция без явного члена и без подходящего правила класса. Вне целей (target = null).</summary>
    public const string Unassigned = "unassigned";

    /// <summary>Валютные позиции T-Invest (юани и т.п.), портфель rub: не облигации и не фонды, цели не имеют. Вне целей.</summary>
    public const string Fx = "fx";

    /// <summary>Свободные деньги портфеля. Единственная системная строка, которая ВХОДИТ в базу доли.</summary>
    public const string Cash = "cash";
}

/// <summary>Член корзины. <see cref="SubWeight"/> — ДОЛЯ внутри корзины (для fixed), null — не задана.</summary>
public sealed class CapitalBasketMember
{
    public ulong Id { get; set; }
    public ulong BasketId { get; set; }
    public string Broker { get; set; } = string.Empty;

    /// <summary>ISIN или тикер; для <see cref="BasketMatchRules.AssetClass"/> — класс актива.</summary>
    public string InstrumentKey { get; set; } = string.Empty;

    public decimal? SubWeight { get; set; }
    public string MatchRule { get; set; } = BasketMatchRules.Instrument;
}

/// <summary>
/// Корзина целевой структуры капитала. <b>Единицы:</b> <see cref="TargetWeight"/>, <see cref="Band"/>, <see cref="HardBand"/> — ДОЛИ
/// (0.15 = 15%, band — абсолютное отклонение доли портфеля); <see cref="MinTrade"/> — в валюте портфеля.
/// </summary>
public sealed class CapitalBasket
{
    public ulong Id { get; set; }
    public ulong UserId { get; set; }

    /// <summary>См. <see cref="CapitalPortfolios"/>.</summary>
    public string Portfolio { get; set; } = CapitalPortfolios.Rub;

    /// <summary>Уникален в пределах пользователя (VARCHAR(32)).</summary>
    public string Code { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    /// <summary>ДОЛЯ от своего портфеля; null — вне целей (temporary/hold): не входит в базу доли.</summary>
    public decimal? TargetWeight { get; set; }

    public decimal Band { get; set; }
    public decimal HardBand { get; set; }

    /// <summary>0..3 (0 — самая ликвидная).</summary>
    public int LiquidityTier { get; set; }

    /// <summary>См. <see cref="BasketFillRules"/>.</summary>
    public string FillRule { get; set; } = BasketFillRules.Hold;

    public bool AutoBuy { get; set; }
    public bool AutoSell { get; set; }

    /// <summary>Минимальная сделка, в валюте портфеля.</summary>
    public decimal MinTrade { get; set; }

    public int SortOrder { get; set; }

    public List<CapitalBasketMember> Members { get; set; } = [];
}

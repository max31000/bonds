using Bonds.Core.Models;
using BrokerIds = Bonds.Core.Models.Brokers;

namespace Bonds.Core.Capital;

/// <summary>
/// Стартовый набор корзин (задача 62, дизайн §3). Это ДАННЫЕ ПОЛЬЗОВАТЕЛЯ, поэтому сидится сервисом при первом чтении (а не миграцией);
/// цели стартовые, пользователь правит их в UI. Все веса — ДОЛИ.
/// <para>
/// Допущения (план не задаёт): полосы там, где не указаны, band 0.05 / hard 0.10; <c>ru_equity</c> — band 0.02, hard 0.05;
/// цели usd-портфеля: у <c>global_core</c> 0.6 по плану, остальные без цели → Σ целей usd = 0.6 ≠ 1 (см. <see cref="BasketValidation"/>).
/// </para>
/// </summary>
public static class DefaultBaskets
{
    private const decimal DefaultBand = 0.05m;
    private const decimal DefaultHardBand = 0.10m;

    public static IReadOnlyList<CapitalBasket> For(ulong userId, string portfolio) =>
        portfolio == CapitalPortfolios.Usd ? Usd(userId) : Rub(userId);

    public static IReadOnlyList<CapitalBasket> All(ulong userId) => [.. Rub(userId), .. Usd(userId)];

    private static List<CapitalBasket> Rub(ulong userId) =>
    [
        Basket(userId, CapitalPortfolios.Rub, "liquidity", "Ликвидность", 0.15m, DefaultBand, DefaultHardBand, 0, BasketFillRules.Fixed, 10,
            autoBuy: true, autoSell: true,
            Member(BrokerIds.TInvest, "TMON@", 0.5m), Member(BrokerIds.TInvest, "TSAV", 0.5m)),
        Basket(userId, CapitalPortfolios.Rub, "bonds", "Облигации", 0.85m, DefaultBand, DefaultHardBand, 2, BasketFillRules.Engine, 20,
            autoBuy: false, autoSell: false,
            Member(BrokerIds.TInvest, CapitalAssetClasses.Bond, null, BasketMatchRules.AssetClass)),
        Basket(userId, CapitalPortfolios.Rub, "ru_equity", "Российские акции", 0m, 0.02m, 0.05m, 1, BasketFillRules.Fixed, 30,
            autoBuy: false, autoSell: false),
        Basket(userId, CapitalPortfolios.Rub, "temporary", "Временные", null, DefaultBand, DefaultHardBand, 1, BasketFillRules.Hold, 40,
            autoBuy: false, autoSell: false,
            Member(BrokerIds.TInvest, "TPAY"), Member(BrokerIds.TInvest, "TOFZ@")),
    ];

    private static List<CapitalBasket> Usd(ulong userId) =>
    [
        Basket(userId, CapitalPortfolios.Usd, "global_core", "Глобальное ядро", 0.6m, DefaultBand, DefaultHardBand, 1, BasketFillRules.Fixed, 110,
            autoBuy: false, autoSell: false,
            Member(BrokerIds.Freedom, "VUAA.EU", 0.8m), Member(BrokerIds.Freedom, "SPYM.US", 0.2m)),
        Basket(userId, CapitalPortfolios.Usd, "satellites", "Спутники", null, DefaultBand, DefaultHardBand, 2, BasketFillRules.Hold, 120,
            autoBuy: false, autoSell: false,
            Member(BrokerIds.Freedom, "LUV"), Member(BrokerIds.Freedom, "UFO"), Member(BrokerIds.Freedom, "ROBO"),
            Member(BrokerIds.Freedom, "SJPA"), Member(BrokerIds.Freedom, "LGQM"), Member(BrokerIds.Freedom, "OTGLY")),
        Basket(userId, CapitalPortfolios.Usd, "frozen", "Заморожено", null, DefaultBand, DefaultHardBand, 3, BasketFillRules.Hold, 130,
            autoBuy: false, autoSell: false,
            Member(BrokerIds.Freedom, "FNMA"), Member(BrokerIds.Freedom, "FMCC")),
    ];

    private static CapitalBasket Basket(
        ulong userId, string portfolio, string code, string title, decimal? target, decimal band, decimal hardBand, int tier, string fillRule,
        int sortOrder, bool autoBuy, bool autoSell, params CapitalBasketMember[] members) => new()
    {
        UserId = userId, Portfolio = portfolio, Code = code, Title = title, TargetWeight = target, Band = band, HardBand = hardBand,
        LiquidityTier = tier, FillRule = fillRule, AutoBuy = autoBuy, AutoSell = autoSell, MinTrade = 0m, SortOrder = sortOrder,
        Members = [.. members],
    };

    private static CapitalBasketMember Member(string broker, string key, decimal? sub = null, string rule = BasketMatchRules.Instrument) =>
        new() { Broker = broker, InstrumentKey = key, SubWeight = sub, MatchRule = rule };
}

using Bonds.Core.Capital;
using Bonds.Core.Models;
using Bonds.Core.Policy;
using BrokerIds = Bonds.Core.Models.Brokers;

namespace Bonds.Tests.Policy;

/// <summary>Сборщик входа правил для юнит-тестов (задача 63): корзины, котировки, держания. Время — относительно сегодня (среда, в окне торгов Мосбиржи).</summary>
internal static class PolicyTestKit
{
    public const ulong AccountId = 7;

    /// <summary>Ближайшая прошедшая среда 12:00 МСК (09:00 UTC) — Мосбиржа открыта; относительно «сегодня», без абсолютных дат.</summary>
    public static DateTime OpenNowUtc()
    {
        var d = DateTime.UtcNow.Date;
        while (d.DayOfWeek != DayOfWeek.Wednesday || d > DateTime.UtcNow.Date) d = d.AddDays(-1);
        return d.AddHours(9);
    }

    /// <summary>Та же среда, 23:00 МСК (20:00 UTC) — Мосбиржа закрыта.</summary>
    public static DateTime ClosedNowUtc() => OpenNowUtc().AddHours(11);

    private static ulong _id;

    public static CapitalBasket Basket(
        string code, decimal? target, string portfolio = CapitalPortfolios.Rub, int tier = 0, string fill = BasketFillRules.Fixed,
        bool autoBuy = false, bool autoSell = false, decimal band = 0.05m, decimal hard = 0.10m, params CapitalBasketMember[] members) => new()
    {
        Id = ++_id, UserId = 1, Portfolio = portfolio, Code = code, Title = code, TargetWeight = target, Band = band, HardBand = hard,
        LiquidityTier = tier, FillRule = fill, AutoBuy = autoBuy, AutoSell = autoSell, SortOrder = (int)_id, Members = [.. members],
    };

    public static CapitalBasketMember Member(string key, decimal? sub = null, string broker = BrokerIds.TInvest) =>
        new() { Broker = broker, InstrumentKey = key, SubWeight = sub, MatchRule = BasketMatchRules.Instrument };

    public static CapitalBasketMember ClassMember(string cls) =>
        new() { Broker = BrokerIds.TInvest, InstrumentKey = cls, MatchRule = BasketMatchRules.AssetClass };

    /// <summary>Фонд T-Invest: цена bid/ask, шаг 0.01, лот 1; по умолчанию без выплат (<paramref name="pays"/> = false — проходит белый список авто, задача 67).</summary>
    public static PolicyQuote Fund(string ticker, decimal bid, decimal ask, decimal step = 0.01m, decimal lot = 1m, bool known = true, bool? pays = false) => new()
    {
        Broker = BrokerIds.TInvest, AccountId = AccountId, InstrumentKey = ticker, Ticker = ticker, Name = ticker, Kind = PolicyAssetKind.Fund,
        Market = PolicyMarket.Moex, Currency = "RUB", Lot = lot, PriceStep = step, Bid = bid, Ask = ask, KnownCommission = known, PaysDistributions = pays,
    };

    /// <summary>Облигация T-Invest: цена в ПУНКТАХ, шаг в пунктах, номинал 1000 ₽, НКД на штуку.</summary>
    public static PolicyQuote Bond(string isin, decimal bidPts, decimal askPts, decimal stepPts = 0.01m, decimal accrued = 10m, decimal nominal = 1000m, decimal lot = 1m) => new()
    {
        Broker = BrokerIds.TInvest, AccountId = AccountId, InstrumentKey = isin, Ticker = isin, Name = isin, Kind = PolicyAssetKind.Bond,
        Market = PolicyMarket.Moex, Currency = "RUB", Lot = lot, PriceStepPoints = stepPts, NominalRub = nominal, Bid = bidPts, Ask = askPts, AccruedPerPiece = accrued,
    };

    public static PolicyQuote FreedomEtf(string ticker, decimal bid, decimal ask) => new()
    {
        Broker = BrokerIds.Freedom, AccountId = 9, InstrumentKey = ticker, Ticker = ticker, Name = ticker, Kind = PolicyAssetKind.Etf,
        Market = TradingHours.MarketOf(BrokerIds.Freedom, ticker, null), Currency = "USD", Lot = 1m, PriceStep = 0.01m, Bid = bid, Ask = ask,
    };

    public static PolicyHolding Holding(string ticker, decimal qty, decimal value, decimal? avgCost, string basket, bool unknownCost = false) => new()
    {
        Broker = BrokerIds.TInvest, InstrumentKey = ticker, Ticker = ticker, QuantityPieces = qty, MarketValue = value, AvgCostPerPiece = avgCost,
        HasUnknownCost = unknownCost, BasketCode = basket,
    };

    public static PolicyBondCandidate Candidate(string secid, decimal afterTax, decimal yield, string sector = "Корпоративные", decimal bidPts = 99.9m, decimal askPts = 100.1m) => new()
    {
        Secid = secid, Isin = secid, Name = secid, Sector = sector, YieldFraction = yield, AfterTaxYieldFraction = afterTax,
        CleanPriceRub = 1000m, AccruedRub = 10m, IssuerKey = secid, Quote = Bond(secid, bidPts, askPts),
        IssuerRatingAtLeastA = true, // задача 67: P3b берёт только эмитентов со свежим рейтингом не ниже A
    };

    public static PolicyInput Input(
        IReadOnlyList<CapitalBasket> baskets, IReadOnlyList<CapitalPosition> positions, decimal freeCash, IReadOnlyList<PolicyQuote> quotes,
        IReadOnlyList<PolicyHolding>? holdings = null, IReadOnlyList<PolicyBondCandidate>? candidates = null, decimal feeRate = 0m,
        DateTime? now = null, bool ignoreHours = false, string portfolio = CapitalPortfolios.Rub, ExecutionLimitsOptions? limits = null,
        decimal autoToday = 0m, decimal? fundYield = null)
    {
        var broker = CapitalPortfolios.BrokerOf(portfolio);
        return new PolicyInput
        {
            NowUtc = now ?? OpenNowUtc(),
            Portfolio = portfolio,
            Broker = broker,
            AccountId = portfolio == CapitalPortfolios.Rub ? AccountId : 9,
            Currency = CapitalPortfolios.CurrencyOf(portfolio),
            FreeCash = freeCash,
            AutoApprovedTodayRub = autoToday,
            Baskets = baskets,
            Drift = BasketDriftService.Calculate(baskets, positions, freeCash),
            Quotes = quotes,
            Holdings = holdings ?? [],
            BondCandidates = candidates ?? [],
            Fees = new PlanFeeModel(feeRate, PlanFeeModel.DefaultFreedomTariffId),
            Options = new PolicyOptions(),
            Limits = limits ?? new ExecutionLimitsOptions(),
            IgnoreTradingHours = ignoreHours,
            LiquidityFundAfterTaxYield = fundYield,
            BondsPortfolioValueRub = positions.Where(p => p.AssetClass == CapitalAssetClasses.Bond).Sum(p => p.Value),
        };
    }

    public static CapitalPosition Pos(string key, string cls, decimal value, string broker = BrokerIds.TInvest) => new(broker, key, cls, key, value);
}

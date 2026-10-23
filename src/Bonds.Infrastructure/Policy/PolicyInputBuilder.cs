using System.Text.Json;
using Bonds.Core.Analytics;
using Bonds.Core.Brokers;
using Bonds.Core.Capital;
using Bonds.Core.Credentials;
using Bonds.Core.Funds;
using Bonds.Core.Interfaces;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Bonds.Core.Policy;
using Bonds.Core.Time;
using Bonds.Infrastructure.Analytics;
using Bonds.Infrastructure.Sync;
using Microsoft.Extensions.Options;
using BrokerIds = Bonds.Core.Models.Brokers;

namespace Bonds.Infrastructure.Policy;

/// <summary>
/// Собирает <see cref="PolicyInput"/> по портфелям пользователя из существующих точек истины (корзины и отклонения — <see cref="CapitalOverviewBuilder"/>,
/// деньги, метаданные торгов, стаканы, позиции со средней ценой входа, комиссия, лимиты). ТОЛЬКО ЧТЕНИЕ: рыночные данные и БД. Заявок нет.
/// <para>
/// <b>Единицы и границы.</b> Деньги портфеля — в его валюте (rub — рубли, usd — доллары); цены стакана — в единицах брокера (облигации T-Invest — пункты);
/// множитель валюты цены → валюты портфеля — курсы bonds (<see cref="ICurrencyRateLookup"/>). Номинал облигации в рублях восстанавливается из метаданных:
/// рублёвый шаг ÷ шаг в пунктах × 100 (у облигаций с нерублёвым номиналом рублёвого шага нет — номинал null, рубли берутся из последней известной цены).
/// Лимит авто-покупок «за день» считается по МОСКОВСКИМ суткам.
/// </para>
/// </summary>
public sealed class PolicyInputBuilder
{
    private readonly CapitalOverviewBuilder _capital;
    private readonly IAccountRepository _accounts;
    private readonly IInstrumentTradingMetaRepository _meta;
    private readonly IPlanQuoteSource _quotes;
    private readonly PortfolioHoldingsBuilder _holdings;
    private readonly IFundPositionRepository _fundPositions;
    private readonly IFundRepository _funds;
    private readonly IFundOperationRepository _fundOperations;
    private readonly IBrokerPositionRepository _brokerPositions;
    private readonly ICurrencyRateLookup _rates;
    private readonly ICommissionRateProvider _commission;
    private readonly IUserCredentialStore _credentials;
    private readonly ITradePlanRepository _plans;
    private readonly IUserSettingsRepository _settings;
    private readonly FundOverviewBuilder _fundOverview;
    private readonly PolicyBondCandidateProvider _candidates;
    private readonly PolicyOptions _options;
    private readonly ExecutionLimitsOptions _limits;

    public PolicyInputBuilder(
        CapitalOverviewBuilder capital,
        IAccountRepository accounts,
        IInstrumentTradingMetaRepository meta,
        IPlanQuoteSource quotes,
        PortfolioHoldingsBuilder holdings,
        IFundPositionRepository fundPositions,
        IFundRepository funds,
        IFundOperationRepository fundOperations,
        IBrokerPositionRepository brokerPositions,
        ICurrencyRateLookup rates,
        ICommissionRateProvider commission,
        IUserCredentialStore credentials,
        ITradePlanRepository plans,
        IUserSettingsRepository settings,
        FundOverviewBuilder fundOverview,
        PolicyBondCandidateProvider candidates,
        IOptions<PolicyOptions> options,
        IOptions<ExecutionLimitsOptions> limits)
    {
        _capital = capital;
        _accounts = accounts;
        _meta = meta;
        _quotes = quotes;
        _holdings = holdings;
        _fundPositions = fundPositions;
        _funds = funds;
        _fundOperations = fundOperations;
        _brokerPositions = brokerPositions;
        _rates = rates;
        _commission = commission;
        _credentials = credentials;
        _plans = plans;
        _settings = settings;
        _fundOverview = fundOverview;
        _candidates = candidates;
        _options = options.Value;
        _limits = limits.Value;
    }

    /// <summary>Облигации портфеля rub, найденные при сборке (нужны подбору кандидатов для лимита эмитента); пусто для usd.</summary>
    private sealed record Built(PolicyInput Input, IReadOnlyList<HeldBondValue> HeldBonds);

    /// <summary>Входы по портфелям пользователя, у которых есть счёт брокера (ключ — <see cref="CapitalPortfolios"/>).</summary>
    public async Task<IReadOnlyDictionary<string, PolicyInput>> BuildAsync(ulong userId, DateTime nowUtc, bool ignoreHours, CancellationToken ct = default)
    {
        var asOf = BusinessClock.MoscowDate(nowUtc);
        var overview = await _capital.BuildAsync(userId, asOf, ct);
        var result = new Dictionary<string, PolicyInput>(StringComparer.Ordinal);
        foreach (var portfolio in overview.Where(p => p.HasAccount))
        {
            ct.ThrowIfCancellationRequested();
            var built = await BuildPortfolioAsync(userId, portfolio, asOf, nowUtc, ignoreHours, ct);
            if (built is not null) result[portfolio.Portfolio] = built.Input;
        }

        return result;
    }

    /// <summary>
    /// Дополняет вход данными, нужными только облигационным правилам (дорого, поэтому по требованию): кандидаты со стаканами, ожидаемая after-tax доходность
    /// фонда ликвидности (<see cref="FundOverviewBuilder"/>), база и лимит эмитента.
    /// </summary>
    public async Task<PolicyInput> WithBondDataAsync(ulong userId, PolicyInput input, CancellationToken ct = default)
    {
        if (input.Portfolio != CapitalPortfolios.Rub) return input;

        var asOf = BusinessClock.MoscowDate(input.NowUtc);
        var held = new List<HeldBondValue>();
        foreach (var h in await _holdings.BuildForAccountAsync(input.AccountId, asOf, ct))
        {
            if (h.Quantity > 0m && !string.IsNullOrWhiteSpace(h.Isin)) held.Add(new HeldBondValue(h.Isin!, h.MarketValueRub));
        }

        var candidates = await _candidates.GetAsync(input.AccountId, held, input.NowUtc, ct);
        var fundYield = await LiquidityFundYieldAsync(input, asOf, ct);
        var settings = await _settings.GetByUserIdAsync(userId);
        return input with
        {
            BondCandidates = candidates,
            LiquidityFundAfterTaxYield = fundYield,
            BondsPortfolioValueRub = held.Sum(h => h.MarketValueRub),
            MaxIssuerSharePercent = settings?.DefaultMaxConcentrationPercent ?? CashAllocationService.DefaultMaxIssuerSharePercent,
        };
    }

    /// <summary>
    /// Ожидаемая after-tax доходность фонда ликвидности: взвешенная по рыночной стоимости доходность фондов корзины <c>liquidity</c>, по которым есть оценка
    /// (<see cref="FundOutlookService"/>: выплаты / индекс / фактическая, после налога НДФЛ). Нет позиций или оценки — null.
    /// </summary>
    private async Task<decimal?> LiquidityFundYieldAsync(PolicyInput input, DateOnly asOf, CancellationToken ct)
    {
        var liquidity = input.BasketByCode("liquidity");
        if (liquidity is null) return null;

        var items = await _fundOverview.BuildAsync(input.AccountId, asOf, openOnly: true, ct: ct);
        decimal weighted = 0m, weight = 0m;
        foreach (var item in items)
        {
            var key = item.Fund.Ticker ?? item.Fund.Isin;
            var member = liquidity.Members.Any(m => BasketDriftService.KeyMatches(m.InstrumentKey, BasketDriftService.NormalizeKey(key)));
            if (!member || item.Outlook.AfterTaxYieldFraction is not { } yield) continue;

            var value = item.Income.Result?.Income.MarketValueCleanRub ?? 0m;
            if (value <= 0m) continue;
            weighted += yield * value;
            weight += value;
        }

        return weight > 0m ? weighted / weight : null;
    }

    private async Task<Built?> BuildPortfolioAsync(
        ulong userId, CapitalPortfolioOverview portfolio, DateOnly asOf, DateTime nowUtc, bool ignoreHours, CancellationToken ct)
    {
        var broker = CapitalPortfolios.BrokerOf(portfolio.Portfolio);
        var account = (await _accounts.GetByUserIdAndBrokerAsync(userId, broker)).FirstOrDefault();
        if (account is null) return null;

        var currency = CapitalPortfolios.CurrencyOf(portfolio.Portfolio);
        var isRub = portfolio.Portfolio == CapitalPortfolios.Rub;
        var metas = await _meta.GetByBrokerAsync(broker);

        // Позиции портфеля с количествами и средней ценой входа (для продаж и налога), привязанные к корзинам по дрейфу.
        var held = isRub ? await BuildRubHoldingsAsync(account, portfolio, asOf, ct) : await BuildUsdHoldingsAsync(account, portfolio, ct);

        // Какие бумаги нужны: члены корзин + позиции + тикеры доливки Freedom.
        var keys = new List<string>();
        foreach (var basket in portfolio.Baskets)
            keys.AddRange(basket.Members.Where(m => m.MatchRule == BasketMatchRules.Instrument).Select(m => m.InstrumentKey));
        keys.AddRange(held.Holdings.Select(h => h.InstrumentKey));
        if (!isRub) keys.AddRange([_options.FreedomSmallTopUpTicker, _options.FreedomLargeTopUpTicker]);

        var selected = new List<InstrumentTradingMeta>();
        foreach (var key in keys.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var meta = FindMeta(metas, key);
            if (meta is not null && selected.All(s => s.Id != meta.Id)) selected.Add(meta);
        }

        IReadOnlyDictionary<string, BookQuote> books = isRub
            ? await _quotes.GetTInvestAsync(selected.Where(m => !string.IsNullOrWhiteSpace(m.Figi)).Select(m => m.Figi!).Distinct().ToList(), ct)
            : await _quotes.GetFreedomAsync(userId, selected.Select(m => m.Ticker).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), ct);

        var knownCommission = isRub ? await KnownFundCommissionsAsync(account.Id, ct) : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var quotes = new List<PolicyQuote>();
        foreach (var meta in selected)
        {
            var book = isRub
                ? (!string.IsNullOrWhiteSpace(meta.Figi) && books.TryGetValue(meta.Figi!, out var tb) ? tb : null)
                : (books.TryGetValue(meta.Ticker.ToUpperInvariant(), out var fb) ? fb : null);
            var holding = held.Holdings.FirstOrDefault(h => BasketDriftService.KeyMatches(h.InstrumentKey, BasketDriftService.NormalizeKey(meta.InstrumentKey))
                                                            || BasketDriftService.KeyMatches(h.InstrumentKey, BasketDriftService.NormalizeKey(meta.Ticker)));
            var quote = await ToQuoteAsync(account, meta, book, holding, held, currency, asOf, knownCommission, ct);
            if (quote is not null) quotes.Add(quote);
        }

        // Деньги: свободные минус резерв под открытые планы; лимит суток по МОСКОВСКОЙ дате.
        var reserved = await _plans.SumReservedBuysAsync(userId, broker, currency, nowUtc);
        var mskDayStartUtc = DateTime.SpecifyKind(TradingHours.ToMoscow(nowUtc).Date - TimeSpan.FromHours(3), DateTimeKind.Utc);
        var autoToday = await _plans.SumAutoApprovedRubSinceAsync(userId, mskDayStartUtc);

        var input = new PolicyInput
        {
            NowUtc = nowUtc,
            Portfolio = portfolio.Portfolio,
            Broker = broker,
            AccountId = account.Id,
            Currency = currency,
            FreeCash = Math.Max(0m, portfolio.FreeCash - reserved),
            ReservedCash = reserved,
            AutoApprovedTodayRub = autoToday,
            Baskets = portfolio.Baskets,
            Drift = portfolio.Drift,
            Quotes = quotes,
            Holdings = held.Holdings,
            Fees = await FeeModelAsync(userId, account, ct),
            Options = _options,
            Limits = _limits,
            IgnoreTradingHours = ignoreHours,
        };
        return new Built(input, held.Bonds);
    }

    private sealed record HeldSet(List<PolicyHolding> Holdings, List<HeldBondValue> Bonds, Dictionary<string, (decimal AccruedPerPiece, decimal? CleanPerPiece)> BondPrices);

    /// <summary>Позиции T-Invest: облигации (ISIN, средняя цена из журнала) и фонды (тикер, средняя цена позиции). Стоимость — в рублях, как в оценке капитала.</summary>
    private async Task<HeldSet> BuildRubHoldingsAsync(Account account, CapitalPortfolioOverview portfolio, DateOnly asOf, CancellationToken ct)
    {
        var set = new HeldSet([], [], new(StringComparer.OrdinalIgnoreCase));
        var drift = portfolio.Drift;

        foreach (var h in await _holdings.BuildForAccountAsync(account.Id, asOf, ct))
        {
            if (h.Quantity <= 0m || string.IsNullOrWhiteSpace(h.Isin)) continue;
            var cost = h.CostBasis;
            set.Bonds.Add(new HeldBondValue(h.Isin!, h.MarketValueRub));
            set.BondPrices[h.Isin!] = (h.AccruedPerBondRub, h.Quantity > 0m ? h.MarketValueRub / h.Quantity - h.AccruedPerBondRub : null);
            set.Holdings.Add(new PolicyHolding
            {
                Broker = BrokerIds.TInvest,
                InstrumentKey = h.Isin!,
                Ticker = h.Isin!,
                Name = h.Name ?? h.Isin!,
                QuantityPieces = h.Quantity,
                MarketValue = h.MarketValueRub,
                AvgCostPerPiece = cost?.AverageCostRub,
                HasUnknownCost = cost is null || cost.AverageCostRub is null || cost.HasUnknownLots,
            });
        }

        foreach (var p in (await _fundPositions.GetByAccountAsync(account.Id)).Where(p => p.Quantity > 0m))
        {
            var fund = await _funds.GetByIdAsync(p.FundId);
            if (fund is null) continue;
            var key = string.IsNullOrWhiteSpace(fund.Ticker) ? fund.Isin : fund.Ticker!;
            var value = ValueOf(drift, key);
            set.Holdings.Add(new PolicyHolding
            {
                Broker = BrokerIds.TInvest,
                InstrumentKey = key,
                Ticker = key,
                Name = fund.Name ?? key,
                QuantityPieces = p.Quantity,
                MarketValue = value,
                AvgCostPerPiece = p.AvgPurchasePriceRub > 0m ? p.AvgPurchasePriceRub : null,
                HasUnknownCost = p.AvgPurchasePriceRub <= 0m,
            });
        }

        return Assign(set, portfolio);
    }

    /// <summary>Позиции Freedom (тикер, количество, средняя цена из <c>broker_positions</c>); стоимость — в долларах из оценки капитала.</summary>
    private async Task<HeldSet> BuildUsdHoldingsAsync(Account account, CapitalPortfolioOverview portfolio, CancellationToken ct)
    {
        var set = new HeldSet([], [], new(StringComparer.OrdinalIgnoreCase));
        foreach (var p in (await _brokerPositions.GetByAccountAsync(account.Id)).Where(p => p.Quantity > 0m))
        {
            ct.ThrowIfCancellationRequested();
            set.Holdings.Add(new PolicyHolding
            {
                Broker = BrokerIds.Freedom,
                InstrumentKey = p.Ticker,
                Ticker = p.Ticker,
                Name = p.Name,
                QuantityPieces = p.Quantity,
                MarketValue = ValueOf(portfolio.Drift, p.Ticker),
                AvgCostPerPiece = p.AvgPrice > 0m ? p.AvgPrice : null,
                HasUnknownCost = p.AvgPrice <= 0m,
            });
        }

        return Assign(set, portfolio);
    }

    private static decimal ValueOf(BasketDriftResult drift, string key)
    {
        var normalized = BasketDriftService.NormalizeKey(key);
        return drift.Rows.SelectMany(r => r.Positions).FirstOrDefault(p => BasketDriftService.NormalizeKey(p.Key) == normalized)?.Value ?? 0m;
    }

    /// <summary>Привязывает позиции к корзинам по тем же правилам, что дрейф (<see cref="BasketDriftService.Assign"/>), и проставляет тир ликвидности.</summary>
    private static HeldSet Assign(HeldSet set, CapitalPortfolioOverview portfolio)
    {
        var codeByKey = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in portfolio.Drift.Rows)
            foreach (var position in row.Positions) codeByKey[BasketDriftService.NormalizeKey(position.Key)] = row.Code;

        for (var i = 0; i < set.Holdings.Count; i++)
        {
            var h = set.Holdings[i];
            var code = codeByKey.GetValueOrDefault(BasketDriftService.NormalizeKey(h.InstrumentKey), SystemBaskets.Unassigned);
            var tier = portfolio.Baskets.FirstOrDefault(b => b.Code == code)?.LiquidityTier ?? 3;
            set.Holdings[i] = h with { BasketCode = code, LiquidityTier = tier };
        }

        return set;
    }

    private static InstrumentTradingMeta? FindMeta(IReadOnlyList<InstrumentTradingMeta> metas, string key) =>
        metas.FirstOrDefault(m => BasketDriftService.KeyMatches(key, BasketDriftService.NormalizeKey(m.InstrumentKey)))
        ?? metas.FirstOrDefault(m => BasketDriftService.KeyMatches(key, BasketDriftService.NormalizeKey(m.Ticker)));

    private async Task<PolicyQuote?> ToQuoteAsync(
        Account account, InstrumentTradingMeta meta, BookQuote? book, PolicyHolding? holding, HeldSet held, string portfolioCurrency, DateOnly asOf,
        HashSet<string> knownCommission, CancellationToken ct)
    {
        var currency = BrokerAssetClassifier.NormalizeCurrency(meta.Currency) ?? portfolioCurrency;
        var fx = await FxAsync(currency, portfolioCurrency, asOf, ct);
        if (fx is null) return null; // нет курса — оценить сумму нельзя, бумага не участвует (в плане причина «нет метаданных или стакана»)

        var isBond = meta.MinPriceIncrementPoints is > 0m && account.Broker == BrokerIds.TInvest;
        var kind = isBond
            ? PolicyAssetKind.Bond
            : account.Broker == BrokerIds.TInvest
                ? PolicyAssetKind.Fund
                : (holding is not null && BrokerAssetClassifier.Classify(holding.Name) == BrokerAssetClass.Stock ? PolicyAssetKind.Stock : PolicyAssetKind.Etf);

        decimal accrued = 0m;
        decimal? reference = null;
        if (isBond && held.BondPrices.TryGetValue(meta.InstrumentKey, out var prices))
        {
            accrued = prices.AccruedPerPiece;
            reference = prices.CleanPerPiece;
        }

        var tickerKey = BasketDriftService.NormalizeKey(meta.Ticker);
        var known = account.Broker != BrokerIds.TInvest
                    || isBond
                    || _options.OwnFunds.Any(f => BasketDriftService.NormalizeKey(f) == tickerKey)
                    || knownCommission.Contains(tickerKey);

        return new PolicyQuote
        {
            Broker = account.Broker,
            AccountId = account.Id,
            InstrumentKey = meta.InstrumentKey,
            Ticker = meta.Ticker,
            Name = holding?.Name ?? meta.Ticker,
            Kind = kind,
            Market = TradingHours.MarketOf(account.Broker, meta.Ticker, meta.ClassCode),
            Currency = currency,
            Lot = meta.Lot,
            PriceStep = isBond ? null : meta.MinPriceIncrement,
            PriceStepPoints = isBond ? meta.MinPriceIncrementPoints : null,
            NominalRub = isBond ? PolicyBondCandidateProvider.NominalRubOf(meta) : null,
            Bid = book?.Bid,
            Ask = book?.Ask,
            AccruedPerPiece = accrued,
            ReferenceCleanPricePerPiece = reference,
            FxToPortfolio = fx.Value,
            BuyAvailable = meta.BuyAvailable && meta.ApiTradeAvailable,
            SellAvailable = meta.SellAvailable && meta.ApiTradeAvailable,
            ItsTwin = meta.ItsTwin ?? false,
            KnownCommission = known,
        };
    }

    /// <summary>Множитель «валюта цены → валюта портфеля» через рубли за единицу валюты (рубль = 1); null — нет курса.</summary>
    private async Task<decimal?> FxAsync(string from, string to, DateOnly asOf, CancellationToken ct)
    {
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) return 1m;
        var fromRub = string.Equals(from, "RUB", StringComparison.OrdinalIgnoreCase) ? 1m : await _rates.GetRubPerUnitAsync(from, asOf, ct);
        var toRub = string.Equals(to, "RUB", StringComparison.OrdinalIgnoreCase) ? 1m : await _rates.GetRubPerUnitAsync(to, asOf, ct);
        return fromRub is > 0m && toRub is > 0m ? fromRub / toRub : null;
    }

    /// <summary>
    /// Сторонние (не своей управляющей компании) фонды T-Invest, по которым в журнале есть и сделка, и списанная комиссия, — «комиссия известна».
    /// Ключ — тикер без «@» ВЕРХНИМ регистром.
    /// </summary>
    private async Task<HashSet<string>> KnownFundCommissionsAsync(ulong accountId, CancellationToken ct)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var operations = await _fundOperations.GetByAccountAsync(accountId);
        foreach (var group in operations.Where(o => o.FundId is not null).GroupBy(o => o.FundId!.Value))
        {
            ct.ThrowIfCancellationRequested();
            if (!group.Any(o => o.Type is FundOperationType.Buy or FundOperationType.Sell) || !group.Any(o => o.Type == FundOperationType.Fee)) continue;
            var fund = await _funds.GetByIdAsync(group.Key);
            if (fund is not null) result.Add(BasketDriftService.NormalizeKey(string.IsNullOrWhiteSpace(fund.Ticker) ? fund.Isin : fund.Ticker!));
        }

        return result;
    }

    private async Task<PlanFeeModel> FeeModelAsync(ulong userId, Account account, CancellationToken ct)
    {
        if (account.Broker == BrokerIds.Freedom)
        {
            var statuses = await _credentials.ListStatusesAsync(userId);
            var tariff = PlanFeeModel.DefaultFreedomTariffId;
            var meta = statuses.FirstOrDefault(s => s.Provider == CredentialProviders.Freedom)?.MetaJson;
            if (!string.IsNullOrWhiteSpace(meta))
            {
                try
                {
                    using var doc = JsonDocument.Parse(meta);
                    if (doc.RootElement.TryGetProperty("tariffId", out var t))
                    {
                        if (t.ValueKind == JsonValueKind.Number && t.TryGetInt32(out var n)) tariff = n;
                        else if (t.ValueKind == JsonValueKind.String && int.TryParse(t.GetString(), out var s)) tariff = s;
                    }
                }
                catch (JsonException)
                {
                    // Метаданные повреждены — тариф по умолчанию (Standard); оценка комиссии приблизительная.
                }
            }

            if (tariff is not (FreedomFeeModelIds.Investor or FreedomFeeModelIds.Standard)) tariff = PlanFeeModel.DefaultFreedomTariffId;
            return new PlanFeeModel(0m, tariff);
        }

        var rate = await _commission.GetAsync(account.Id, ct);
        return new PlanFeeModel(rate.Rate, PlanFeeModel.DefaultFreedomTariffId);
    }
}

/// <summary>Идентификаторы тарифов Freedom, поддержанные моделью комиссий (<see cref="Bonds.Core.Fees.FreedomFeeModel"/>).</summary>
internal static class FreedomFeeModelIds
{
    public const int Investor = Bonds.Core.Fees.FreedomFeeModel.InvestorTariffId;
    public const int Standard = Bonds.Core.Fees.FreedomFeeModel.StandardTariffId;
}

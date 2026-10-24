using BrokerIds = Bonds.Core.Models.Brokers;
using Bonds.Core.Capital;
using Bonds.Core.Fees;
using Bonds.Core.Models;

namespace Bonds.Core.Policy;

/// <summary>Рынок, по часам которого торгуется бумага (<see cref="TradingHours"/>).</summary>
public enum PolicyMarket
{
    Moex,
    SpbRu,
    FreedomUs,
    FreedomEu,

    /// <summary>Часы не ограничиваем (неизвестная площадка).</summary>
    Unrestricted,
}

/// <summary>Вид бумаги для цены и комиссии.</summary>
public enum PolicyAssetKind
{
    Bond,
    Fund,
    Stock,
    Etf,
}

/// <summary>
/// Торгуемая бумага со стаканом (глубина 1) и параметрами торгов. <b>Единицы цены:</b> <see cref="Bid"/>/<see cref="Ask"/> — как отдаёт брокер:
/// у облигаций T-Invest в ПУНКТАХ (процент номинала), у остальных — в <see cref="Currency"/> за 1 штуку. <see cref="PriceStep"/> — шаг цены
/// в <see cref="Currency"/> за штуку (у облигаций не используется), <see cref="PriceStepPoints"/> — шаг облигации в ПУНКТАХ.
/// <see cref="NominalRub"/> — текущий номинал облигации в рублях (null — номинал не в RUB или неизвестен: тогда рубли для оценки суммы
/// берутся из <see cref="ReferenceCleanPricePerPiece"/>). <see cref="FxToPortfolio"/> — множитель «валюта цены → валюта портфеля»
/// (RUB→RUB и USD→USD = 1).
/// </summary>
public sealed record PolicyQuote
{
    public required string Broker { get; init; }
    public required ulong AccountId { get; init; }

    /// <summary>ISIN либо тикер — как в <c>instrument_trading_meta</c>.</summary>
    public required string InstrumentKey { get; init; }

    public required string Ticker { get; init; }
    public string Name { get; init; } = string.Empty;
    public required PolicyAssetKind Kind { get; init; }
    public required PolicyMarket Market { get; init; }

    /// <summary>ISO-код валюты цены, ВЕРХНИМ регистром.</summary>
    public required string Currency { get; init; }

    /// <summary>Штук в лоте.</summary>
    public required decimal Lot { get; init; }

    public decimal? PriceStep { get; init; }
    public decimal? PriceStepPoints { get; init; }
    public decimal? NominalRub { get; init; }
    public decimal? Bid { get; init; }
    public decimal? Ask { get; init; }

    /// <summary>Цена последней сделки в тех же единицах, что bid/ask (облигации — пункты); null — неизвестна. Только для санити-проверки стакана.</summary>
    public decimal? Last { get; init; }

    /// <summary>Облигация: НКД на штуку (валюта цены); для остальных 0.</summary>
    public decimal AccruedPerPiece { get; init; }

    /// <summary>
    /// Облигация без рублёвого номинала: последняя известная ЧИСТАЯ цена за штуку в ВАЛЮТЕ ПОРТФЕЛЯ (рубли; из оценки портфеля или банка).
    /// <see cref="LinePricer"/> переводит её в валюту цены делением на <see cref="FxToPortfolio"/> (задача 67, #8).
    /// </summary>
    public decimal? ReferenceCleanPricePerPiece { get; init; }

    public decimal FxToPortfolio { get; init; } = 1m;
    public bool BuyAvailable { get; init; } = true;
    public bool SellAvailable { get; init; } = true;

    /// <summary>Freedom: есть котировочный двойник на ITS (определяет маршрут комиссии).</summary>
    public bool ItsTwin { get; init; }

    /// <summary>Комиссия бумаги известна: фонд управляющей компании брокера либо в журнале есть сделка и списанная по ней комиссия.</summary>
    public bool KnownCommission { get; init; } = true;

    /// <summary>
    /// Фонд: были ли выплаты за последние 12 месяцев (<c>instrument_trading_meta.pays_distributions</c>). null — неизвестно (сбой запроса или не фонд):
    /// для белого списка авто-исполнения (<see cref="AutoWhitelist"/>) неизвестно = «платит» (консервативно).
    /// </summary>
    public bool? PaysDistributions { get; init; }
}

/// <summary>
/// Оценка комиссии брокера. T-Invest — ДОЛЯ суммы (<see cref="TInvestRate"/>, 0.003 = 0.3%, как <c>ICommissionRateProvider</c>), КРОМЕ фондов управляющей компании
/// брокера (<see cref="OwnFunds"/>, тикеры без «@») — по ним комиссия 0; Freedom — <see cref="FreedomFeeModel"/> по тарифу.
/// <para>
/// <b>Валюта:</b> <see cref="Estimate"/> ВСЕГДА возвращает комиссию в валюте ЦЕНЫ бумаги (как <c>EstAmount</c> строки): Freedom считает в USD, поэтому для бумаги,
/// котируемой в другой валюте (EUR), USD-комиссия делится на <see cref="PolicyQuote.FxToPortfolio"/>. В валюту портфеля её приводят умножением на тот же курс.
/// </para>
/// </summary>
public sealed record PlanFeeModel(decimal TInvestRate, int FreedomTariffId, IReadOnlySet<string>? OwnFunds = null)
{
    public const int DefaultFreedomTariffId = FreedomFeeModel.StandardTariffId;

    private static readonly IReadOnlySet<string> DefaultOwnFunds =
        new HashSet<string>(new PolicyOptions().OwnFunds.Select(NormalizeFund), StringComparer.OrdinalIgnoreCase);

    /// <summary>Нормализация тикера фонда для сравнения: без «@» и пробелов, ВЕРХНИЙ регистр.</summary>
    public static string NormalizeFund(string ticker) => ticker.Trim().TrimEnd('@').ToUpperInvariant();

    /// <summary>Фонд управляющей компании брокера (комиссия 0)? Список — <see cref="OwnFunds"/> либо значения <see cref="PolicyOptions.OwnFunds"/> по умолчанию.</summary>
    public bool IsOwnFund(PolicyQuote quote)
    {
        if (quote.Broker != BrokerIds.TInvest || quote.Kind != PolicyAssetKind.Fund) return false;
        var key = NormalizeFund(quote.Ticker);
        return (OwnFunds ?? DefaultOwnFunds).Any(f => string.Equals(NormalizeFund(f), key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Комиссия в ВАЛЮТЕ ЦЕНЫ бумаги за заявку на <paramref name="amount"/> (валюта цены) и <paramref name="pieces"/> штук.
    /// <paramref name="freedomMarketOverride"/> — явный маршрут Freedom (P7 выбирает его сам по реальным комиссиям маршрутов).
    /// </summary>
    public decimal Estimate(PolicyQuote quote, decimal amount, decimal pieces, FreedomMarket? freedomMarketOverride = null)
    {
        if (quote.Broker == BrokerIds.Freedom)
        {
            var market = freedomMarketOverride
                         ?? FreedomFeeModel.RouteFor(quote.Ticker, quote.ItsTwin, quote.Kind is PolicyAssetKind.Etf or PolicyAssetKind.Fund);
            var fx = quote.FxToPortfolio > 0m ? quote.FxToPortfolio : 1m;
            var amountUsd = amount * fx; // портфель Freedom измеряется в USD
            return FreedomFeeModel.Fee(FreedomTariffId, market, amountUsd, pieces) / fx; // USD → валюта цены
        }

        return IsOwnFund(quote) ? 0m : amount * TInvestRate;
    }
}

/// <summary>Позиция портфеля для продаж (P5, выход из корзины, P3b, P4). <see cref="AvgCostPerPiece"/> — средняя цена входа за штуку в валюте цены (null — неизвестна).</summary>
public sealed record PolicyHolding
{
    public required string Broker { get; init; }
    public required string InstrumentKey { get; init; }
    public required string Ticker { get; init; }
    public string Name { get; init; } = string.Empty;
    public required decimal QuantityPieces { get; init; }

    /// <summary>Стоимость позиции в валюте ПОРТФЕЛЯ.</summary>
    public required decimal MarketValue { get; init; }

    /// <summary>Средняя цена входа за штуку (валюта цены, для облигаций с НКД как в <c>PositionCostBasis</c>); null — журнал неполон.</summary>
    public decimal? AvgCostPerPiece { get; init; }

    public bool HasUnknownCost { get; init; }
    public string BasketCode { get; init; } = SystemBaskets.Unassigned;
    public int LiquidityTier { get; init; } = 3;
}

/// <summary>
/// Кандидат-облигация (уже отфильтрован: гигиена, не флоатер, Green, ликвидность не Caution). <b>Единицы:</b> доходности — ДОЛИ годовых;
/// <see cref="CleanPriceRub"/>/<see cref="AccruedRub"/> — рубли за штуку (банк: <c>PricePercent/100 × FaceValue</c>, НКД MOEX).
/// <see cref="Quote"/> — стакан и параметры торгов T-Invest (null — нет метаданных или стакана: кандидат пропускается).
/// </summary>
public sealed record PolicyBondCandidate
{
    public required string Secid { get; init; }
    public string? Isin { get; init; }
    public required string Name { get; init; }
    public string? Sector { get; init; }

    /// <summary>Биржевая доходность (YTM MOEX), ДОЛЯ — по ней ранжирует <c>CashAllocationService</c>, как source=recommended.</summary>
    public decimal? YieldFraction { get; init; }

    /// <summary>Доходность к худшему из погашения/оферты ПОСЛЕ налога (<c>CandidateYieldRanking</c>), ДОЛЯ.</summary>
    public decimal? AfterTaxYieldFraction { get; init; }

    public decimal CleanPriceRub { get; init; }
    public decimal AccruedRub { get; init; }

    /// <summary>Ключ эмитента для лимита концентрации (id эмитента либо ИНН/название).</summary>
    public string? IssuerKey { get; init; }

    /// <summary>Рыночная стоимость уже удерживаемых бумаг этого эмитента, рубли.</summary>
    public decimal IssuerHeldValueRub { get; init; }

    public PolicyQuote? Quote { get; init; }
}

/// <summary>
/// Вход правил: срез одного портфеля пользователя. Правила — чистые функции от него (время, лимиты и опции приходят явно). <b>Единицы:</b> деньги —
/// в валюте портфеля (<see cref="Currency"/>), доли — ДОЛИ.
/// </summary>
public sealed record PolicyInput
{
    public required DateTime NowUtc { get; init; }
    public required string Portfolio { get; init; }
    public required string Broker { get; init; }
    public required ulong AccountId { get; init; }

    /// <summary>Валюта портфеля: RUB для rub, USD для usd.</summary>
    public required string Currency { get; init; }

    /// <summary>Свободные деньги ПОСЛЕ вычета резерва под открытые планы (валюта портфеля, не меньше 0).</summary>
    public decimal FreeCash { get; init; }

    /// <summary>Сколько уже зарезервировано под покупки открытых планов (справочно, валюта портфеля).</summary>
    public decimal ReservedCash { get; init; }

    /// <summary>Σ сумм авто-подтверждённых планов за московские сутки, рубли (для лимита дня).</summary>
    public decimal AutoApprovedTodayRub { get; init; }

    public IReadOnlyList<CapitalBasket> Baskets { get; init; } = [];
    public BasketDriftResult Drift { get; init; } = new([], 0m, 0m);
    public IReadOnlyList<PolicyQuote> Quotes { get; init; } = [];
    public IReadOnlyList<PolicyHolding> Holdings { get; init; } = [];
    public IReadOnlyList<PolicyBondCandidate> BondCandidates { get; init; } = [];
    public PlanFeeModel Fees { get; init; } = new(0.003m, PlanFeeModel.DefaultFreedomTariffId);
    public PolicyOptions Options { get; init; } = new();
    public ExecutionLimitsOptions Limits { get; init; } = new();

    /// <summary>true — игнорировать часы торгов (предпросмотр и ручные запросы): цены помечаются «возможно устарели».</summary>
    public bool IgnoreTradingHours { get; init; }

    /// <summary>Ожидаемая after-tax доходность фонда ликвидности (взвешенная по стоимости), ДОЛЯ годовых; null — нет оценки.</summary>
    public decimal? LiquidityFundAfterTaxYield { get; init; }

    /// <summary>Лимит доли эмитента, ПРОЦЕНТЫ (как <c>user_settings.default_max_concentration_percent</c>).</summary>
    public decimal MaxIssuerSharePercent { get; init; } = 25m;

    /// <summary>Стоимость облигационного портфеля T-Invest в рублях (база лимита эмитента).</summary>
    public decimal BondsPortfolioValueRub { get; init; }

    /// <summary>
    /// Задача 67 (#3): действует удержание денег под вывод (<see cref="WithdrawHold"/>: открыт план P5/выхода из корзины с <see cref="TradePlanSummary.CashHold"/>
    /// либо не прошло <see cref="PolicyOptions.WithdrawHoldTtl"/> рабочих дней с его исполнения, и владелец не отметил «деньги выведены»). P1 при нём не паркует кэш.
    /// Сумма удержания уже вычтена из <see cref="FreeCash"/> (входит в <see cref="ReservedCash"/>).
    /// </summary>
    public bool CashHoldActive { get; init; }

    /// <summary>
    /// Задача 67 (#7): оценка портфеля неполна — нет цены хотя бы у одной позиции с ненулевым количеством или нет курса (<c>CapitalPortfolioOverview.DataIncomplete</c>).
    /// Корзины, где именно не хватает данных, — <see cref="IncompleteBaskets"/>.
    /// </summary>
    public bool DataIncomplete { get; init; }

    /// <summary>
    /// Задача 67 (#7): коды строк дрейфа (корзины и системные <c>fx</c>/<c>unassigned</c>/<c>cash</c>) с неполными данными; <c>cash</c> — неизвестна сумма свободных
    /// денег (нет курса валюты, которую вычитают из денег T-Invest, или курса остатка Freedom).
    /// </summary>
    public IReadOnlySet<string> IncompleteBaskets { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// Задача 67 (#7): последний успешный синк брокера (UTC), если он старше <see cref="PolicyOptions.MaxSyncAge"/>; <see cref="DateTime.MinValue"/> — синка не было;
    /// null — данные свежие.
    /// </summary>
    public DateTime? StaleSince { get; init; }

    public CapitalBasket? BasketByCode(string code) => Baskets.FirstOrDefault(b => string.Equals(b.Code, code, StringComparison.Ordinal));

    public BasketDriftRow? RowByCode(string code) => Drift.Rows.FirstOrDefault(r => string.Equals(r.Code, code, StringComparison.Ordinal));

    /// <summary>Котировка по ключу корзины/позиции: ISIN, тикер (регистр и хвостовое «@» не важны; член без суффикса биржи подхватывает «.US»).</summary>
    public PolicyQuote? FindQuote(string key)
    {
        return Quotes.FirstOrDefault(q => BasketDriftService.KeyMatches(key, BasketDriftService.NormalizeKey(q.InstrumentKey)))
               ?? Quotes.FirstOrDefault(q => BasketDriftService.KeyMatches(key, BasketDriftService.NormalizeKey(q.Ticker)));
    }
}

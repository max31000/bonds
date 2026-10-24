using Bonds.Core.Brokers;
using Bonds.Core.Credentials;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Bonds.Core.Trading;
using Bonds.Infrastructure.Connectors.Freedom;
using Bonds.Infrastructure.Connectors.Freedom.Models;
using Bonds.Infrastructure.Connectors.TInvest;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bonds.Infrastructure.Sync;

/// <summary>Настройки <see cref="TradingMetaRefresher"/> (секция <c>TradingMeta</c>). Дефолты — рабочие, конфигурация не обязательна.</summary>
public sealed class TradingMetaOptions
{
    public const string SectionName = "TradingMeta";

    /// <summary>
    /// «Вселенная фондов» T-Invest: тикеры, метаданные которых ведутся ВСЕГДА (даже без позиции) — кандидаты для денег (правила фазы 2).
    /// Т-Капитал: TMON@, TSAV, TBRU@, TOFZ@, TPAY. Сторонние: LQDT (в API торгуется через SPBRU, не TQBR), SBFR, SBRB, OBLG, SBGB, AKMB.
    /// </summary>
    public List<string> FundUniverse { get; set; } = ["TMON@", "TSAV", "TBRU@", "TOFZ@", "TPAY", "LQDT", "SBFR", "SBRB", "OBLG", "SBGB", "AKMB"];

    /// <summary>Тикеры Freedom, метаданные которых ведутся всегда (сверх позиций счетов).</summary>
    public List<string> FreedomExtraTickers { get; set; } = ["SPYM.US", "VUAA.EU"];

    /// <summary>Запись моложе этого интервала не обновляется (суточный шаг: 20 ч, чтобы не «уплывал» из-за разброса времени циклов).</summary>
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromHours(20);

    /// <summary>Окно для признака «фонд платит распределения» — дни назад от сегодня.</summary>
    public int DistributionsWindowDays { get; set; } = 365;

    /// <summary>Пауза после неудачи по тикеру (исключение): повтор не раньше чем через этот срок, чтобы стабильно падающий тикер не долбился каждым циклом.</summary>
    public TimeSpan FailureBackoff { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Пауза между запросами листингов T-Invest (щадим лимиты брокера). В тестах — <see cref="TimeSpan.Zero"/>.</summary>
    public TimeSpan QueryDelay { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Максимум запросов листингов T-Invest за один цикл: холодный старт растягивается на несколько циклов (остаток добирается следующим).</summary>
    public int MaxQueriesPerCycle { get; set; } = 60;
}

/// <summary>Итог <see cref="TradingMetaRefresher"/> (для лога и тестов).</summary>
public sealed class TradingMetaRefreshResult
{
    public int TInvestUpserted { get; set; }
    public int FreedomUpserted { get; set; }
    public List<string> Errors { get; } = [];
}

/// <summary>
/// Задача 61: суточное обновление <c>instrument_trading_meta</c> — как и чем торгуется бумага (лот, шаг цены, флаги, режим торгов, признак выплат).
/// Шаг <see cref="Scheduling.SyncCycleService"/>; необязательный: сбой одной бумаги — LogWarning и дальше, метод не бросает (кроме отмены).
/// <para>
/// <b>Устаревание.</b> Ворота — по самой записи: строка моложе <see cref="TradingMetaOptions.RefreshInterval"/> не перезапрашивается (тикер или ключ
/// совпадает с <c>ticker</c>/<c>instrument_key</c>). Поэтому новая позиция/пользователь получают метаданные в ближайшем цикле, а не через сутки,
/// и повторные циклы дня не делают лишних вызовов брокера.
/// </para>
/// <para>
/// <b>T-Invest.</b> Набор: облигации (ISIN) и фонды (ISIN, иначе тикер) основного счёта + <see cref="TradingMetaOptions.FundUniverse"/>. По каждому —
/// все листинги (<see cref="ITInvestPortfolioClient.GetInstrumentListingsAsync"/>), режим торгов выбирает <see cref="TradingListingSelector"/>.
/// Шаг цены облигации — пункты → рубли (<see cref="BondPriceStep"/>, только при номинале в RUB, иначе NULL), сырой шаг в пунктах сохраняется отдельно (сетка цен облигации — в пунктах, заявки фазы 2 округлять в них). Между запросами — пауза <see cref="TradingMetaOptions.QueryDelay"/>, за цикл не более <see cref="TradingMetaOptions.MaxQueriesPerCycle"/> запросов, упавший тикер откладывается на <see cref="TradingMetaOptions.FailureBackoff"/>. Выплаты фонда —
/// <c>GetDividends</c> за 365 дней, сбой запроса оставляет прежнее значение признака.
/// </para>
/// <para>
/// <b>Freedom.</b> Тикеры позиций каждого пользователя с валидными ключами + <see cref="TradingMetaOptions.FreedomExtraTickers"/>. Лот — 1, шаг —
/// <c>min_step</c> котировки, валюта — <c>x_curr</c>. <c>its_twin</c> — существует ли котировка <c>&lt;base&gt;.ITS</c> (запись брокера с хотя бы одним
/// положительным полем: пустая запись = двойника нет). Тикеры <c>*.ITS</c> только котировочные — <c>api_trade_available = false</c>. Ключи пользователей
/// берутся через <see cref="IUserCredentialStore"/> и нигде не логируются; метаданные общие, пишутся один раз на тикер за прогон.
/// </para>
/// </summary>
public sealed class TradingMetaRefresher
{
    // Запросы без результата (тикер не найден у брокера): не долбим брокера каждым циклом, повтор не раньше чем через интервал обновления.
    // Static: сервис scoped (новый экземпляр на цикл), а память о промахах нужна между циклами. Ключ - "брокер:запрос".
    internal static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> MissedUntil = new();

    private readonly ITInvestPortfolioClient _tInvest;
    private readonly IFreedomApiClient _freedom;
    private readonly IUserCredentialStore _credentials;
    private readonly IAccountRepository _accounts;
    private readonly IPositionRepository _positions;
    private readonly IInstrumentRepository _instruments;
    private readonly IFundPositionRepository _fundPositions;
    private readonly IFundRepository _funds;
    private readonly IBrokerPositionRepository _brokerPositions;
    private readonly IInstrumentTradingMetaRepository _meta;
    private readonly TradingMetaOptions _options;
    private readonly ILogger<TradingMetaRefresher> _logger;

    public TradingMetaRefresher(
        ITInvestPortfolioClient tInvest,
        IFreedomApiClient freedom,
        IUserCredentialStore credentials,
        IAccountRepository accounts,
        IPositionRepository positions,
        IInstrumentRepository instruments,
        IFundPositionRepository fundPositions,
        IFundRepository funds,
        IBrokerPositionRepository brokerPositions,
        IInstrumentTradingMetaRepository meta,
        IOptions<TradingMetaOptions> options,
        ILogger<TradingMetaRefresher> logger)
    {
        _tInvest = tInvest;
        _freedom = freedom;
        _credentials = credentials;
        _accounts = accounts;
        _positions = positions;
        _instruments = instruments;
        _fundPositions = fundPositions;
        _funds = funds;
        _brokerPositions = brokerPositions;
        _meta = meta;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<TradingMetaRefreshResult> RefreshIfDueAsync(DateTime utcNow, bool tInvestAvailable, CancellationToken ct = default)
    {
        var result = new TradingMetaRefreshResult();

        if (tInvestAvailable)
        {
            try
            {
                result.TInvestUpserted = await RefreshTInvestAsync(utcNow, result, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Trading meta (T-Invest) refresh failed");
                result.Errors.Add("T-Invest: сбой обновления торговых метаданных");
            }
        }

        try
        {
            result.FreedomUpserted = await RefreshFreedomAsync(utcNow, result, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Trading meta (Freedom) refresh failed");
            result.Errors.Add("Freedom: сбой обновления торговых метаданных");
        }

        return result;
    }

    // ─── T-Invest ────────────────────────────────────────────────────────────────────────────

    private async Task<int> RefreshTInvestAsync(DateTime utcNow, TradingMetaRefreshResult result, CancellationToken ct)
    {
        var existing = await _meta.GetByBrokerAsync(Brokers.TInvest);
        var fresh = FreshKeys(existing, utcNow);
        var previous = existing.ToDictionary(m => m.InstrumentKey, StringComparer.OrdinalIgnoreCase);

        var queries = new List<string>();
        // Задача 67 (#5): позиции не только основного счёта, но и счетов T-Invest всех пользователей с валидным tinvest_read (у каждого свой токен и свой
        // портфель; метаданные торгов общие).
        var accountIds = new List<ulong>();
        if (await _accounts.GetPrimaryAccountIdAsync() is { } primaryAccount) accountIds.Add(primaryAccount);
        foreach (var userId in await _credentials.ListUserIdsWithValidAsync(CredentialProviders.TInvestRead) ?? [])
            foreach (var a in await _accounts.GetByUserIdAndBrokerAsync(userId, Brokers.TInvest) ?? [])
                if (!accountIds.Contains(a.Id)) accountIds.Add(a.Id);

        foreach (var acc in accountIds)
        {
            foreach (var p in await _positions.GetByAccountIdAsync(acc))
            {
                if (p.Quantity <= 0m) continue;
                var isin = (await _instruments.GetByIdAsync(p.InstrumentId))?.Isin;
                if (!string.IsNullOrWhiteSpace(isin)) queries.Add(isin.Trim());
            }

            foreach (var fp in await _fundPositions.GetByAccountAsync(acc))
            {
                if (fp.Quantity <= 0m) continue;
                var fund = await _funds.GetByIdAsync(fp.FundId);
                var q = !string.IsNullOrWhiteSpace(fund?.Isin) ? fund!.Isin : fund?.Ticker;
                if (!string.IsNullOrWhiteSpace(q)) queries.Add(q.Trim());
            }
        }

        queries.AddRange(_options.FundUniverse.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()));

        var upserted = 0;
        var sent = 0;
        var to = DateOnly.FromDateTime(utcNow);
        var from = to.AddDays(-_options.DistributionsWindowDays);
        foreach (var query in queries.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            if (fresh.Contains(query) || IsMissed(Brokers.TInvest, query, utcNow)) continue;

            if (sent >= _options.MaxQueriesPerCycle)
            {
                _logger.LogInformation("Trading meta: T-Invest query cap {Cap} reached, the rest is left for the next cycle", _options.MaxQueriesPerCycle);
                break;
            }

            if (sent > 0 && _options.QueryDelay > TimeSpan.Zero) await Task.Delay(_options.QueryDelay, ct);
            sent++;

            try
            {
                var listings = await _tInvest.GetInstrumentListingsAsync(query, ct);
                var picked = TradingListingSelector.Pick(listings.Select(l => new TradingListing(l.ClassCode, l.ApiTradeAvailable)).ToList());
                if (picked is null)
                {
                    _logger.LogWarning("Trading meta: no T-Invest listing found for {Query}", query);
                    MarkMissed(Brokers.TInvest, query, utcNow);
                    continue;
                }

                var listing = listings.First(l => string.Equals(l.ClassCode, picked.ClassCode, StringComparison.OrdinalIgnoreCase));
                var meta = MapTInvest(listing, utcNow);
                if (meta is null)
                {
                    _logger.LogWarning("Trading meta: {Query} skipped — no usable price step/nominal", query);
                    MarkMissed(Brokers.TInvest, query, utcNow);
                    continue;
                }

                if (!listing.IsBond)
                {
                    var count = await _tInvest.CountDistributionsAsync(listing.Uid, from, to, ct);
                    meta.PaysDistributions = count is { } c
                        ? c > 0
                        : previous.GetValueOrDefault(meta.InstrumentKey)?.PaysDistributions; // не удалось узнать — прежнее значение
                }

                await _meta.UpsertAsync(meta);
                upserted++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Trading meta: failed to refresh {Query}", query);
                result.Errors.Add($"T-Invest {query}: не удалось обновить");
                MarkMissed(Brokers.TInvest, query, utcNow, _options.FailureBackoff);
            }
        }

        return upserted;
    }

    /// <summary>
    /// Задача 63: метаданные T-Invest для облигаций-КАНДИДАТОВ (не в портфеле) по требованию: что уже есть в таблице — отдаётся как есть, недостающие ISIN
    /// запрашиваются у брокера (не больше <paramref name="maxQueries"/> за вызов, провал помнится в <see cref="MissedUntil"/> как у суточного шага) и сохраняются.
    /// Ключ результата — ISIN/тикер как передан (регистр не важен). Сбой одного ключа не роняет остальные.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, InstrumentTradingMeta>> EnsureTInvestAsync(
        IReadOnlyCollection<string> keys, DateTime utcNow, int maxQueries, CancellationToken ct = default)
    {
        var result = new Dictionary<string, InstrumentTradingMeta>(StringComparer.OrdinalIgnoreCase);
        if (keys.Count == 0) return result;

        foreach (var m in await _meta.GetByKeysAsync(Brokers.TInvest, keys)) result[m.InstrumentKey] = m;

        var sent = 0;
        foreach (var key in keys.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            if (result.ContainsKey(key) || IsMissed(Brokers.TInvest, key, utcNow) || sent >= maxQueries) continue;

            if (sent > 0 && _options.QueryDelay > TimeSpan.Zero) await Task.Delay(_options.QueryDelay, ct);
            sent++;
            try
            {
                var listings = await _tInvest.GetInstrumentListingsAsync(key, ct);
                var picked = TradingListingSelector.Pick(listings.Select(l => new TradingListing(l.ClassCode, l.ApiTradeAvailable)).ToList());
                var listing = picked is null ? null : listings.First(l => string.Equals(l.ClassCode, picked.ClassCode, StringComparison.OrdinalIgnoreCase));
                var meta = listing is null ? null : MapTInvest(listing, utcNow);
                if (meta is null)
                {
                    MarkMissed(Brokers.TInvest, key, utcNow);
                    continue;
                }

                await _meta.UpsertAsync(meta);
                result[key] = meta;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Trading meta: on-demand refresh of {Key} failed", key);
                MarkMissed(Brokers.TInvest, key, utcNow, _options.FailureBackoff);
            }
        }

        return result;
    }

    /// <summary>
    /// Листинг T-Invest → запись метаданных. Облигация: шаг брокера в ПУНКТАХ → рубли за бумагу (<see cref="BondPriceStep.PointsToRub"/>,
    /// номинал текущий), сырой шаг — в <see cref="InstrumentTradingMeta.MinPriceIncrementPoints"/>. Фонд: шаг уже в рублях за пай. Номинал не в RUB —
    /// рублёвый шаг null (остаётся шаг в пунктах). Нет номинала у облигации / шаг ≤ 0 / лот ≤ 0 — null (запись с неверным шагом хуже её отсутствия). Ключ — ISIN, иначе тикер.
    /// </summary>
    internal static InstrumentTradingMeta? MapTInvest(TInvestInstrumentListing listing, DateTime utcNow)
    {
        if (listing.Lot <= 0m || listing.MinPriceIncrementRaw <= 0m) return null;

        decimal? step;
        decimal? stepPoints = null;
        if (listing.IsBond)
        {
            if (listing.Nominal is not > 0m) return null;
            stepPoints = listing.MinPriceIncrementRaw;
            // Рублёвый шаг - только при номинале в RUB: иначе пункты/100 x номинал были бы в чужой валюте (100 USD != 100 руб).
            step = string.Equals(listing.NominalCurrency, "RUB", StringComparison.OrdinalIgnoreCase)
                ? BondPriceStep.PointsToRub(listing.MinPriceIncrementRaw, listing.Nominal.Value)
                : null;
        }
        else
        {
            step = listing.MinPriceIncrementRaw;
        }

        return new InstrumentTradingMeta
        {
            Broker = Brokers.TInvest,
            InstrumentKey = !string.IsNullOrWhiteSpace(listing.Isin) ? listing.Isin! : listing.Ticker,
            Uid = listing.Uid,
            Figi = listing.Figi,
            Ticker = listing.Ticker,
            ClassCode = listing.ClassCode,
            Lot = listing.Lot,
            MinPriceIncrement = step,
            MinPriceIncrementPoints = stepPoints,
            Currency = listing.Currency,
            ApiTradeAvailable = listing.ApiTradeAvailable,
            BuyAvailable = listing.BuyAvailable,
            SellAvailable = listing.SellAvailable,
            UpdatedAt = utcNow,
        };
    }

    // ─── Freedom ─────────────────────────────────────────────────────────────────────────────

    private async Task<int> RefreshFreedomAsync(DateTime utcNow, TradingMetaRefreshResult result, CancellationToken ct)
    {
        IReadOnlyList<ulong> userIds = await _credentials.ListUserIdsWithValidAsync(CredentialProviders.Freedom);
        if (userIds.Count == 0) return 0;

        var fresh = FreshKeys(await _meta.GetByBrokerAsync(Brokers.Freedom), utcNow);
        var handled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var upserted = 0;
        foreach (var userId in userIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                upserted += await RefreshFreedomUserAsync(userId, utcNow, fresh, handled, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Trading meta (Freedom) failed for user {UserId}", userId);
                result.Errors.Add($"Freedom (пользователь {userId}): не удалось обновить");
            }
        }

        return upserted;
    }

    private async Task<int> RefreshFreedomUserAsync(
        ulong userId, DateTime utcNow, HashSet<string> fresh, HashSet<string> handled, CancellationToken ct)
    {
        var tickers = new List<string>();
        foreach (var account in await _accounts.GetByUserIdAndBrokerAsync(userId, Brokers.Freedom))
        {
            tickers.AddRange((await _brokerPositions.GetByAccountAsync(account.Id)).Select(p => p.Ticker));
        }

        tickers.AddRange(_options.FreedomExtraTickers);
        var wanted = tickers
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim().ToUpperInvariant())
            .Distinct()
            .Where(t => !fresh.Contains(t) && !handled.Contains(t) && !IsMissed(Brokers.Freedom, t, utcNow))
            .ToList();
        if (wanted.Count == 0) return 0;

        var secret = await _credentials.GetSecretResultAsync(userId, CredentialProviders.Freedom);
        if (secret.Outcome != CredentialReadOutcome.Ok
            || secret.Fields is null
            || !secret.Fields.TryGetValue("publicKey", out var publicKey)
            || !secret.Fields.TryGetValue("privateKey", out var privateKey)
            || string.IsNullOrWhiteSpace(publicKey)
            || string.IsNullOrWhiteSpace(privateKey))
        {
            return 0;
        }

        var credentials = new FreedomCredentials(publicKey, privateKey);
        var twins = wanted.Where(t => !IsItsTicker(t)).Select(ItsTwinOf).ToList();
        var quotes = await _freedom.GetQuotesAsync(credentials, wanted.Concat(twins).Distinct().ToList(), ct);
        var byTicker = quotes
            .Where(q => !string.IsNullOrWhiteSpace(q.Ticker))
            .GroupBy(q => q.Ticker.Trim().ToUpperInvariant())
            .ToDictionary(g => g.Key, g => g.First());

        var upserted = 0;
        foreach (var ticker in wanted)
        {
            handled.Add(ticker);
            byTicker.TryGetValue(ticker, out var quote);
            bool? twin = IsItsTicker(ticker) ? null : QuoteHasData(byTicker.GetValueOrDefault(ItsTwinOf(ticker)));
            var meta = MapFreedom(ticker, quote, twin, utcNow);
            if (meta is null)
            {
                _logger.LogWarning("Trading meta: no usable Freedom quote for {Ticker}", ticker);
                MarkMissed(Brokers.Freedom, ticker, utcNow);
                continue;
            }

            await _meta.UpsertAsync(meta);
            upserted++;
        }

        return upserted;
    }

    /// <summary>
    /// Котировка Freedom → метаданные. Лот — 1 (брокер лотность не отдаёт, штучные бумаги), шаг — <c>min_step</c> в валюте котировки
    /// (<c>x_curr</c>, RUR нормализуется в RUB). Нет данных котировки / шага / валюты — null. <c>*.ITS</c> — только котировочный тикер,
    /// торговля по нему отклоняется (<c>api_trade_available = false</c>, покупка/продажа недоступны). Ключ — тикер (ISIN Freedom не отдаёт).
    /// </summary>
    internal static InstrumentTradingMeta? MapFreedom(string ticker, FreedomQuote? quote, bool? itsTwin, DateTime utcNow)
    {
        if (!QuoteHasData(quote) || quote!.MinStep is not > 0m) return null;
        var currency = BrokerAssetClassifier.NormalizeCurrency(quote.Currency);
        if (currency is null) return null;

        var tradable = !IsItsTicker(ticker);
        return new InstrumentTradingMeta
        {
            Broker = Brokers.Freedom,
            InstrumentKey = ticker,
            Ticker = ticker,
            Lot = 1m,
            MinPriceIncrement = quote.MinStep.Value,
            Currency = currency,
            ApiTradeAvailable = tradable,
            BuyAvailable = tradable,
            SellAvailable = tradable,
            ItsTwin = itsTwin,
            UpdatedAt = utcNow,
        };
    }

    /// <summary>Двойник <c>&lt;base&gt;.ITS</c>: <c>SPYM.US</c> → <c>SPYM.ITS</c>; тикер без суффикса — <c>X.ITS</c>.</summary>
    internal static string ItsTwinOf(string ticker)
    {
        var dot = ticker.LastIndexOf('.');
        var baseTicker = dot > 0 ? ticker[..dot] : ticker;
        return baseTicker + ".ITS";
    }

    internal static bool IsItsTicker(string ticker) => ticker.EndsWith(".ITS", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Запись котировки «живая»: хотя бы одно положительное поле (bid/ask/last/min_step). Брокер на несуществующий тикер может вернуть запись
    /// только с названием тикера или нулями — это «нет такого инструмента», а не котировка.
    /// </summary>
    internal static bool QuoteHasData(FreedomQuote? q) =>
        q is not null && (q.Bid > 0m || q.Ask > 0m || q.Last > 0m || q.MinStep > 0m);

    private bool IsMissed(string broker, string query, DateTime utcNow) =>
        MissedUntil.TryGetValue(broker + ":" + query.ToUpperInvariant(), out var until) && utcNow < until;

    private void MarkMissed(string broker, string query, DateTime utcNow, TimeSpan? backoff = null) =>
        MissedUntil[broker + ":" + query.ToUpperInvariant()] = utcNow + (backoff ?? _options.RefreshInterval);

    /// <summary>Ключи и тикеры (верхний регистр) записей моложе интервала обновления — их перезапрашивать не нужно.</summary>
    private HashSet<string> FreshKeys(IReadOnlyList<InstrumentTradingMeta> existing, DateTime utcNow)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in existing.Where(m => utcNow - m.UpdatedAt < _options.RefreshInterval))
        {
            set.Add(m.InstrumentKey);
            set.Add(m.Ticker);
        }

        return set;
    }
}

using Bonds.Core.Calculation;
using Bonds.Core.Currencies;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Bonds.Core.Time;
using Bonds.Infrastructure.Connectors.Moex;
using Bonds.Infrastructure.Connectors.TInvest;
using Bonds.Infrastructure.Quotes;
using Microsoft.Extensions.Logging;

namespace Bonds.Infrastructure.Sync;

/// <summary>
/// Оркестратор синка. Один вызов <see cref="SyncAsync"/>:
/// (1) синк позиций+операций T-Invest → upsert в positions/operations;
/// (2) для каждого инструмента портфеля — догрузка расписаний/параметров из MOEX;
/// (3) обновление котировок (T-Invest = "сейчас") и снимка безрисковой кривой (MOEX Gcurve);
/// (4) пометки неполноты (DataIncomplete на Instrument/Position) — не падает на одном плохом
///     инструменте, остальной синк продолжается (устойчивость к частичным сбоям, см. SyncResult).
/// Не вызывается из HTTP и не планируется по расписанию — чистый сервис,
/// вызываемый программно/из тестов на этом этапе.
/// </summary>
public sealed class BondSyncService
{
    // Счета, по которым предыдущий синк получил от брокера пустой список облигаций (см. сверку).
    // Static: сервис scoped (новый экземпляр на цикл), а подтверждение нужно между циклами.
    internal static readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, byte> EmptyPortfolioSeen = new();

    private readonly ITInvestPortfolioClient _tInvest;
    private readonly IMoexIssClient _moex;
    private readonly IInstrumentRepository _instruments;
    private readonly ICouponScheduleRepository _coupons;
    private readonly IAmortizationScheduleRepository _amortizations;
    private readonly IOfferScheduleRepository _offers;
    private readonly IMarketQuoteRepository _quotes;
    private readonly IYieldCurveRepository _yieldCurve;
    private readonly IPositionRepository _positions;
    private readonly IOperationRepository _operations;
    private readonly IAccountRepository _accounts;
    private readonly ILogger<BondSyncService> _logger;
    private readonly IFundSyncService? _fundSync;
    private readonly ICurrencySyncService? _currencySync;
    private readonly ICurrencyRateLookup? _rateLookup;

    public BondSyncService(
        ITInvestPortfolioClient tInvest,
        IMoexIssClient moex,
        IInstrumentRepository instruments,
        ICouponScheduleRepository coupons,
        IAmortizationScheduleRepository amortizations,
        IOfferScheduleRepository offers,
        IMarketQuoteRepository quotes,
        IYieldCurveRepository yieldCurve,
        IPositionRepository positions,
        IOperationRepository operations,
        IAccountRepository accounts,
        ILogger<BondSyncService> logger,
        IFundSyncService? fundSync = null,
        ICurrencySyncService? currencySync = null,
        ICurrencyRateLookup? rateLookup = null)
    {
        _fundSync = fundSync;
        _currencySync = currencySync;
        _rateLookup = rateLookup;
        _tInvest = tInvest;
        _moex = moex;
        _instruments = instruments;
        _coupons = coupons;
        _amortizations = amortizations;
        _offers = offers;
        _quotes = quotes;
        _yieldCurve = yieldCurve;
        _positions = positions;
        _operations = operations;
        _accounts = accounts;
        _logger = logger;
    }

    public async Task<SyncResult> SyncAsync(ulong accountId, DateTime? operationsFrom = null, CancellationToken ct = default)
    {
        var result = new SyncResult();

        // Брокерский счёт закрепляется за локальным при первом синке: «первый открытый счёт» может
        // смениться (открыли ИИС/второй счёт), а сверка позиций ниже удаляет всё, чего нет на
        // синкаемом счёте, — переключение счёта молча стёрло бы портфель.
        var account = await _accounts.GetByIdAsync(accountId);
        var brokerAccountId = account?.BrokerAccountId;
        if (string.IsNullOrEmpty(brokerAccountId))
        {
            brokerAccountId = await _tInvest.GetPrimaryAccountIdAsync(ct);
            if (brokerAccountId is null)
            {
                result.Errors.Add("T-Invest: не удалось определить брокерский счёт (нет открытых счетов для токена)");
                return result;
            }

            if (account is not null)
            {
                account.BrokerAccountId = brokerAccountId;
                await _accounts.UpdateAsync(account);
            }
        }

        // --- (1) Позиции + операции из T-Invest ---
        TInvestPortfolio portfolio;
        try
        {
            portfolio = await _tInvest.GetPortfolioAsync(brokerAccountId, ct);
        }
        catch (Exception ex)
        {
            // Полный сбой брокерского API — без позиций продолжать бессмысленно (нет инструментов
            // для дозагрузки MOEX-данных), но это не должно ронять процесс синка с unhandled exception.
            _logger.LogError(ex, "T-Invest: failed to fetch portfolio positions");
            result.Errors.Add("T-Invest: не удалось получить позиции портфеля");
            return result;
        }

        var tInvestPositions = portfolio.Bonds;
        try
        {
            // Свободные деньги: сюда приходят погашения/купоны — без них итог не сходится с брокером.
            await _accounts.UpdateBalanceAsync(accountId, portfolio.CashRub, portfolio.TotalPortfolioRub, DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to store account cash balance");
            result.Errors.Add("Не удалось сохранить денежный баланс счёта");
        }

        // Задача 51: операции по фондам (БПИФ/ETF) собираем отдельно и отдаём FundSyncService — в облигационный
        // журнал они не попадают (ни Buy/Sell, ни выплаты/налог).
        var fundOperations = new List<TInvestOperation>();
        // Задача 55: операции по валюте - отдельный валютный журнал (CurrencySyncService).
        var currencyOperations = new List<TInvestOperation>();
        try
        {
            var tInvestOperations = await _tInvest.GetOperationsAsync(brokerAccountId, operationsFrom, ct);
            // Задача 55: платёж операции - в СВОЕЙ валюте (Payment.Currency). Не-рублёвый пересчитывается по курсу дня или операция
            // пропускается (с ошибкой в результате) - раньше число молча записывалось как рубли.
            tInvestOperations = await NormalizePaymentCurrenciesAsync(tInvestOperations, result, ct);
            var domainOperations = new List<Operation>();
            // Резолв FIGI → инструмент кэшируется в пределах синка: историческая бумага с десятком
            // операций не должна давать десять походов в справочник/T-Invest/MOEX.
            var figiCache = new Dictionary<string, ulong?>();
            foreach (var op in tInvestOperations)
            {
                if (op.IsFund)
                {
                    fundOperations.Add(op);
                    continue;
                }

                if (op.IsCurrency)
                {
                    currencyOperations.Add(op);
                    continue;
                }

                var mappedType = TInvestOperationMapper.Map(op.OperationType);
                if (mappedType is null) continue; // нерелевантный тип операции (см. README коннектора) — пропускаем, не падаем

                // Журнал — облигационный. Операции по акциям/фондам/валюте (купил CNYRUB_TOM,
                // дивидендный налог по акции) без инструмента-облигации портили XIRR и InvestedRub.
                // Тип неизвестен (null) — операция по счёту (комиссия/налог без бумаги) либо старый
                // формат — не отбрасываем. Тип известен и это не облигация — пропускаем любую операцию.
                if (op.InstrumentKind is not null && !op.IsBond) continue;

                ulong? instrumentId = null;
                if (!string.IsNullOrEmpty(op.Figi))
                {
                    // Тип не указан — создавать инструмент не будем (может быть не облигация), только найдём.
                    instrumentId = await ResolveOperationInstrumentAsync(op.Figi, allowCreate: op.IsBond, figiCache, ct);
                }

                domainOperations.Add(new Operation
                {
                    AccountId = accountId,
                    InstrumentId = instrumentId,
                    Type = mappedType.Value,
                    Date = op.Date,
                    AmountRub = op.PaymentRub,
                    Quantity = op.Quantity,
                    // НКД операции — величина в РУБЛЯХ на всю операцию (T-Invest AccruedInt, знак отбрасываем: направление
                    // задаёт тип). Только для Buy/Sell; у купонов/налогов/комиссий поле не применимо (задача 52).
                    AccruedRub = mappedType is OperationType.Buy or OperationType.Sell && op.AccruedInterest is not null
                        ? Math.Abs(op.AccruedInterest.Value)
                        : null,
                    ExternalId = op.Id,
                });
            }

            if (domainOperations.Count > 0)
            {
                result.OperationsUpserted = await _operations.UpsertManyByExternalIdAsync(domainOperations);
            }

            result.JournalSynced = !result.JournalIncompleteByRates;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "T-Invest: failed to sync operations journal");
            result.Errors.Add("T-Invest: не удалось синхронизировать журнал операций");
            // Не return — позиции и справочник всё равно стоит обновить (частичный успех).
        }

        // --- Фонды (задача 51): отдельный домен, на уже полученных данных брокера. Сбой не страдает облигационный синк.
        if (_fundSync is not null)
        {
            try
            {
                var fundResult = await _fundSync.ApplyAsync(accountId, portfolio.Funds, fundOperations, ct);
                result.FundsSynced = fundResult.FundsSynced;
                foreach (var error in fundResult.Errors) result.Errors.Add($"Фонды: {error}");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fund sync failed");
                result.Errors.Add($"Фонды: ошибка синка ({ex.GetType().Name})");
            }
        }

        // --- Валюта (задача 55): отдельный домен, на уже полученных данных брокера. Сбой не затрагивает облигационный синк.
        if (_currencySync is not null)
        {
            try
            {
                var currencyResult = await _currencySync.ApplyAsync(accountId, portfolio.Currencies, currencyOperations, ct);
                result.CurrenciesSynced = currencyResult.CurrenciesSynced;
                foreach (var error in currencyResult.Errors) result.Errors.Add($"Валюта: {error}");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Currency sync failed");
                result.Errors.Add($"Валюта: ошибка синка ({ex.GetType().Name})");
            }
        }

        // --- (2)+(4) Для каждой позиции — резолв инструмента + догрузка MOEX + upsert позиции ---
        var resolvedInstruments = new List<(ulong InstrumentId, string Figi)>();
        // Инструменты, которые брокер СЕЙЧАС держит на счёте, — всё остальное в positions после цикла
        // удаляется (сверка). Если хоть одну бумагу брокера не удалось сопоставить с инструментом,
        // сверку пропускаем целиком: лучше один цикл показать лишнюю позицию, чем удалить живую.
        var heldInstrumentIds = new HashSet<ulong>();
        var reconciliationSafe = true;
        foreach (var tip in tInvestPositions)
        {
            ct.ThrowIfCancellationRequested();
            ulong? instrumentId = null;
            try
            {
                instrumentId = await ResolveOrCreateInstrumentAsync(tip.Figi, ct);
                if (instrumentId is null)
                {
                    reconciliationSafe = false;
                    result.Errors.Add($"Не удалось определить инструмент для FIGI {tip.Figi} — позиция пропущена");
                    continue;
                }

                heldInstrumentIds.Add(instrumentId.Value);

                await EnrichFromMoexAsync(instrumentId.Value, ct);

                await _positions.UpsertAsync(new Position
                {
                    AccountId = accountId,
                    InstrumentId = instrumentId.Value,
                    Quantity = tip.Quantity,
                    AvgPurchasePrice = tip.AveragePositionPrice,
                    Accrued = tip.CurrentNkd ?? 0m,
                    // Позиция считается неполной, если T-Invest не отдал НКД по открытой позиции —
                    // движок не должен молча считать НКД=0 как достоверный факт (spec §4.4).
                    DataIncomplete = tip.CurrentNkd is null,
                });

                var quotedFromPortfolio = false;
                if (tip.CurrentPrice is not null)
                {
                    await _quotes.UpsertAsync(new MarketQuote
                    {
                        InstrumentId = instrumentId.Value,
                        AsOf = BusinessClock.MoscowToday(),
                        CleanPrice = tip.CurrentPrice,
                        DirtyPrice = tip.CurrentPrice + tip.CurrentNkd,
                        Accrued = tip.CurrentNkd,
                        Source = MarketQuoteSource.TInvest,
                    });
                    quotedFromPortfolio = true;
                }

                // Котировка из портфеля (с НКД) — приоритетнее, чем добавочный вызов GetQuotesAsync
                // ниже (часть 3): тот несёт только LastPrice без НКД, и если бы мы его всё равно
                // писали повторно, ON DUPLICATE KEY UPDATE обнулил бы уже сохранённые Accrued/DirtyPrice
                // для той же строки (InstrumentId, AsOf, Source) — наблюдалось при ревью кода этого
                // этапа. Поэтому instrument добавляется в очередь на доп. котировку только если
                // портфель не дал цену вовсе (например, бумага неактивна/не торгуется сегодня).
                if (!quotedFromPortfolio)
                {
                    resolvedInstruments.Add((instrumentId.Value, tip.Figi));
                }

                result.InstrumentsSynced++;
            }
            catch (Exception ex)
            {
                // Устойчивость к частичному сбою: один упавший инструмент
                // не должен ронять синк остальных позиций портфеля.
                _logger.LogWarning(ex, "Failed to sync instrument for FIGI {Figi} — skipping, continuing with the rest", tip.Figi);
                result.Errors.Add($"Инструмент FIGI {tip.Figi}: ошибка синка ({ex.GetType().Name})");
                if (instrumentId is null) reconciliationSafe = false;
            }
        }

        // --- Сверка: позиции, которых у брокера больше нет (погашение, оферта, полная продажа,
        // вывод бумаг), удаляются. Без этого погашенная бумага навсегда оставалась в списке и итогах
        // с последней известной ценой (баг ОФЗ 26226, 2026-10).
        // Пустой ответ брокера при непустом локальном портфеле — подозрительно (деградация API,
        // токен с урезанным доступом): стирать весь портфель по одному ответу нельзя. Легитимный
        // случай «всё погашено/продано» подтверждается тем, что брокер видит счёт, но облигаций нет —
        // тогда удаляем со следующего синка, если и он пустой (два подряд).
        if (reconciliationSafe && tInvestPositions.Count == 0)
        {
            var localCount = (await _positions.GetByAccountIdAsync(accountId)).Count();
            if (localCount > 0 && !EmptyPortfolioSeen.ContainsKey(accountId))
            {
                EmptyPortfolioSeen[accountId] = 0;
                reconciliationSafe = false;
                _logger.LogWarning(
                    "Sync: broker returned no bonds while {Count} local position(s) exist — deferring deletion to the next sync",
                    localCount);
            }
        }
        else
        {
            EmptyPortfolioSeen.TryRemove(accountId, out _);
        }

        if (reconciliationSafe)
        {
            try
            {
                result.PositionsClosed = await _positions.DeleteAllExceptAsync(accountId, heldInstrumentIds);
                if (result.PositionsClosed > 0)
                {
                    _logger.LogInformation("Sync: closed {Count} position(s) no longer held at the broker", result.PositionsClosed);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to reconcile closed positions");
                result.Errors.Add("Не удалось убрать закрытые позиции (погашенные/проданные)");
            }
        }

        // --- (3) Котировки/стакан — fallback только для позиций, по которым портфель T-Invest
        // не дал CurrentPrice вовсе (resolvedInstruments здесь содержит именно такие, см. выше).
        try
        {
            var figis = resolvedInstruments.Select(r => r.Figi).ToList();
            if (figis.Count > 0)
            {
                var liveQuotes = await _tInvest.GetQuotesAsync(figis, ct);
                foreach (var (instrumentId, figi) in resolvedInstruments)
                {
                    if (!liveQuotes.TryGetValue(figi, out var q) || q.LastPrice is null) continue;

                    // T-Invest marketdata (GetQuotesAsync/LastPrice) отдаёт цену облигации в
                    // ПУНКТАХ (% от номинала), не в рублях — в отличие от GetPortfolio.CurrentPrice
                    // (часть 2 выше), который брокер уже конвертирует в рубли. См. doc-comment
                    // TInvestQuote.LastPrice и LiveQuoteConverter. Переводим в рубли через номинал.
                    var instrument = await _instruments.GetByIdAsync(instrumentId);
                    if (instrument is null) continue;

                    // По погашенной/делистингованной бумаге последняя сделка — давняя, а
                    // свежая котировка «сегодня» воскресила бы её в итогах.
                    if (instrument.MaturityDate < BusinessClock.MoscowToday()) continue;

                    var cleanPriceRub = LiveQuoteConverter.TryComputeCleanPriceRub(
                        q.LastPrice.Value, instrument.FaceValue, instrument.IsOutOfScopeCurrency);
                    if (cleanPriceRub is null)
                    {
                        // Валютный номинал — конвертация в рубли без курса невозможна (spec §11),
                        // не пишем неверную котировку (задание п.2).
                        continue;
                    }

                    // q.BestBid/q.BestAsk (стакан) намеренно НЕ пишутся здесь — это осознанное
                    // решение, не недосмотр. Спека §8 относит "предупреждение о низкой
                    // ликвидности по позиции (тонкий стакан)" явно к категории "на будущее, при
                    // появлении планирования вывода" — вне MVP. MarketQuote не несёт
                    // полей под bid/ask: добавлять миграцию/колонки под данные, которые сейчас
                    // никто не читает, было бы преждевременным расширением схемы. Стакан всё
                    // равно ЗАПРАШИВАЕТСЯ у T-Invest (ITInvestPortfolioClient.GetQuotesAsync) —
                    // это сознательно сохранено на случай будущего правила ликвидности (plan/40 удалил
                    // заглушку LowLiquidityWarning из движка событий) — когда оно понадобится, реализация
                    // сможет читать q.BestBid/q.BestAsk без отдельного похода в API. Если понадобится
                    // персистентность стакана (история, а не только "сейчас") — тогда добавить миграцию полей.
                    await _quotes.UpsertAsync(new MarketQuote
                    {
                        InstrumentId = instrumentId,
                        AsOf = BusinessClock.MoscowToday(),
                        CleanPrice = cleanPriceRub,
                        Volume = null,
                        Source = MarketQuoteSource.TInvest,
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh live quotes/order book from T-Invest");
            result.Errors.Add("T-Invest: не удалось обновить текущие котировки/стакан");
        }

        // --- (3) Gcurve ---
        try
        {
            var curve = await _moex.GetYieldCurveAsync(ct);
            if (curve is not null)
            {
                await _yieldCurve.UpsertAsync(curve);
                result.YieldCurveUpdated = true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh MOEX yield curve (Gcurve)");
            result.Errors.Add("MOEX: не удалось обновить безрисковую кривую (Gcurve)");
        }

        return result;
    }

    /// <summary>
    /// Приводит платежи операций к рублям (задача 55). Рублёвые (и без валюты) и НУЛЕВЫЕ платежи (0 в любой валюте = 0 ₽, например
    /// Input/OutputSecurities) - без курса, как есть (в рубли). Не-рублёвые, релевантные портфелю (тип операции мапится в облигационный/
    /// фондовый/валютный журнал), пересчитываются по курсу дня из <c>currency_rate_history</c> (<see cref="TInvestOperationCurrency.ToRubles"/>).
    /// Курса нет - операция пропускается, но это НЕ ошибка цикла (иначе постоянный HasErrors и ежедневный алерт): LogWarning +
    /// <see cref="SyncResult.SkippedForeignOperations"/>. Если причина самолечащаяся (валюта есть, истории курса ещё нет), дополнительно
    /// <see cref="SyncResult.JournalSynced"/> не выставляется, пока такие пропуски есть - бэкфилл истории стоимости не должен
    /// закрепиться из неполного журнала. Журнал читается целиком каждый синк, поэтому пропущенное подхватится, когда появится курс.
    /// </summary>
    private async Task<IReadOnlyList<TInvestOperation>> NormalizePaymentCurrenciesAsync(
        IReadOnlyList<TInvestOperation> operations, SyncResult result, CancellationToken ct)
    {
        if (operations.All(o => PaymentCurrencyConverter.IsRub(o.PaymentCurrency))) return operations;

        var normalized = new List<TInvestOperation>(operations.Count);
        var skipped = 0;
        var skippedCurrencies = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var op in operations)
        {
            if (PaymentCurrencyConverter.IsRub(op.PaymentCurrency) || !IsRelevantForPortfolio(op))
            {
                // Нерелевантные операции (дивиденд по акции в USD, ввод валюты и т.п.) всё равно отбросят дальше по типу - не шумим.
                normalized.Add(op);
                continue;
            }

            if (op.PaymentRub == 0m)
            {
                // 0 в любой валюте = 0 рублей: курс не нужен. Сохраняем операцию как рублёвую.
                normalized.Add(TInvestOperationCurrency.ToRubles(op, 1m) ?? op);
                continue;
            }

            decimal? rate = null;
            if (_rateLookup is not null)
            {
                rate = await _rateLookup.GetRubPerUnitAsync(op.PaymentCurrency!, DateOnly.FromDateTime(op.Date), ct);
            }

            var converted = TInvestOperationCurrency.ToRubles(op, rate);
            if (converted is null)
            {
                skipped++;
                skippedCurrencies.Add(op.PaymentCurrency!.ToUpperInvariant());
                var miss = _rateLookup is null ? RateMissKind.NotInDirectory : await _rateLookup.ClassifyMissAsync(op.PaymentCurrency!, ct);
                if (miss == RateMissKind.NoHistoryYet) result.JournalIncompleteByRates = true;
                _logger.LogWarning(
                    "Operation {OperationId} has payment in {Currency} without an exchange rate for {Date:yyyy-MM-dd} ({Reason}) - skipped",
                    op.Id, op.PaymentCurrency, op.Date, miss);
                continue;
            }

            normalized.Add(converted);
        }

        result.SkippedForeignOperations += skipped;
        if (skipped > 0)
        {
            _logger.LogWarning(
                "Skipped {Count} operations in foreign currency without a rate ({Currencies}); not a sync error",
                skipped, string.Join(", ", skippedCurrencies));
        }

        return normalized;
    }

    /// <summary>
    /// Операция потенциально попадёт в один из журналов портфеля: облигация/фонд/валюта (или без инструмента - «по счёту») и тип
    /// операции мапится соответствующим маппером. Остальное (акции, фьючерсы, пополнения) отбрасывается дальше - пересчёт ей не нужен.
    /// </summary>
    private static bool IsRelevantForPortfolio(TInvestOperation op)
    {
        if (op.IsFund) return TInvestFundOperationMapper.Map(op.OperationType) is not null;
        if (op.IsCurrency) return TInvestCurrencyOperationMapper.Map(op.OperationType) is not null;
        if (op.InstrumentKind is not null && !op.IsBond) return false;
        return TInvestOperationMapper.Map(op.OperationType) is not null;
    }

    /// <summary>
    /// Инструмент для облигационной операции журнала. Бумага, купленная и погашенная/
    /// проданная до первого синка, в справочнике отсутствует — создаём её тем же
    /// <see cref="ResolveOrCreateInstrumentAsync"/> и по возможности обогащаем из MOEX, чтобы
    /// <see cref="Operation.InstrumentId"/> не был null (иначе операция выпадает из истории и cost basis).
    /// Результат (в т.ч. неудачный) кэшируется по FIGI на время синка. Сбой не бросается: операция
    /// сохранится без инструмента, как раньше.
    /// </summary>
    private async Task<ulong?> ResolveOperationInstrumentAsync(
        string figi, bool allowCreate, Dictionary<string, ulong?> cache, CancellationToken ct)
    {
        if (cache.TryGetValue(figi, out var cached)) return cached;

        ulong? id = null;
        try
        {
            var existing = await _instruments.GetByFigiAsync(figi);
            if (existing is not null)
            {
                id = existing.Id;
            }
            else if (allowCreate)
            {
                id = await ResolveOrCreateInstrumentAsync(figi, ct);
                if (id is not null)
                {
                    try
                    {
                        await EnrichFromMoexAsync(id.Value, ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(ex, "Failed to enrich historical instrument for FIGI {Figi}", figi);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to resolve instrument for operation FIGI {Figi}", figi);
        }

        cache[figi] = id;
        return id;
    }

    /// <summary>
    /// Резолвит инструмент по ISIN для бумаги БЕЗ позиции (задача 20 — watchlist): если уже есть в
    /// справочнике — возвращает его Id; иначе создаёт минимальную запись Instrument (без FIGI —
    /// T-Invest мост здесь не нужен, у бумаги нет позиции/брокерского счёта), которую затем
    /// дозаполняет <see cref="EnrichFromMoexAsync"/>. Тот же принцип "MOEX — источник истины по
    /// справочным полям", что и у <see cref="ResolveOrCreateInstrumentAsync"/>, без параллельного
    /// пайплайна — переиспользует <see cref="EnrichFromMoexAsync"/> один в один.
    /// </summary>
    public async Task<ulong?> ResolveOrCreateInstrumentByIsinAsync(string isin, CancellationToken ct = default)
    {
        var existing = await _instruments.GetByIsinAsync(isin);
        if (existing is not null)
        {
            await EnrichFromMoexAsync(existing.Id, ct);
            return existing.Id;
        }

        var placeholder = new Instrument
        {
            Isin = isin,
            FaceValue = 0m,
            Currency = "RUB",
            MaturityDate = BusinessClock.MoscowToday(),
            DataIncomplete = true,
        };

        var id = await _instruments.UpsertAsync(placeholder);
        await EnrichFromMoexAsync(id, ct);
        return id;
    }

    /// <summary>
    /// Резолвит инструмент по FIGI: если уже есть в справочнике — возвращает его Id; иначе
    /// узнаёт ISIN через T-Invest (часть B п.2) и создаёт минимальную запись Instrument, которую
    /// затем дозаполнит <see cref="EnrichFromMoexAsync"/>. Источник истины по справочным полям —
    /// MOEX (см. doc-comment Instrument.cs), T-Invest здесь используется только как мост FIGI→ISIN.
    /// </summary>
    private async Task<ulong?> ResolveOrCreateInstrumentAsync(string figi, CancellationToken ct)
    {
        var existing = await _instruments.GetByFigiAsync(figi);
        if (existing is not null) return existing.Id;

        var isin = await _tInvest.GetIsinByFigiAsync(figi, ct);
        if (isin is null) return null;

        var byIsin = await _instruments.GetByIsinAsync(isin);
        if (byIsin is not null)
        {
            // Инструмент уже существовал (например, создан раньше через MOEX-поиск без FIGI) —
            // дозаполняем Figi, чтобы следующий синк резолвился по FIGI напрямую.
            byIsin.Figi = figi;
            return await _instruments.UpsertAsync(byIsin);
        }

        // Минимальная заготовка — MaturityDate/FaceValue будут перезаписаны в EnrichFromMoexAsync,
        // если MOEX найдёт бумагу; до этого момента инструмент создаётся с заведомо неполными
        // справочными данными и явной пометкой (spec §4.4 "не подставлять нули молча").
        var placeholder = new Instrument
        {
            Isin = isin,
            Figi = figi,
            FaceValue = 0m,
            Currency = "RUB",
            MaturityDate = BusinessClock.MoscowToday(),
            DataIncomplete = true,
        };

        var id = await _instruments.UpsertAsync(placeholder);
        return id;
    }

    /// <summary>
    /// Догружает/обновляет справочные данные инструмента из MOEX: резолвит SECID (если ещё не
    /// закэширован), параметры выпуска, купоны/амортизации/оферты, помечает DataIncomplete.
    /// Источник истины по расписаниям — MOEX, даже если T-Invest что-то задублировал (преамбула plan/04).
    /// Public — переиспользуется задачей 20 (watchlist) как единственная точка обогащения инструмента,
    /// чтобы не заводить параллельный пайплайн расчёта для бумаг без позиции.
    /// </summary>
    public async Task EnrichFromMoexAsync(ulong instrumentId, CancellationToken ct = default)
    {
        var instrument = await _instruments.GetByIdAsync(instrumentId);
        if (instrument is null) return;

        var secid = instrument.Secid;
        if (string.IsNullOrEmpty(secid))
        {
            secid = await _moex.ResolveSecidByIsinAsync(instrument.Isin, ct);
            if (secid is null)
            {
                // Не нашли бумагу на MOEX (делистинг, иностранный эмитент вне ISS и т.п.) —
                // помечаем неполноту и выходим, не подставляя нули (spec §4.4).
                instrument.DataIncomplete = true;
                await _instruments.UpsertAsync(instrument);
                return;
            }

            instrument.Secid = secid;
        }

        var info = await _moex.GetSecurityInfoAsync(secid, ct);
        var bondization = await _moex.GetBondizationAsync(secid, ct);

        if (info is not null)
        {
            instrument.FaceValue = info.FaceValue ?? instrument.FaceValue;
            instrument.MaturityDate = info.MatDate ?? instrument.MaturityDate;
            // валюту номинала берём из MOEX FACEUNIT и нормализуем (SUR/RUR→RUB; иначе как
            // есть и вне скоупа). Раньше Currency не обновлялась и оставалась "RUB" даже для USD-бумаг.
            if (info.FaceUnit is not null)
            {
                var (currency, outOfScope) = CurrencyNormalizer.Normalize(info.FaceUnit);
                instrument.Currency = currency;
                instrument.IsOutOfScopeCurrency = outOfScope;
            }
        }

        instrument.Name = info?.ShortName ?? info?.SecName ?? instrument.Isin;

        var searchInfo = await _moex.GetSecuritySearchAsync(instrument.Isin, ct);
        instrument.Issuer = searchInfo?.EmitentTitle ?? instrument.Issuer;
        instrument.Sector = MoexSegmentMapper.MapTypeToSegment(searchInfo?.TypeCode) ?? instrument.Sector;

        // CouponType/HasAmortization/HasOffers выводятся из ФАКТИЧЕСКОГО графика bondization
        // (приоритетнее эвристики BONDTYPE из securities.json — последняя используется только
        // как fallback, если bondization не вернул купонов вовсе).
        var hasFloatingCoupon = HasFloatingCoupon(
            bondization.Coupons,
            bondization.Offers,
            BusinessClock.MoscowToday(),
            info?.LooksLikeFloater ?? false);
        instrument.CouponType = hasFloatingCoupon ? CouponType.Floating : CouponType.Fixed;
        instrument.HasAmortization = bondization.Amortizations.Count > 1 || (info?.HasAmortizationHint ?? false);
        instrument.HasOffers = bondization.Offers.Count > 0;
        instrument.DataIncomplete = bondization.DataIncomplete || (info is null);

        await _instruments.UpsertAsync(instrument);

        if (bondization.Coupons.Count > 0)
        {
            await _coupons.ReplaceForInstrumentAsync(instrumentId, bondization.Coupons);
        }

        if (bondization.Amortizations.Count > 0)
        {
            await _amortizations.ReplaceForInstrumentAsync(instrumentId, bondization.Amortizations);
        }

        if (bondization.Offers.Count > 0)
        {
            await _offers.ReplaceForInstrumentAsync(instrumentId, bondization.Offers);
        }
    }

    /// <summary>
    /// Правило классификации <see cref="CouponType.Floating"/>. Баг в проде: Брус 2Р06
    /// (RU000A10EC63) — фикс-купон 22.75% (BONDTYPE «Биржевая облигация», COUPONPERCENT задан),
    /// put-оферта 2028-02-18. MOEX bondization отдаёт 24 известных купона до оферты и 12
    /// неизвестных после (ставка пересматривается на оферте — стандарт для put-оферт, не
    /// флоатер). Старое правило (<c>Coupons.Any(!IsKnown)</c>) считало флоатером любую фикс-бумагу
    /// с офертой и пересмотром ставки после неё.
    /// <para>
    /// Правильное правило: неизвестный купон делает бумагу флоатером, только если его дата НЕ
    /// ПОЗЖЕ ближайшей будущей оферты. Ближайшая оферта берётся через
    /// <see cref="OfferCutoffResolver.ResolveNearestOfferDate"/> — <b>не</b> через
    /// <see cref="OfferCutoffResolver.Resolve"/>. Первая версия этого фикса переиспользовала
    /// <c>Resolve</c>, который отфильтровывает оферты ближе
    /// <see cref="OfferCutoffResolver.MinDaysToOffer"/> дней — конвенция, уместная для горизонта
    /// расчёта доходности (spec §7.3), но не для классификации купонов: в окне
    /// [оферта−14д, оферта] <c>Resolve</c> считал оферту "слишком близкой" и откатывался к
    /// горизонту погашения, из-за чего баг Брус 2Р06 воспроизводился заново каждые две недели перед
    /// офертой (см. ревью T-36). <c>ResolveNearestOfferDate</c>
    /// ищет ближайшую будущую оферту исключительно по календарю, без этой отсечки.
    /// Если релевантной будущей оферты нет (нет оферт вовсе или все уже прошли) — правило
    /// откатывается к прежнему поведению: любой неизвестный купон = Floating.
    /// </para>
    /// <paramref name="looksLikeFloater"/> (BONDTYPE «Флоатер» / отсутствие COUPONPERCENT в
    /// securities.json) остаётся в OR как был — единственный сигнал для настоящих флоатеров, у
    /// которых ближайшие купоны ещё известны (ставка ещё не наступила к пересчёту).
    /// </summary>
    internal static bool HasFloatingCoupon(
        IReadOnlyList<CouponSchedule> coupons,
        IReadOnlyList<OfferSchedule> offers,
        DateOnly asOf,
        bool looksLikeFloater)
    {
        var nearestOfferDate = OfferCutoffResolver.ResolveNearestOfferDate(asOf, offers);

        var hasUnknownWithinHorizon = nearestOfferDate is { } offerDate
            ? coupons.Any(c => !c.IsKnown && c.CouponDate <= offerDate)
            : coupons.Any(c => !c.IsKnown);

        return hasUnknownWithinHorizon || looksLikeFloater;
    }
}

/// <summary>Итог одного вызова синка — для логирования/отображения владельцу (без секретов).</summary>
public sealed class SyncResult
{
    public int InstrumentsSynced { get; set; }
    public int OperationsUpserted { get; set; }

    /// <summary>Задача 51: сколько позиций в фондах (БПИФ) обновлено — отдельный домен, в <see cref="InstrumentsSynced"/> не входит.</summary>
    public int FundsSynced { get; set; }

    /// <summary>Задача 55: сколько валютных позиций (юани и т.п.) обновлено - отдельный домен, в <see cref="InstrumentsSynced"/> не входит.</summary>
    public int CurrenciesSynced { get; set; }

    /// <summary>Журнал операций полностью перечитан у брокера и сохранён (база для бэкфилла истории).</summary>
    public bool JournalSynced { get; set; }

    /// <summary>Сколько позиций удалено сверкой (у брокера их больше нет: погашены/проданы).</summary>
    public int PositionsClosed { get; set; }
    public bool YieldCurveUpdated { get; set; }

    /// <summary>
    /// Сколько операций в чужой валюте пропущено из-за отсутствия курса (T-56fix). Информационное поле: НЕ ошибка цикла и не попадает
    /// в <see cref="Errors"/> (иначе постоянный HasErrors -> ежедневный Telegram-алерт).
    /// </summary>
    public int SkippedForeignOperations { get; set; }

    /// <summary>Были пропуски из-за ещё не загруженной истории курса (самолечащийся случай): журнал неполон, <see cref="JournalSynced"/> = false.</summary>
    public bool JournalIncompleteByRates { get; set; }

    public List<string> Errors { get; } = [];

    public bool HasErrors => Errors.Count > 0;
}

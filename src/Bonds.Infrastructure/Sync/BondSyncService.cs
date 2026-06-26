using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Bonds.Infrastructure.Connectors.Moex;
using Bonds.Infrastructure.Connectors.TInvest;
using Microsoft.Extensions.Logging;

namespace Bonds.Infrastructure.Sync;

/// <summary>
/// Оркестратор синка (plan/04 Часть C). Один вызов <see cref="SyncAsync"/>:
/// (1) синк позиций+операций T-Invest → upsert в positions/operations;
/// (2) для каждого инструмента портфеля — догрузка расписаний/параметров из MOEX;
/// (3) обновление котировок (T-Invest = "сейчас") и снимка безрисковой кривой (MOEX Gcurve);
/// (4) пометки неполноты (DataIncomplete на Instrument/Position) — не падает на одном плохом
///     инструменте, остальной синк продолжается (устойчивость к частичным сбоям, см. SyncResult).
/// Не вызывается из HTTP (этап 08) и не планируется по расписанию (этап 07) — чистый сервис,
/// вызываемый программно/из тестов на этом этапе.
/// </summary>
public sealed class BondSyncService
{
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
    private readonly ILogger<BondSyncService> _logger;

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
        ILogger<BondSyncService> logger)
    {
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
        _logger = logger;
    }

    public async Task<SyncResult> SyncAsync(ulong accountId, DateTime? operationsFrom = null, CancellationToken ct = default)
    {
        var result = new SyncResult();

        var brokerAccountId = await _tInvest.GetPrimaryAccountIdAsync(ct);
        if (brokerAccountId is null)
        {
            result.Errors.Add("T-Invest: не удалось определить брокерский счёт (нет открытых счетов для токена)");
            return result;
        }

        // --- (1) Позиции + операции из T-Invest ---
        IReadOnlyList<TInvestPortfolioPosition> tInvestPositions;
        try
        {
            tInvestPositions = await _tInvest.GetBondPositionsAsync(brokerAccountId, ct);
        }
        catch (Exception ex)
        {
            // Полный сбой брокерского API — без позиций продолжать бессмысленно (нет инструментов
            // для дозагрузки MOEX-данных), но это не должно ронять процесс синка с unhandled exception.
            _logger.LogError(ex, "T-Invest: failed to fetch portfolio positions");
            result.Errors.Add("T-Invest: не удалось получить позиции портфеля");
            return result;
        }

        try
        {
            var tInvestOperations = await _tInvest.GetOperationsAsync(brokerAccountId, operationsFrom, ct);
            var domainOperations = new List<Operation>();
            foreach (var op in tInvestOperations)
            {
                var mappedType = TInvestOperationMapper.Map(op.OperationType);
                if (mappedType is null) continue; // нерелевантный тип операции (см. README коннектора) — пропускаем, не падаем

                ulong? instrumentId = null;
                if (!string.IsNullOrEmpty(op.Figi))
                {
                    var instrument = await _instruments.GetByFigiAsync(op.Figi);
                    instrumentId = instrument?.Id;
                }

                domainOperations.Add(new Operation
                {
                    AccountId = accountId,
                    InstrumentId = instrumentId,
                    Type = mappedType.Value,
                    Date = op.Date,
                    AmountRub = op.PaymentRub,
                    Quantity = op.Quantity,
                    ExternalId = op.Id,
                });
            }

            if (domainOperations.Count > 0)
            {
                result.OperationsUpserted = await _operations.UpsertManyByExternalIdAsync(domainOperations);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "T-Invest: failed to sync operations journal");
            result.Errors.Add("T-Invest: не удалось синхронизировать журнал операций");
            // Не return — позиции и справочник всё равно стоит обновить (частичный успех).
        }

        // --- (2)+(4) Для каждой позиции — резолв инструмента + догрузка MOEX + upsert позиции ---
        var resolvedInstruments = new List<(ulong InstrumentId, string Figi)>();
        foreach (var tip in tInvestPositions)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var instrumentId = await ResolveOrCreateInstrumentAsync(tip.Figi, ct);
                if (instrumentId is null)
                {
                    result.Errors.Add($"Не удалось определить инструмент для FIGI {tip.Figi} — позиция пропущена");
                    continue;
                }

                await EnrichFromMoexAsync(instrumentId.Value, ct);

                await _positions.UpsertAsync(new Position
                {
                    AccountId = accountId,
                    InstrumentId = instrumentId.Value,
                    Quantity = tip.Quantity,
                    AvgPurchasePrice = tip.AveragePositionPrice,
                    Accrued = tip.CurrentNkd ?? 0m,
                    // Позиция считается неполной, если T-Invest не отдал НКД по открытой позиции —
                    // движок (этап 05) не должен молча считать НКД=0 как достоверный факт (spec §4.4).
                    DataIncomplete = tip.CurrentNkd is null,
                });

                var quotedFromPortfolio = false;
                if (tip.CurrentPrice is not null)
                {
                    await _quotes.UpsertAsync(new MarketQuote
                    {
                        InstrumentId = instrumentId.Value,
                        AsOf = DateOnly.FromDateTime(DateTime.UtcNow),
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
                // Устойчивость к частичному сбою (plan/04 Часть C): один упавший инструмент
                // не должен ронять синк остальных позиций портфеля.
                _logger.LogWarning(ex, "Failed to sync instrument for FIGI {Figi} — skipping, continuing with the rest", tip.Figi);
                result.Errors.Add($"Инструмент FIGI {tip.Figi}: ошибка синка ({ex.GetType().Name})");
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

                    // q.BestBid/q.BestAsk (стакан) намеренно НЕ пишутся здесь — это осознанное
                    // решение, не недосмотр. Спека §8 относит "предупреждение о низкой
                    // ликвидности по позиции (тонкий стакан)" явно к категории "на будущее, при
                    // появлении планирования вывода" — вне MVP. MarketQuote (этап 03) не несёт
                    // полей под bid/ask: добавлять миграцию/колонки под данные, которые сейчас
                    // никто не читает, было бы преждевременным расширением схемы. Стакан всё
                    // равно ЗАПРАШИВАЕТСЯ у T-Invest (ITInvestPortfolioClient.GetQuotesAsync) —
                    // это сознательно сохранено, а не убрано, потому что Signal.LowLiquidityWarning
                    // (см. Bonds.Core.Models.Signal) уже существует как точка расширения для
                    // этапа 07 — когда сигнал понадобится, его реализация сможет читать
                    // q.BestBid/q.BestAsk прямо из ITInvestPortfolioClient без отдельного похода
                    // в API заново. Если этап 07 решит, что нужна персистентность стакана
                    // (история, а не только "сейчас") — тогда и добавить миграцию полей.
                    await _quotes.UpsertAsync(new MarketQuote
                    {
                        InstrumentId = instrumentId,
                        AsOf = DateOnly.FromDateTime(DateTime.UtcNow),
                        CleanPrice = q.LastPrice,
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
            MaturityDate = DateOnly.FromDateTime(DateTime.UtcNow),
            DataIncomplete = true,
        };

        var id = await _instruments.UpsertAsync(placeholder);
        return id;
    }

    /// <summary>
    /// Догружает/обновляет справочные данные инструмента из MOEX: резолвит SECID (если ещё не
    /// закэширован), параметры выпуска, купоны/амортизации/оферты, помечает DataIncomplete.
    /// Источник истины по расписаниям — MOEX, даже если T-Invest что-то задублировал (преамбула plan/04).
    /// </summary>
    private async Task EnrichFromMoexAsync(ulong instrumentId, CancellationToken ct)
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
            instrument.IsOutOfScopeCurrency = info.FaceUnit is not null
                && !string.Equals(info.FaceUnit, "SUR", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(info.FaceUnit, "RUB", StringComparison.OrdinalIgnoreCase);
        }

        instrument.Name = info?.ShortName ?? info?.SecName ?? instrument.Isin;

        var searchInfo = await _moex.GetSecuritySearchAsync(instrument.Isin, ct);
        instrument.Issuer = searchInfo?.EmitentTitle ?? instrument.Issuer;
        instrument.Sector = MoexSegmentMapper.MapTypeToSegment(searchInfo?.TypeCode) ?? instrument.Sector;

        // CouponType/HasAmortization/HasOffers выводятся из ФАКТИЧЕСКОГО графика bondization
        // (приоритетнее эвристики BONDTYPE из securities.json — последняя используется только
        // как fallback, если bondization не вернул купонов вовсе).
        var hasFloatingCoupon = bondization.Coupons.Any(c => !c.IsKnown) || (info?.LooksLikeFloater ?? false);
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
}

/// <summary>Итог одного вызова синка — для логирования/отображения владельцу (без секретов).</summary>
public sealed class SyncResult
{
    public int InstrumentsSynced { get; set; }
    public int OperationsUpserted { get; set; }
    public bool YieldCurveUpdated { get; set; }
    public List<string> Errors { get; } = [];

    public bool HasErrors => Errors.Count > 0;
}

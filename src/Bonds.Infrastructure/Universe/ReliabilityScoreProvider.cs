using Bonds.Core.Analytics;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Bonds.Core.Time;
using Bonds.Core.Universe;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bonds.Infrastructure.Universe;

/// <summary>
/// Готовая скоркарта надёжности ВСЕГО банка облигаций по secid (задача 46). Считается раз в
/// <see cref="BondUniverseRefreshOptions.RelativeValueCacheDuration"/> провайдером <see cref="ReliabilityScoreProvider"/>.
/// </summary>
public sealed class ReliabilityBook
{
    private readonly IReadOnlyDictionary<string, ReliabilityScore> _bySecid;
    private readonly IReadOnlyDictionary<string, string> _secidByIsin;
    private readonly ReliabilityScore _empty;

    public ReliabilityBook(
        IReadOnlyDictionary<string, ReliabilityScore> bySecid,
        IReadOnlyDictionary<string, string> secidByIsin,
        ReliabilityScore empty)
    {
        _bySecid = bySecid;
        _secidByIsin = secidByIsin;
        _empty = empty;
    }

    /// <summary>Скоркарта бумаги по secid; для неизвестного secid — <see cref="ReliabilityScoreService.Unknown"/>
    /// (Yellow, без компонентов), а не null: потребителям не нужна ветка «нет оценки».</summary>
    public ReliabilityScore Get(string? secid) =>
        secid is not null && _bySecid.TryGetValue(secid, out var score) ? score : _empty;

    public bool TryGetBySecid(string secid, out ReliabilityScore score) => _bySecid.TryGetValue(secid, out score!);

    public bool TryGetByIsin(string isin, out ReliabilityScore score)
    {
        score = _empty;
        return _secidByIsin.TryGetValue(isin, out var secid) && _bySecid.TryGetValue(secid, out score!);
    }

    public int Count => _bySecid.Count;
}

/// <summary>
/// Сборка входов скоркарты (<see cref="ReliabilityScoreService"/>) БАТЧЕВО для всего банка и кэш результата —
/// без N+1: по одному запросу на справочник (карточки, дефолт-агрегаты, отчётность, статусы ГИР БО, рейтинги,
/// risk_level, 20 дней истории). Паттерн кэша/Singleton — как <see cref="RelativeValueSnapshotBuilder"/>
/// (Scoped-репозитории резолвятся через <see cref="IServiceScopeFactory"/>; длительность кэша — та же опция,
/// интеграционные тесты её обнуляют). Ручной ввод (рейтинги/отчётность) сбрасывает кэш через <see cref="Invalidate"/>.
/// </summary>
public sealed class ReliabilityScoreProvider
{
    /// <summary>Сколько последних торговых дней истории читать для динамики спреда и просадки цены.</summary>
    public const int HistoryTradingDays = 20;

    /// <summary>Сколько лет отчётности брать на ИНН (тренд — по ≤ 3 годам).</summary>
    public const int FinancialsYears = 3;

    private const int ChunkSize = 1000;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RelativeValueSnapshotBuilder _snapshotBuilder;
    private readonly TimeSpan _cacheDuration;
    private readonly ILogger<ReliabilityScoreProvider> _logger;
    private readonly SemaphoreSlim _buildGate = new(1, 1);

    private ReliabilityBook? _cached;
    private DateTime _cachedAtUtc;

    public ReliabilityScoreProvider(
        IServiceScopeFactory scopeFactory,
        RelativeValueSnapshotBuilder snapshotBuilder,
        IOptions<BondUniverseRefreshOptions> options,
        ILogger<ReliabilityScoreProvider> logger)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _snapshotBuilder = snapshotBuilder;
        _cacheDuration = options.Value.RelativeValueCacheDuration;
    }

    /// <summary>Сбрасывает кэш: следующий вызов пересчитает скоркарты (после ручного ввода рейтинга/отчётности).</summary>
    public void Invalidate() => Volatile.Write(ref _cached, null);

    public async Task<ReliabilityBook> GetBookAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        if (Volatile.Read(ref _cached) is { } hit && now - _cachedAtUtc < _cacheDuration)
        {
            return hit;
        }

        await _buildGate.WaitAsync(ct);
        try
        {
            now = DateTime.UtcNow;
            if (Volatile.Read(ref _cached) is { } again && now - _cachedAtUtc < _cacheDuration)
            {
                return again;
            }

            var book = await BuildAsync(ct);
            _cached = book;
            _cachedAtUtc = now;
            return book;
        }
        finally
        {
            _buildGate.Release();
        }
    }

    private async Task<ReliabilityBook> BuildAsync(CancellationToken ct)
    {
        var snapshot = await _snapshotBuilder.GetSnapshotAsync(ct);
        var today = BusinessClock.MoscowToday();
        var entries = snapshot.CurrentEntriesBySecid.Values.ToList();

        using var scope = _scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        var universeRepo = sp.GetRequiredService<IBondUniverseRepository>();
        var detailsRepo = sp.GetRequiredService<ISecurityDetailsRepository>();
        var financialsRepo = sp.GetRequiredService<IIssuerFinancialsRepository>();
        var ratingRepo = sp.GetRequiredService<IIssuerRatingRepository>();
        var riskRepo = sp.GetRequiredService<IInstrumentRiskLevelRepository>();

        // Карточки бумаг (флаги дефолтов, квал-инвесторы) — по secid.
        var details = new Dictionary<string, SecurityDetails>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in entries.Select(e => e.Secid).Chunk(ChunkSize))
        {
            foreach (var d in await detailsRepo.GetBySecidsAsync(chunk, ct)) details[d.Secid] = d;
        }

        // Дефолт-агрегаты по ВСЕМ выпускам эмитента (EMITTER_ID банка, при его отсутствии — из карточки).
        int? EmitentIdOf(BondUniverseEntry e) => e.EmitentId ?? (details.TryGetValue(e.Secid, out var d) ? d.EmitentId : null);
        var emitentIds = entries.Select(EmitentIdOf).Where(id => id is not null).Select(id => id!.Value).Distinct().ToList();
        var defaults = new Dictionary<int, EmitentDefaultHistory>();
        foreach (var chunk in emitentIds.Chunk(ChunkSize))
        {
            foreach (var (emitentKey, aggregate) in await detailsRepo.GetEmitentDefaultHistoryAsync(chunk, ct)) defaults[emitentKey] = aggregate;
        }

        // Отчётность, статусы ГИР БО, ручные рейтинги — по ИНН.
        var inns = entries.Select(e => e.EmitentInn).Where(inn => !string.IsNullOrWhiteSpace(inn)).Select(inn => inn!.Trim()).Distinct().ToList();
        var financials = new Dictionary<string, IReadOnlyList<IssuerFinancials>>();
        var statuses = new Dictionary<string, IssuerFinancialsStatus>();
        var ratings = new Dictionary<string, IReadOnlyList<IssuerRating>>();
        foreach (var chunk in inns.Chunk(ChunkSize))
        {
            foreach (var (inn, rows) in await financialsRepo.GetLatestByInnsAsync(chunk, FinancialsYears, ct)) financials[inn] = rows;
            foreach (var (inn, status) in await financialsRepo.GetStatusesAsync(chunk, ct)) statuses[inn] = status;
            foreach (var (inn, rows) in await ratingRepo.GetByInnsAsync(chunk, ct)) ratings[inn] = rows;
        }

        var riskLevels = await riskRepo.GetAllAsync(ct);

        // История последних 20 торговых дней (ОДИН запрос на весь банк), по secid, без сегодняшней строки:
        // «сегодня» берётся из текущего снимка, а в историю оно попадает только вечером.
        var history = (await universeRepo.GetRecentHistoryAsync(HistoryTradingDays, ct))
            .Where(h => h.SnapshotDate < today)
            .GroupBy(h => h.Secid, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(h => h.SnapshotDate).ToList(), StringComparer.OrdinalIgnoreCase);

        var bySecid = new Dictionary<string, ReliabilityScore>(entries.Count, StringComparer.OrdinalIgnoreCase);
        var secidByIsin = new Dictionary<string, string>(entries.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            details.TryGetValue(entry.Secid, out var card);
            var emitentId = EmitentIdOf(entry);
            var issuerWide = emitentId is { } id && defaults.ContainsKey(id);
            defaults.TryGetValue(emitentId ?? -1, out var emitentDefaults);
            var inn = string.IsNullOrWhiteSpace(entry.EmitentInn) ? null : entry.EmitentInn.Trim();
            history.TryGetValue(entry.Secid, out var points);
            points ??= [];

            var liquidity = LiquidityScoreCalculator.Assess(snapshot.LiquidityTurnoverRub(entry), entry.BidPercent, entry.OfferPercent, entry.NumTrades);
            var liquiditySignal = CandidateRiskSignalService.AssessLiquidity(liquidity.Score, entry.ListLevel).Level;

            BrokerRiskLevel? brokerLevel = entry.Isin is not null && riskLevels.TryGetValue(entry.Isin, out var level) ? level : null;

            var input = new ReliabilityScoreInput
            {
                Today = today,
                Sector = entry.Sector,
                IsFloater = entry.IsFloater == true,
                IssuerHasDefault = issuerWide ? emitentDefaults!.HasDefault : card?.HasDefault,
                IssuerHasTechnicalDefault = issuerWide ? emitentDefaults!.HasTechnicalDefault : card?.HasTechnicalDefault,
                DefaultHistoryIsIssuerWide = issuerWide,
                ListLevel = entry.ListLevel,
                OfferDate = entry.OfferDate,
                IsQualifiedInvestors = card?.IsQualifiedInvestors,
                BrokerRiskLevel = brokerLevel,
                Ratings = inn is not null && ratings.TryGetValue(inn, out var issuerRatings)
                    ? issuerRatings.Select(r => new IssuerRatingInput(r.Agency, r.Rating, r.RatingDate)).ToList()
                    : null,
                Financials = inn is not null && financials.TryGetValue(inn, out var rows) ? rows : null,
                FinancialsStatus = inn is not null && statuses.TryGetValue(inn, out var status) ? status.Status : null,
                GSpreadFraction = entry.EffectiveGspreadFraction,
                GSpreadHistory = points.Select(p => p.ResolvedGspreadFraction).Where(v => v is not null).Select(v => v!.Value).ToList(),
                PricePercent = entry.PricePercent,
                PriceHistory = points.Select(p => p.PricePercent).Where(v => v is not null).Select(v => v!.Value).ToList(),
                Liquidity = liquiditySignal,
                LiquidityScoreRaw = liquidity.Score,
                TurnoverMedianRub = snapshot.LiquidityTurnoverRub(entry),
            };

            // Сбой расчёта ОДНОЙ бумаги (мусорные данные в отчётности и т.п.) не должен ронять скоркарты всего банка:
            // откат этой бумаги к «неизвестной» (Yellow без компонентов) и предупреждение в лог.
            try
            {
                bySecid[entry.Secid] = ReliabilityScoreService.Evaluate(input);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Скоркарта надёжности: расчёт по {Secid} не удался, бумага помечена как неоценённая", entry.Secid);
                bySecid[entry.Secid] = ReliabilityScoreService.Unknown("Жёлтый: расчёт надёжности по этой бумаге не удался (некорректные входные данные) — оценка недоступна.");
            }

            if (!string.IsNullOrWhiteSpace(entry.Isin)) secidByIsin[entry.Isin] = entry.Secid;
        }

        return new ReliabilityBook(bySecid, secidByIsin, ReliabilityScoreService.Unknown());
    }
}

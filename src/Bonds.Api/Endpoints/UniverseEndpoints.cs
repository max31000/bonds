using System.Security.Claims;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Bonds.Core.Time;
using Bonds.Core.Universe;
using Bonds.Infrastructure.Analytics;
using Bonds.Infrastructure.Sync;
using Microsoft.Extensions.Options;

namespace Bonds.Api.Endpoints;

/// <summary>
/// Задача 26 часть D — банк облигаций MOEX: контракт API для задач 27-30 (выпадашка-сравнивалка,
/// скринер, конструктор, relative value). GET /api/universe отдаёт дешёвую биржевую статистику
/// (YIELD/DURATION/обороты/листинг), НЕ прогоняя банк через точный движок BondMetricsCalculator
/// (двухъярусная архитектура, см. doc-comment plan/26). Оба эндпоинта — под общей FallbackPolicy
/// (авторизация обязательна), как все доменные эндпоинты этого сервиса.
/// </summary>
public static class UniverseEndpoints
{
    public static void MapUniverseEndpoints(this WebApplication app)
    {
        app.MapGet("/api/universe", GetUniverse);
        app.MapGet("/api/universe/status", GetUniverseStatus);
        app.MapPost("/api/universe/{secid}/materialize", PostMaterialize);
    }

    public const string Disclaimer =
        "Метрики банка облигаций (доходность, дюрация, приближённый G-спред, скор ликвидности) — " +
        "биржевая статистика MOEX, не результат точного расчётного движка и не инвестиционная рекомендация.";

    // ─── GET /api/universe ────────────────────────────────────────────────────────────────────

    private static async Task<IResult> GetUniverse(
        ClaimsPrincipal principal,
        IBondUniverseRepository universeRepo,
        IAccountRepository accountRepo,
        IPositionRepository positionRepo,
        IInstrumentRepository instrumentRepo,
        IWatchlistItemRepository watchlistRepo,
        IOptions<Bonds.Infrastructure.Universe.BondUniverseRefreshOptions> universeOptions,
        string? search,
        decimal? minDurationYears,
        decimal? maxDurationYears,
        decimal? minYield,
        decimal? maxYield,
        string? sector,
        bool? includeHidden,
        string? sortBy,
        string? sortDir,
        int? limit,
        int? offset)
    {
        var all = await universeRepo.GetAllAsync();
        var hygieneOptions = universeOptions.Value.Hygiene;
        var today = BusinessClock.MoscowToday();

        // Флаги причины скрытия считаются на ВСЕЙ вселенной (до текстового/числового фильтра) —
        // hiddenCount в ответе должен отражать реальную гигиену банка, а не "скрыто среди
        // отфильтрованных", иначе число прыгало бы вместе с поиском/фильтрами без видимой причины.
        var withHygiene = all
            .Select(e => (Entry: e, HiddenReason: UniverseHygieneFilter.Evaluate(e, hygieneOptions, today)))
            .ToList();
        var hiddenCount = withHygiene.Count(x => x.HiddenReason != HygieneHiddenReason.None);

        IEnumerable<(BondUniverseEntry Entry, HygieneHiddenReason HiddenReason)> query = withHygiene;
        if (includeHidden != true)
        {
            query = query.Where(x => x.HiddenReason == HygieneHiddenReason.None);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var needle = search.Trim();
            query = query.Where(x =>
                Contains(x.Entry.ShortName, needle) ||
                Contains(x.Entry.SecName, needle) ||
                Contains(x.Entry.Isin, needle) ||
                Contains(x.Entry.Secid, needle));
        }

        if (minDurationYears is { } minDur) query = query.Where(x => x.Entry.DurationYears is { } d && d >= minDur);
        if (maxDurationYears is { } maxDur) query = query.Where(x => x.Entry.DurationYears is { } d && d <= maxDur);
        if (minYield is { } minY) query = query.Where(x => x.Entry.YieldFraction is { } y && y >= minY);
        if (maxYield is { } maxY) query = query.Where(x => x.Entry.YieldFraction is { } y && y <= maxY);
        if (!string.IsNullOrWhiteSpace(sector)) query = query.Where(x => string.Equals(x.Entry.Sector, sector, StringComparison.OrdinalIgnoreCase));

        var materialized = query.ToList();
        var total = materialized.Count;

        materialized = SortRows(materialized, sortBy, sortDir);

        var effectiveOffset = Math.Max(offset ?? 0, 0);
        var effectiveLimit = Math.Clamp(limit ?? 50, 1, 500);
        var page = materialized.Skip(effectiveOffset).Take(effectiveLimit).ToList();

        // Флаги inPortfolio/inWatchlist — join по ISIN (план часть D.1). Single-user продукт: один
        // primary account, один эффективный "пользователь" watchlist — тот же принцип, что
        // PositionsEndpoints.ResolveAccountIdAsync/WatchlistEndpoints.
        var portfolioIsins = await ResolvePortfolioIsinsAsync(accountRepo, positionRepo, instrumentRepo);
        var watchlistIsins = await ResolveWatchlistIsinsAsync(principal, watchlistRepo);

        var rows = page.Select(x => ToRowDto(x.Entry, x.HiddenReason, portfolioIsins, watchlistIsins)).ToList();

        return Results.Ok(new UniverseResponseDto
        {
            Rows = rows,
            Total = total,
            HiddenCount = hiddenCount,
            Disclaimer = Disclaimer,
        });
    }

    private static bool Contains(string? haystack, string needle) =>
        haystack is not null && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static List<(BondUniverseEntry Entry, HygieneHiddenReason HiddenReason)> SortRows(
        List<(BondUniverseEntry Entry, HygieneHiddenReason HiddenReason)> rows, string? sortBy, string? sortDir)
    {
        var descending = string.Equals(sortDir, "desc", StringComparison.OrdinalIgnoreCase);

        Func<(BondUniverseEntry Entry, HygieneHiddenReason HiddenReason), decimal?> keySelector = sortBy?.ToLowerInvariant() switch
        {
            "duration" => x => x.Entry.DurationYears,
            "turnover" => x => x.Entry.TurnoverRub,
            "gspread" => x => x.Entry.GspreadApproxFraction,
            "yield" => x => x.Entry.YieldFraction,
            _ => x => x.Entry.YieldFraction, // дефолт — сортировка по доходности (наиболее востребованный сценарий скринера).
        };

        // Null трактуется как "меньше всего" (нет данных — в конец списка при desc, в начало при asc)
        // через явную проекцию на (HasValue, Value), а не полагаемся на дефолтный Nullable-компаратор.
        IOrderedEnumerable<(BondUniverseEntry Entry, HygieneHiddenReason HiddenReason)> ordered = descending
            ? rows.OrderByDescending(x => keySelector(x).HasValue).ThenByDescending(x => keySelector(x) ?? decimal.MinValue)
            : rows.OrderByDescending(x => keySelector(x).HasValue).ThenBy(x => keySelector(x) ?? decimal.MaxValue);

        return ordered.ToList();
    }

    private static async Task<HashSet<string>> ResolvePortfolioIsinsAsync(
        IAccountRepository accountRepo, IPositionRepository positionRepo, IInstrumentRepository instrumentRepo)
    {
        var accountId = await accountRepo.GetPrimaryAccountIdAsync();
        if (accountId is null) return [];

        var positions = (await positionRepo.GetByAccountIdAsync(accountId.Value))
            .Where(p => p.Quantity != 0)
            .ToList();
        if (positions.Count == 0) return [];

        var isins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var position in positions)
        {
            var instrument = await instrumentRepo.GetByIdAsync(position.InstrumentId);
            if (instrument is not null) isins.Add(instrument.Isin);
        }

        return isins;
    }

    private static async Task<HashSet<string>> ResolveWatchlistIsinsAsync(ClaimsPrincipal principal, IWatchlistItemRepository watchlistRepo)
    {
        var sub = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!ulong.TryParse(sub, out var userId)) return [];

        var items = await watchlistRepo.GetByUserIdAsync(userId);
        return new HashSet<string>(items.Select(i => i.Isin), StringComparer.OrdinalIgnoreCase);
    }

    private static UniverseRowDto ToRowDto(
        BondUniverseEntry entry, HygieneHiddenReason hiddenReason, HashSet<string> portfolioIsins, HashSet<string> watchlistIsins)
    {
        var liquidity = LiquidityScoreCalculator.Assess(entry.TurnoverRub, entry.BidPercent, entry.OfferPercent, entry.NumTrades);
        var isInPortfolio = entry.Isin is not null && portfolioIsins.Contains(entry.Isin);
        var isInWatchlist = entry.Isin is not null && watchlistIsins.Contains(entry.Isin);

        return new UniverseRowDto
        {
            Secid = entry.Secid,
            Isin = entry.Isin,
            Name = entry.ShortName ?? entry.SecName,
            Sector = entry.Sector,
            YieldFraction = entry.YieldFraction,
            DurationYears = entry.DurationYears,
            PricePercent = entry.PricePercent,
            TurnoverRub = entry.TurnoverRub,
            ListLevel = entry.ListLevel,
            LiquidityScore = liquidity.Score.ToString(),
            SlippageEstimateFraction = liquidity.SlippageEstimateFraction,
            GspreadApproxFraction = entry.GspreadApproxFraction,
            MaturityDate = entry.MaturityDate,
            OfferDate = entry.OfferDate,
            IsHidden = hiddenReason != HygieneHiddenReason.None,
            HiddenReason = hiddenReason == HygieneHiddenReason.None ? null : hiddenReason.ToString(),
            InPortfolio = isInPortfolio,
            InWatchlist = isInWatchlist,
        };
    }

    // ─── GET /api/universe/status ─────────────────────────────────────────────────────────────

    private static async Task<IResult> GetUniverseStatus(
        IBondUniverseRepository universeRepo,
        IOptions<Bonds.Infrastructure.Universe.BondUniverseRefreshOptions> universeOptions)
    {
        var lastRefreshUtc = await universeRepo.GetLastRefreshUtcAsync();
        var total = await universeRepo.CountAsync();
        var historyDays = await universeRepo.GetHistoryDaysCountAsync();

        var hygieneOptions = universeOptions.Value.Hygiene;
        var today = BusinessClock.MoscowToday();
        var all = await universeRepo.GetAllAsync();
        var hiddenCount = all.Count(e => UniverseHygieneFilter.Evaluate(e, hygieneOptions, today) != HygieneHiddenReason.None);

        return Results.Ok(new UniverseStatusDto
        {
            LastRefreshUtc = lastRefreshUtc,
            TotalBonds = total,
            HiddenBonds = hiddenCount,
            HistoryDays = historyDays,
        });
    }

    // ─── POST /api/universe/{secid}/materialize ────────────────────────────────────────────────

    /// <summary>
    /// Задача 27 часть A: превращает биржевую статистику банка (GET /api/universe, дешёвое) в
    /// точную бумагу нашего движка — резолвит ISIN из <c>bond_universe</c> по <paramref name="secid"/>,
    /// заводит/находит <see cref="Instrument"/> + котировку через
    /// <see cref="InstrumentEnrichmentService.EnrichByIsinAsync"/> (ОДИН путь с watchlist, см.
    /// doc-comment сервиса), затем считает полные метрики тем же способом, что watchlist/матрица
    /// замен (<see cref="PortfolioHoldingsBuilder.BuildForInstrumentsAsync"/>). НЕ создаёт
    /// watchlist-запись — это отдельное явное действие пользователя (кнопка "В watchlist" на
    /// карточке результата фронта, POST /api/watchlist).
    /// <para>
    /// Идемпотентно: повторный вызов на тот же SECID не плодит дублей Instrument
    /// (ResolveOrCreateInstrumentByIsinAsync находит существующий по ISIN), только обновляет
    /// котировку.
    /// </para>
    /// </summary>
    private static async Task<IResult> PostMaterialize(
        string secid,
        IBondUniverseRepository universeRepo,
        InstrumentEnrichmentService enrichment,
        IInstrumentRepository instrumentRepo,
        PortfolioHoldingsBuilder holdingsBuilder,
        CancellationToken ct)
    {
        var all = await universeRepo.GetAllAsync(ct);
        var entry = all.FirstOrDefault(e => string.Equals(e.Secid, secid, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            return MaterializeError($"Бумага {secid} не найдена в банке облигаций");
        }

        if (string.IsNullOrWhiteSpace(entry.Isin))
        {
            return MaterializeError($"У бумаги {secid} нет ISIN в банке облигаций — материализация невозможна");
        }

        var instrumentId = await enrichment.EnrichByIsinAsync(entry.Isin, ct);
        if (instrumentId is null)
        {
            return MaterializeError($"Бумага {secid} (ISIN {entry.Isin}) не найдена на MOEX или это не облигация");
        }

        var instrument = await instrumentRepo.GetByIdAsync(instrumentId.Value);
        if (instrument is null)
        {
            // Не должно происходить — EnrichByIsinAsync только что создал/нашёл запись. Защита от гонки.
            return MaterializeError($"Инструмент для {secid} не удалось прочитать после материализации");
        }

        if (string.IsNullOrEmpty(instrument.Secid))
        {
            // EnrichByIsinAsync заводит placeholder-Instrument (DataIncomplete=true) даже когда ISIN
            // не резолвится на MOEX (тот же путь, что у watchlist, см. doc-comment
            // BondSyncService.ResolveOrCreateInstrumentByIsinAsync) — для materialize из банка это
            // именно "бумага не нашлась на MOEX", а не частично неполные данные, поэтому 422, не 200
            // с dataIncomplete=true (план требует человекочитаемую причину отказа).
            return MaterializeError($"Бумага {secid} (ISIN {entry.Isin}) не найдена на MOEX или это не облигация");
        }

        var asOf = BusinessClock.MoscowToday();
        var holdings = await holdingsBuilder.BuildForInstrumentsAsync([instrumentId.Value], asOf, ct);
        var holding = holdings.FirstOrDefault(h => h.InstrumentId == instrumentId.Value);

        if (holding is null)
        {
            // Инструмент заведён, но движок не смог посчитать holding (например, IsOutOfScopeCurrency
            // либо ссылочная целостность нарушена между вызовами) — 422 с явной причиной, не 500.
            return MaterializeError($"Не удалось посчитать метрики для {secid} — данные неполные или бумага вне контура расчёта");
        }

        var dto = new MaterializeResponseDto
        {
            InstrumentId = instrumentId.Value,
            Secid = secid,
            Isin = entry.Isin,
            Metrics = new MaterializeMetricsDto
            {
                Name = holding.Name,
                Issuer = holding.Issuer,
                Sector = holding.Sector,
                CouponType = holding.CouponType.ToString(),
                MaturityDate = instrument.MaturityDate,
                HorizonDate = holding.HorizonDate,
                CalculatedToOffer = holding.IsCalculatedToOffer,
                ModifiedDuration = holding.ModifiedDuration,
                MacaulayDuration = holding.MacaulayDuration,
                YtmEffective = holding.YtmEffective,
                CurrentYield = holding.CurrentYield,
                EffectiveYield = (holding.IsFloater || holding.IsIndexed) ? holding.CurrentYield : holding.YtmEffective,
                GSpread = holding.GSpread,
                IsFloater = holding.IsFloater,
                IsIndexed = holding.IsIndexed,
                IsEstimated = holding.IsEstimated,
                DataIncomplete = holding.DataIncomplete,
            },
            Disclaimer = WatchlistEndpoints.WatchlistDisclaimer,
        };

        return Results.Ok(dto);
    }

    private static IResult MaterializeError(string message) =>
        Results.Json(new { error = message, type = "ValidationException" }, statusCode: StatusCodes.Status422UnprocessableEntity);
}

public sealed record UniverseResponseDto
{
    public required IReadOnlyList<UniverseRowDto> Rows { get; init; }
    public required int Total { get; init; }
    public required int HiddenCount { get; init; }
    public required string Disclaimer { get; init; }
}

public sealed record UniverseRowDto
{
    public required string Secid { get; init; }
    public string? Isin { get; init; }
    public string? Name { get; init; }
    public string? Sector { get; init; }
    public decimal? YieldFraction { get; init; }
    public decimal? DurationYears { get; init; }
    public decimal? PricePercent { get; init; }
    public decimal? TurnoverRub { get; init; }
    public int? ListLevel { get; init; }
    public required string LiquidityScore { get; init; }
    public decimal? SlippageEstimateFraction { get; init; }
    public decimal? GspreadApproxFraction { get; init; }
    public DateOnly? MaturityDate { get; init; }
    public DateOnly? OfferDate { get; init; }
    public required bool IsHidden { get; init; }
    public string? HiddenReason { get; init; }
    public required bool InPortfolio { get; init; }
    public required bool InWatchlist { get; init; }
}

public sealed record UniverseStatusDto
{
    public DateTime? LastRefreshUtc { get; init; }
    public required int TotalBonds { get; init; }
    public required int HiddenBonds { get; init; }
    public required int HistoryDays { get; init; }
}

/// <summary>Задача 27 часть A — ответ POST /api/universe/{secid}/materialize.</summary>
public sealed record MaterializeResponseDto
{
    public required ulong InstrumentId { get; init; }
    public required string Secid { get; init; }
    public required string Isin { get; init; }
    public required MaterializeMetricsDto Metrics { get; init; }
    public required string Disclaimer { get; init; }
}

/// <summary>Полные метрики движка для материализованной бумаги — те же поля, что WatchlistItemDto (тот же расчётный путь).</summary>
public sealed record MaterializeMetricsDto
{
    public string? Name { get; init; }
    public string? Issuer { get; init; }
    public string? Sector { get; init; }
    public required string CouponType { get; init; }
    public required DateOnly MaturityDate { get; init; }
    public required DateOnly HorizonDate { get; init; }
    public required bool CalculatedToOffer { get; init; }
    public decimal? ModifiedDuration { get; init; }
    public decimal? MacaulayDuration { get; init; }
    public decimal? YtmEffective { get; init; }
    public decimal? CurrentYield { get; init; }

    /// <summary>YTM либо CurrentYield для флоатера/индексируемой — то же правило, что WatchlistItemDto/ScatterPointDto.</summary>
    public decimal? EffectiveYield { get; init; }
    public decimal? GSpread { get; init; }
    public required bool IsFloater { get; init; }
    public required bool IsIndexed { get; init; }
    public required bool IsEstimated { get; init; }
    public required bool DataIncomplete { get; init; }
}

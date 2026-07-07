using System.Security.Claims;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Bonds.Core.Time;
using Bonds.Core.Universe;
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

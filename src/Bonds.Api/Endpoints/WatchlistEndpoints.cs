using System.Security.Claims;
using Bonds.Api.Middleware;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Bonds.Core.Time;
using Bonds.Infrastructure.Analytics;
using Bonds.Infrastructure.Connectors.Moex;
using Bonds.Infrastructure.Sync;

namespace Bonds.Api.Endpoints;

/// <summary>
/// Задача 20 (часть A): ручной watchlist — бумаги вне текущих позиций, отслеживаемые по ISIN
/// (НЕ скринер по всей вселенной, см. doc-comment <see cref="Bonds.Core.Analytics.ICandidateScreener"/>).
/// GET отдаёт полный набор метрик тем же расчётным путём, что у позиций
/// (<see cref="PortfolioHoldingsBuilder.BuildForInstrumentsAsync"/> → <see cref="Bonds.Core.Calculation.BondMetricsCalculator"/>).
/// POST валидирует ISIN на MOEX синхронно (422, если не найден/не облигация) и сразу обогащает
/// инструмент тем же путём, что <see cref="Bonds.Infrastructure.Sync.BondSyncService"/> — бумага
/// появляется с метриками сразу после добавления, не дожидаясь следующего цикла синка.
/// </summary>
public static class WatchlistEndpoints
{
    public static void MapWatchlistEndpoints(this WebApplication app)
    {
        app.MapGet("/api/watchlist", GetWatchlist);
        app.MapPost("/api/watchlist", PostWatchlist);
        app.MapDelete("/api/watchlist/{id}", DeleteWatchlist);
    }

    /// <summary>Дисклеймер watchlist — отдельная формулировка от Disclaimers.Metrics (позиции),
    /// т.к. явно уточняет, что бумага не входит в портфель.</summary>
    internal const string WatchlistDisclaimer =
        "Метрики watchlist-бумаг (YTM, дюрация, G-спред и т.д.) — те же аналитические оценки, что и " +
        "по позициям портфеля, не инвестиционные рекомендации. Бумага НЕ входит в портфель — это " +
        "ручной список для сравнения.";

    // ─── GET /api/watchlist ──────────────────────────────────────────────────────────────────

    private static async Task<IResult> GetWatchlist(
        ClaimsPrincipal principal,
        IWatchlistItemRepository watchlistRepo,
        IInstrumentRepository instrumentRepo,
        PortfolioHoldingsBuilder holdingsBuilder)
    {
        var userId = ResolveUserId(principal);
        if (userId is null)
        {
            return Results.Ok(new WatchlistResponseDto { Items = [], Disclaimer = WatchlistDisclaimer });
        }

        var items = (await watchlistRepo.GetByUserIdAsync(userId.Value)).ToList();
        if (items.Count == 0)
        {
            return Results.Ok(new WatchlistResponseDto { Items = [], Disclaimer = WatchlistDisclaimer });
        }

        // Резолвим InstrumentId по ISIN (справочник может отставать от watchlist на доли секунды
        // между POST и первым циклом синка — такая запись просто не попадёт в holdings ниже и
        // будет опущена, а не упадёт 500).
        var instrumentByIsin = new Dictionary<string, Instrument>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            if (instrumentByIsin.ContainsKey(item.Isin)) continue;
            var instrument = await instrumentRepo.GetByIsinAsync(item.Isin);
            if (instrument is not null) instrumentByIsin[item.Isin] = instrument;
        }

        var instrumentIds = instrumentByIsin.Values.Select(i => i.Id).Distinct().ToList();
        var asOf = BusinessClock.MoscowToday();
        var holdings = (await holdingsBuilder.BuildForInstrumentsAsync(instrumentIds, asOf))
            .ToDictionary(h => h.InstrumentId);

        var rows = new List<WatchlistItemDto>();
        foreach (var item in items)
        {
            instrumentByIsin.TryGetValue(item.Isin, out var instrument);
            var holding = instrument is not null && holdings.TryGetValue(instrument.Id, out var h) ? h : null;

            rows.Add(new WatchlistItemDto
            {
                Id = item.Id,
                Isin = item.Isin,
                Note = item.Note,
                AddedAtUtc = item.AddedAtUtc,
                InstrumentId = instrument?.Id,
                Name = holding?.Name ?? instrument?.Name,
                Issuer = holding?.Issuer ?? instrument?.Issuer,
                Sector = holding?.Sector,
                CouponType = (holding?.CouponType ?? instrument?.CouponType)?.ToString(),
                MaturityDate = instrument?.MaturityDate,
                HorizonDate = holding?.HorizonDate,
                CalculatedToOffer = holding?.IsCalculatedToOffer,
                ModifiedDuration = holding?.ModifiedDuration,
                MacaulayDuration = holding?.MacaulayDuration,
                YtmEffective = holding?.YtmEffective,
                CurrentYield = holding?.CurrentYield,
                EffectiveYield = holding is null ? null : (holding.IsFloater || holding.IsIndexed) ? holding.CurrentYield : holding.YtmEffective,
                GSpread = holding?.GSpread,
                IsFloater = holding?.IsFloater,
                IsIndexed = holding?.IsIndexed,
                IsEstimated = holding?.IsEstimated,
                DataIncomplete = holding?.DataIncomplete ?? instrument?.DataIncomplete ?? true,
            });
        }

        return Results.Ok(new WatchlistResponseDto { Items = rows, Disclaimer = WatchlistDisclaimer });
    }

    // ─── POST /api/watchlist ─────────────────────────────────────────────────────────────────

    private static async Task<IResult> PostWatchlist(
        WatchlistCreateRequestDto request,
        ClaimsPrincipal principal,
        IWatchlistItemRepository watchlistRepo,
        IMoexIssClient moex,
        WatchlistSyncService watchlistSync,
        CancellationToken ct)
    {
        var userId = ResolveUserId(principal);
        if (userId is null) throw new NotFoundException("Пользователь не найден");

        var isin = request.Isin?.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(isin))
        {
            return ValidationError("ISIN не указан");
        }

        var existing = await watchlistRepo.GetByUserIdAndIsinAsync(userId.Value, isin);
        if (existing is not null)
        {
            return ValidationError($"ISIN {isin} уже в watchlist");
        }

        // Валидация: ISIN найден на MOEX и это облигация (ResolveSecidByIsinAsync ищет только
        // группу stock_bonds — акции/прочие инструменты сюда не попадают, см. MoexSecuritiesParser).
        var secid = await moex.ResolveSecidByIsinAsync(isin, ct);
        if (secid is null)
        {
            return ValidationError($"ISIN {isin} не найден на MOEX или это не облигация");
        }

        var info = await moex.GetSecurityInfoAsync(secid, ct);
        if (info is null)
        {
            return ValidationError($"ISIN {isin} не найден на MOEX или это не облигация");
        }

        var id = await watchlistRepo.CreateAsync(new WatchlistItem
        {
            UserId = userId.Value,
            Isin = isin,
            Note = request.Note,
        });

        // Синхронное обогащение + котировка (plan/20 §A.4) — бумага появляется с метриками сразу,
        // не дожидаясь следующего цикла синка. Тот же путь, что и у позиций (BondSyncService), плюс
        // MOEX-котировка (WatchlistSyncService.SyncOneAsync — переиспользуется и шагом синка).
        await watchlistSync.SyncOneAsync(isin, ct);

        var created = await watchlistRepo.GetByIdAsync(id, userId.Value);

        return Results.Created($"/api/watchlist/{id}", new WatchlistCreateResponseDto
        {
            Id = id,
            Isin = isin,
            Note = created?.Note,
            AddedAtUtc = created?.AddedAtUtc ?? DateTime.UtcNow,
        });
    }

    // ─── DELETE /api/watchlist/{id} ──────────────────────────────────────────────────────────

    private static async Task<IResult> DeleteWatchlist(
        ulong id,
        ClaimsPrincipal principal,
        IWatchlistItemRepository watchlistRepo)
    {
        var userId = ResolveUserId(principal);
        if (userId is null) throw new NotFoundException("Пользователь не найден");

        var existing = await watchlistRepo.GetByIdAsync(id, userId.Value);
        if (existing is null) throw new NotFoundException($"Запись watchlist {id} не найдена");

        await watchlistRepo.DeleteAsync(id, userId.Value);
        return Results.NoContent();
    }

    private static IResult ValidationError(string message) =>
        Results.Json(new { error = message, type = "ValidationException" }, statusCode: StatusCodes.Status422UnprocessableEntity);

    private static ulong? ResolveUserId(ClaimsPrincipal principal)
    {
        var sub = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        return ulong.TryParse(sub, out var userId) ? userId : null;
    }
}

public sealed record WatchlistResponseDto
{
    public required IReadOnlyList<WatchlistItemDto> Items { get; init; }
    public required string Disclaimer { get; init; }
}

/// <summary>Одна строка watchlist — метрики те же, что у позиции (см. PositionRowDto), плюс поля самой watchlist-записи (Id/Isin/Note/AddedAtUtc).</summary>
public sealed record WatchlistItemDto
{
    public required ulong Id { get; init; }
    public required string Isin { get; init; }
    public string? Note { get; init; }
    public required DateTime AddedAtUtc { get; init; }

    /// <summary>Null — инструмент ещё не подтянут справочником (запись только что создана, следующий синк дозаполнит).</summary>
    public ulong? InstrumentId { get; init; }
    public string? Name { get; init; }
    public string? Issuer { get; init; }
    public string? Sector { get; init; }
    public string? CouponType { get; init; }
    public DateOnly? MaturityDate { get; init; }
    public DateOnly? HorizonDate { get; init; }
    public bool? CalculatedToOffer { get; init; }
    public decimal? ModifiedDuration { get; init; }
    public decimal? MacaulayDuration { get; init; }
    public decimal? YtmEffective { get; init; }
    public decimal? CurrentYield { get; init; }

    /// <summary>YTM либо CurrentYield для флоатера/индексируемой — то же правило, что PositionComparisonService/ScatterPointDto.</summary>
    public decimal? EffectiveYield { get; init; }
    public decimal? GSpread { get; init; }
    public bool? IsFloater { get; init; }
    public bool? IsIndexed { get; init; }
    public bool? IsEstimated { get; init; }
    public required bool DataIncomplete { get; init; }
}

public sealed record WatchlistCreateRequestDto
{
    public string? Isin { get; init; }
    public string? Note { get; init; }
}

public sealed record WatchlistCreateResponseDto
{
    public required ulong Id { get; init; }
    public required string Isin { get; init; }
    public string? Note { get; init; }
    public required DateTime AddedAtUtc { get; init; }
}

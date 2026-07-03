using System.Security.Claims;
using Bonds.Core.Analytics;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;

namespace Bonds.Api.Endpoints;

/// <summary>
/// Лёгкий контур "только цены" (plan/16 часть A) — намеренно НЕ переиспользует
/// <see cref="Bonds.Infrastructure.Analytics.PortfolioHoldingsBuilder"/> (тот тянет купоны/
/// амортизации/оферты/движок метрик на каждую позицию — тяжело для поллинга раз в 60 сек с
/// фронта). Здесь только Position + Instrument + последний тик <see cref="IntradayQuote"/> (с
/// fallback на дневной <see cref="MarketQuote"/> из последнего полного синка, если тиков ещё нет).
/// <para>
/// GET /api/live/positions — стоимость и дневное изменение по каждой открытой позиции.
/// GET /api/live/portfolio-intraday — ряд суммарной стоимости портфеля за 1 или 5 дней, собранный
/// чистым <see cref="IntradaySeriesBuilder"/> из разреженных тиков.
/// </para>
/// </summary>
public static class LiveEndpoints
{
    public static void MapLiveEndpoints(this WebApplication app)
    {
        app.MapGet("/api/live/positions", GetLivePositions);
        app.MapGet("/api/live/portfolio-intraday", GetPortfolioIntraday);
    }

    // ─── GET /api/live/positions ────────────────────────────────────────────────────────────

    private static async Task<IResult> GetLivePositions(
        ClaimsPrincipal principal,
        IAccountRepository accountRepo,
        IPositionRepository positionRepo,
        IInstrumentRepository instrumentRepo,
        IIntradayQuoteRepository intradayRepo,
        IMarketQuoteRepository marketQuoteRepo)
    {
        var accountId = await PositionsEndpoints.ResolveAccountIdAsync(principal, accountRepo);
        if (accountId is null)
        {
            return Results.Ok(new LivePositionsResponseDto
            {
                Positions = [],
                TotalMarketValueRub = 0m,
                AsOfUtc = DateTime.UtcNow,
            });
        }

        var positions = (await positionRepo.GetByAccountIdAsync(accountId.Value))
            .Where(p => p.Quantity != 0)
            .ToList();

        var rows = new List<LivePositionRowDto>(positions.Count);
        decimal total = 0m;
        DateTime? latestTickUtc = null;

        foreach (var position in positions)
        {
            var instrument = await instrumentRepo.GetByIdAsync(position.InstrumentId);
            if (instrument is null) continue; // ссылочная целостность нарушена — пропускаем, не падаем (тот же паттерн, что PortfolioHoldingsBuilder)

            var tick = await intradayRepo.GetLatestAsync(position.InstrumentId);
            // Дневной снимок последнего полного синка — источник и для fallback-цены (когда тиков
            // ещё нет), и для точки отсчёта "изменение за день" (утренний/предыдущий срез, не
            // первый intraday-тик — иначе "день" был бы окном "с момента, когда открылась вкладка").
            var lastFullSyncQuote = await marketQuoteRepo.GetLatestAsync(position.InstrumentId);

            decimal? dirtyPrice;
            DateTime asOfUtc;
            bool isStale;

            if (tick is not null)
            {
                dirtyPrice = tick.DirtyPriceRub;
                asOfUtc = tick.TsUtc;
                isStale = false;
                if (latestTickUtc is null || tick.TsUtc > latestTickUtc) latestTickUtc = tick.TsUtc;
            }
            else
            {
                // Нет ни одного intraday-тика ещё (только что открыт рынок / нет позиций для
                // поллинга ранее) — fallback на последний снимок полного синка (plan/16: "isStale: true").
                dirtyPrice = lastFullSyncQuote?.DirtyPrice;
                asOfUtc = lastFullSyncQuote is not null
                    ? lastFullSyncQuote.AsOf.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)
                    : DateTime.UtcNow;
                isStale = true;
            }

            var marketValue = (dirtyPrice ?? 0m) * position.Quantity;
            total += marketValue;

            decimal? changeDayPercent = null;
            if (dirtyPrice is not null && lastFullSyncQuote?.DirtyPrice is { } refPrice && refPrice != 0m)
            {
                changeDayPercent = (dirtyPrice.Value - refPrice) / refPrice;
            }

            rows.Add(new LivePositionRowDto
            {
                PositionId = position.Id,
                InstrumentId = instrument.Id,
                LastPriceRub = dirtyPrice,
                MarketValueRub = marketValue,
                ChangeDayPercent = changeDayPercent,
                IsStale = isStale,
                AsOfUtc = asOfUtc,
            });
        }

        return Results.Ok(new LivePositionsResponseDto
        {
            Positions = rows,
            TotalMarketValueRub = total,
            AsOfUtc = latestTickUtc ?? DateTime.UtcNow,
        });
    }

    // ─── GET /api/live/portfolio-intraday?range=1d|5d ──────────────────────────────────────────

    private static async Task<IResult> GetPortfolioIntraday(
        ClaimsPrincipal principal,
        IAccountRepository accountRepo,
        IPositionRepository positionRepo,
        IIntradayQuoteRepository intradayRepo,
        string? range)
    {
        var accountId = await PositionsEndpoints.ResolveAccountIdAsync(principal, accountRepo);
        if (accountId is null)
        {
            return Results.Ok(new PortfolioIntradayResponseDto { Points = [] });
        }

        var days = range == "5d" ? 5 : 1;
        var toUtc = DateTime.UtcNow;
        var fromUtc = toUtc.AddDays(-days);

        var positions = (await positionRepo.GetByAccountIdAsync(accountId.Value))
            .Where(p => p.Quantity != 0)
            .ToList();

        if (positions.Count == 0)
        {
            return Results.Ok(new PortfolioIntradayResponseDto { Points = [] });
        }

        var instrumentIds = positions.Select(p => p.InstrumentId).Distinct().ToList();
        var quantityByInstrument = positions
            .GroupBy(p => p.InstrumentId)
            .ToDictionary(g => g.Key, g => g.Sum(p => p.Quantity));

        var ticks = await intradayRepo.GetRangeAsync(instrumentIds, fromUtc, toUtc);
        var quotesByInstrument = instrumentIds.ToDictionary(
            id => id,
            id => (IReadOnlyList<IntradayQuote>)ticks.Where(t => t.InstrumentId == id).ToList());

        var series = IntradaySeriesBuilder.Build(quotesByInstrument, quantityByInstrument);

        return Results.Ok(new PortfolioIntradayResponseDto
        {
            Points = series.Select(p => new IntradaySeriesPointDto
            {
                TsUtc = p.TsUtc,
                TotalMarketValueRub = p.TotalMarketValueRub,
            }).ToList(),
        });
    }
}

public sealed record LivePositionRowDto
{
    public required ulong PositionId { get; init; }
    public required ulong InstrumentId { get; init; }
    public decimal? LastPriceRub { get; init; }
    public required decimal MarketValueRub { get; init; }

    /// <summary>
    /// Audit(portfolio) P-2: несмотря на имя, это ДОЛЯ (0.01 = +1%), а не готовый процент —
    /// соответствует общей бэкенд-конвенции репо «бэкенд = доли», фронт делает
    /// <c>formatPercent</c> (×100). Имя поля историческое и вводит в заблуждение при чтении без
    /// контекста (в отличие от <c>DeltaPercent</c>/<c>SharePercent</c>, которые ИЗ ИМЕНИ похожи,
    /// но фактически уже готовые проценты — см. P-1). Не переименовывать — ломает контракт фронта.
    /// </summary>
    public decimal? ChangeDayPercent { get; init; }
    public required bool IsStale { get; init; }
    public required DateTime AsOfUtc { get; init; }
}

public sealed record LivePositionsResponseDto
{
    public required IReadOnlyList<LivePositionRowDto> Positions { get; init; }
    public required decimal TotalMarketValueRub { get; init; }
    public required DateTime AsOfUtc { get; init; }
}

public sealed record IntradaySeriesPointDto
{
    public required DateTime TsUtc { get; init; }
    public required decimal TotalMarketValueRub { get; init; }
}

public sealed record PortfolioIntradayResponseDto
{
    public required IReadOnlyList<IntradaySeriesPointDto> Points { get; init; }
}

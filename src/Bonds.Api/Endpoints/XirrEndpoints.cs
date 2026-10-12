using Bonds.Api.Auth;
using Bonds.Api.Middleware;
using Bonds.Core.Analytics;
using Bonds.Core.Calculation;
using Bonds.Core.CashFlow;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Interfaces;
using Bonds.Core.Models;
using Bonds.Core.Time;
using Bonds.Core.Universe;
using Bonds.Infrastructure.Analytics;
using Bonds.Infrastructure.Universe;
using Microsoft.Extensions.Options;
using static Bonds.Api.Endpoints.AnalyticsSupport;
using static Bonds.Core.Analytics.RelativeValueService;

namespace Bonds.Api.Endpoints;

/// <summary>Аналитические эндпоинты XIRR/динамики портфеля (/api/analytics/xirr).</summary>
public static class XirrEndpoints
{
    public static void MapXirrEndpoints(this WebApplication app)
    {
        app.MapGet("/api/analytics/xirr", GetXirr);
        app.MapPost("/api/analytics/xirr/backfill", PostXirrBackfill);
    }

    // ─── GET /api/analytics/xirr ────────────────────────────────────────────────────────────

    private static async Task<IResult> GetXirr(
        ICurrentUser currentUser,
        IAccountRepository accountRepo,
        IPortfolioValueSnapshotRepository snapshotRepo,
        DateOnly? from,
        DateOnly? to)
    {
        var accountId = await PositionsEndpoints.ResolveAccountIdAsync(currentUser, accountRepo);
        if (accountId is null)
        {
            return Results.Ok(new XirrResponseDto { CurrentXirr = null, History = [], Disclaimer = Disclaimers.Metrics });
        }

        var history = (await snapshotRepo.GetByAccountIdAsync(accountId.Value, from, to))
            .OrderBy(s => s.AsOf)
            .ToList();
        var latest = history.LastOrDefault() ?? await snapshotRepo.GetLatestAsync(accountId.Value);

        var dto = new XirrResponseDto
        {
            CurrentXirr = latest?.XirrToDate,
            History = history.Select(s => new PortfolioValuePointDto
            {
                Date = s.AsOf,
                MarketValueRub = s.MarketValueRub,
                InvestedRub = s.InvestedRub,
                Xirr = s.XirrToDate,
            }).ToList(),
            Disclaimer = Disclaimers.Metrics,
        };

        return Results.Ok(dto);
    }

    // ─── POST /api/analytics/xirr/backfill ──────────────────────────────────────────────────

    /// <summary>
    /// Ретроспективный бэкфилл истории XIRR: восстанавливает недельный ряд
    /// стоимости/XIRR из журнала операций + дневных исторических цен MOEX ISS и дозаполняет
    /// <c>portfolio_value_snapshots</c> (идемпотентно — не трогает уже существующие даты, живые
    /// или от предыдущего запуска, см. doc-comment <see cref="PortfolioHistoryBackfillService"/>).
    /// Single-user, длительная операция — выполняется синхронно (портфель маленький, plan/15 §B.4);
    /// вызывающий (фронт) ждёт ответа и сам решает, показывать ли индикатор загрузки.
    /// </summary>
    private static async Task<IResult> PostXirrBackfill(
        ICurrentUser currentUser,
        IAccountRepository accountRepo,
        PortfolioHistoryBackfillService backfillService)
    {
        var accountId = await PositionsEndpoints.ResolveAccountIdAsync(currentUser, accountRepo);
        if (accountId is null)
        {
            return Results.Ok(new XirrBackfillResponseDto { PointsWritten = 0 });
        }

        var asOf = BusinessClock.MoscowToday();
        var written = await backfillService.BackfillAsync(accountId.Value, asOf);

        return Results.Ok(new XirrBackfillResponseDto { PointsWritten = written });
    }
}

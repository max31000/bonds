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

public sealed record XirrResponseDto
{
    public decimal? CurrentXirr { get; init; }
    public required IReadOnlyList<PortfolioValuePointDto> History { get; init; }
    public required string Disclaimer { get; init; }
}

public sealed record PortfolioValuePointDto
{
    public required DateOnly Date { get; init; }
    public required decimal MarketValueRub { get; init; }
    public required decimal InvestedRub { get; init; }
    public decimal? Xirr { get; init; }
}

/// <summary>Ответ POST /api/analytics/xirr/backfill — сколько новых точек истории записано.</summary>
public sealed record XirrBackfillResponseDto
{
    public required int PointsWritten { get; init; }
}

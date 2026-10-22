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

/// <summary>Аналитика портфеля: композиция, scatter «дюрация×доходность», сценарии ставки, траектория.</summary>
public static class PortfolioAnalyticsEndpoints
{
    public static void MapPortfolioAnalyticsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/analytics/composition", GetComposition);
        app.MapGet("/api/analytics/scatter", GetScatter);
        app.MapGet("/api/analytics/rate-scenario", GetRateScenario);
        app.MapGet("/api/analytics/trajectory", GetTrajectory);
    }

    // ─── GET /api/analytics/composition ─────────────────────────────────────────────────────

    private static async Task<IResult> GetComposition(
        ICurrentUser currentUser,
        IAccountRepository accountRepo,
        PortfolioHoldingsBuilder holdingsBuilder,
        FundOverviewBuilder fundOverview,
        bool? includeFunds,
        CancellationToken ct)
    {
        // includeFunds по умолчанию true: «Аналитика» показывает картину всего портфеля. false — прежний облигационный ответ
        // (SignalsEngine и вердикты позиций считают композицию через Calculate и фонды не видят).
        var withFunds = includeFunds ?? true;
        var accountId = await PositionsEndpoints.ResolveAccountIdAsync(currentUser, accountRepo);
        if (accountId is null)
        {
            return Results.Ok(new CompositionResponseDto
            {
                TotalMarketValueRub = 0m,
                ByIssuer = [],
                BySector = [],
                ByCouponType = [],
                ByDurationBucket = [],
                FundsIncluded = withFunds,
                Disclaimer = Disclaimers.Metrics,
            });
        }

        var asOf = BusinessClock.MoscowToday();
        var holdings = await holdingsBuilder.BuildForAccountAsync(accountId.Value, asOf, ct);
        var fundItems = await fundOverview.BuildExposuresAsync(accountId.Value, asOf, ct);
        var composition = withFunds
            ? PortfolioCompositionService.CalculateWithFunds(holdings, fundItems.Select(f => f.Exposure).ToList())
            : PortfolioCompositionService.Calculate(holdings);

        var dto = new CompositionResponseDto
        {
            TotalMarketValueRub = composition.TotalMarketValueRub,
            ByIssuer = composition.ByIssuer.Select(ToShareDto).ToList(),
            BySector = composition.BySector.Select(ToShareDto).ToList(),
            ByCouponType = composition.ByCouponType.Select(ToShareDto).ToList(),
            ByDurationBucket = composition.ByDurationBucket.Select(ToShareDto).ToList(),
            ByAssetClass = composition.ByAssetClass.Select(ToShareDto).ToList(),
            FundsIncluded = withFunds,
            HasFunds = fundItems.Count > 0,
            Disclaimer = Disclaimers.Metrics,
        };

        return Results.Ok(dto);
    }

    private static CompositionShareDto ToShareDto(CompositionShare s) => new()
    {
        Key = s.Key,
        MarketValueRub = s.MarketValueRub,
        SharePercent = s.SharePercent,
    };

    // ─── GET /api/analytics/scatter ─────────────────────────────────────────────────────────

    /// <summary>
    /// Точки «дюрация×доходность» твоих бумаг + параметры Gcurve для наложения кривой (spec §9).
    /// Кривая отдаётся как набор точек (срок, доходность), посчитанных <see cref="GSpreadCalculator.CurveValue"/>
    /// на сетке сроков 0.25..30 лет (самостоятельное решение — спека не фиксирует шаг сетки;
    /// фронт рисует line chart по этим точкам, не пересчитывая NSS-формулу на клиенте).
    /// <para>
    /// <b>Задача 20:</b> к точкам портфеля добавляются watchlist-бумаги (<c>IsWatchlist=true</c>) —
    /// тот же расчётный путь (<see cref="PortfolioHoldingsBuilder.BuildForInstrumentsAsync"/>).
    /// Бумага, которая уже есть в портфеле (совпадает InstrumentId), не дублируется отдельной точкой.
    /// </para>
    /// </summary>
    private static async Task<IResult> GetScatter(
        ICurrentUser currentUser,
        IAccountRepository accountRepo,
        IYieldCurveRepository yieldCurveRepo,
        IWatchlistItemRepository watchlistRepo,
        IInstrumentRepository instrumentRepo,
        PortfolioHoldingsBuilder holdingsBuilder)
    {
        var accountId = await PositionsEndpoints.ResolveAccountIdAsync(currentUser, accountRepo);
        var curve = await yieldCurveRepo.GetLatestAsync();

        IReadOnlyList<Core.Analytics.PortfolioHolding> holdings = [];
        if (accountId is not null)
        {
            var asOf = BusinessClock.MoscowToday();
            holdings = await holdingsBuilder.BuildForAccountAsync(accountId.Value, asOf);
        }

        var points = holdings
            .Where(h => h.ModifiedDuration is not null && h.MacaulayDuration is not null)
            .Select(h => new ScatterPointDto
            {
                PositionId = h.PositionId,
                InstrumentId = h.InstrumentId,
                Name = h.Name,
                Issuer = h.Issuer,
                ModifiedDuration = h.ModifiedDuration!.Value,
                MacaulayDuration = h.MacaulayDuration!.Value,
                EffectiveYield = (h.IsFloater || h.IsIndexed) ? h.CurrentYield : h.YtmEffective,
                YieldKind = (h.IsFloater || h.IsIndexed) ? "CurrentYield" : "Ytm",
                IsFloater = h.IsFloater,
                IsIndexed = h.IsIndexed,
                IsEstimated = h.IsEstimated,
                DataIncomplete = h.DataIncomplete,
                IsWatchlist = false,
            })
            .ToList();

        if (await currentUser.GetUserIdAsync() is { } userId)
        {
            var portfolioInstrumentIds = holdings.Select(h => h.InstrumentId).ToHashSet();
            var watchlistItems = (await watchlistRepo.GetByUserIdAsync(userId)).ToList();

            var watchlistInstrumentIds = new List<ulong>();
            foreach (var item in watchlistItems)
            {
                var instrument = await instrumentRepo.GetByIsinAsync(item.Isin);
                if (instrument is not null && !portfolioInstrumentIds.Contains(instrument.Id))
                {
                    watchlistInstrumentIds.Add(instrument.Id);
                }
            }

            var asOf = BusinessClock.MoscowToday();
            var watchlistHoldings = await holdingsBuilder.BuildForInstrumentsAsync(watchlistInstrumentIds.Distinct().ToList(), asOf);

            points.AddRange(watchlistHoldings
                .Where(h => h.ModifiedDuration is not null && h.MacaulayDuration is not null)
                .Select(h => new ScatterPointDto
                {
                    PositionId = h.PositionId,
                    InstrumentId = h.InstrumentId,
                    Name = h.Name,
                    Issuer = h.Issuer,
                    ModifiedDuration = h.ModifiedDuration!.Value,
                    MacaulayDuration = h.MacaulayDuration!.Value,
                    EffectiveYield = (h.IsFloater || h.IsIndexed) ? h.CurrentYield : h.YtmEffective,
                    YieldKind = (h.IsFloater || h.IsIndexed) ? "CurrentYield" : "Ytm",
                    IsFloater = h.IsFloater,
                    IsIndexed = h.IsIndexed,
                    IsEstimated = h.IsEstimated,
                    DataIncomplete = h.DataIncomplete,
                    IsWatchlist = true,
                }));
        }

        var curvePoints = new List<CurvePointDto>();
        if (curve is not null)
        {
            decimal[] termsYears = [0.25m, 0.5m, 1m, 2m, 3m, 5m, 7m, 10m, 15m, 20m, 30m];
            foreach (var term in termsYears)
            {
                curvePoints.Add(new CurvePointDto
                {
                    TermYears = term,
                    Yield = GSpreadCalculator.CurveValue(curve, term),
                });
            }
        }

        var dto = new ScatterResponseDto
        {
            Points = points,
            Curve = curvePoints,
            CurveAsOf = curve?.AsOf,
            Disclaimer = Disclaimers.Metrics,
        };

        return Results.Ok(dto);
    }

    // ─── GET /api/analytics/rate-scenario ───────────────────────────────────────────────────

    private static async Task<IResult> GetRateScenario(
        ICurrentUser currentUser,
        IAccountRepository accountRepo,
        PortfolioHoldingsBuilder holdingsBuilder,
        FundOverviewBuilder fundOverview,
        string? shiftsBp,
        bool? includeFunds,
        CancellationToken ct)
    {
        var withFunds = includeFunds ?? true;
        var accountId = await PositionsEndpoints.ResolveAccountIdAsync(currentUser, accountRepo);
        if (accountId is null)
        {
            return Results.Ok(new RateScenarioResponseDto
            {
                CurrentValueRub = 0,
                Scenarios = [],
                FundsIncluded = withFunds,
                Disclaimer = Disclaimers.Metrics,
            });
        }

        var asOf = BusinessClock.MoscowToday();
        var holdings = (await holdingsBuilder.BuildForAccountAsync(accountId.Value, asOf, ct)).ToList();
        var fundItems = await fundOverview.BuildExposuresAsync(accountId.Value, asOf, ct);
        var funds = withFunds ? fundItems.Select(f => f.Exposure).ToList() : [];
        var currentValue = holdings.Sum(h => h.MarketValueRub) + funds.Sum(f => f.MarketValueRub);

        var shifts = string.IsNullOrEmpty(shiftsBp)
            ? RateScenarioService.DefaultShiftsBp
            : shiftsBp.Split(',').Select(int.Parse).ToArray();

        var scenarios = RateScenarioService.Compute(holdings, funds, shifts);

        return Results.Ok(new RateScenarioResponseDto
        {
            CurrentValueRub = currentValue,
            RateSensitiveValueRub = RateScenarioService.RateSensitiveValue(holdings, funds),
            FundsIncluded = withFunds,
            HasFunds = fundItems.Count > 0,
            Funds = funds.Select(f => new RateScenarioFundDto
            {
                FundId = f.FundId,
                Ticker = f.Ticker,
                Name = f.Name,
                MarketValueRub = f.MarketValueRub,
                ModifiedDurationYears = f.ModifiedDurationYears,
                DurationSource = fundItems.First(x => x.Exposure.FundId == f.FundId).Estimate.Source ?? "unknown",
                DeltaAtPlus100bpRub = RateScenarioService.FundDelta(f, 100),
            }).ToList(),
            Scenarios = scenarios.Select(s => new RateScenarioPointDto
            {
                ShiftBp = s.ShiftBp,
                NewValueRub = s.NewValueRub,
                DeltaRub = s.DeltaRub,
                DeltaPercent = s.DeltaPercent,
            }).ToList(),
            Disclaimer = Disclaimers.Metrics,
        });
    }

    // ─── GET /api/analytics/trajectory ──────────────────────────────────────────────────────

    private static async Task<IResult> GetTrajectory(
        ICurrentUser currentUser,
        IAccountRepository accountRepo,
        PortfolioHoldingsBuilder holdingsBuilder,
        IProjectedCashFlowRepository projectedCashFlows,
        int? horizonMonths,
        decimal? reinvestRate)
    {
        var accountId = await PositionsEndpoints.ResolveAccountIdAsync(currentUser, accountRepo);
        if (accountId is null)
        {
            return Results.Ok(new TrajectoryResponseDto
            {
                InitialValueRub = 0,
                WithReinvest = [],
                WithoutReinvest = [],
                ReinvestRateUsed = 0,
                Disclaimer = Disclaimers.Metrics,
            });
        }

        var horizon = horizonMonths ?? 36;
        var asOf = BusinessClock.MoscowToday();
        var holdings = (await holdingsBuilder.BuildForAccountAsync(accountId.Value, asOf)).ToList();
        var effectiveRate = reinvestRate ?? PortfolioTrajectoryService.DefaultReinvestRate(holdings);

        var from = BusinessClock.MoscowToday();
        var to = from.AddMonths(horizon);
        var flows = await projectedCashFlows.GetByAccountIdAsync(accountId.Value, from, to);
        var monthlySummaries = CashFlowAggregator.ByMonth(flows);

        var result = PortfolioTrajectoryService.Compute(holdings, monthlySummaries, horizon, effectiveRate, asOf);

        return Results.Ok(new TrajectoryResponseDto
        {
            InitialValueRub = result.InitialValueRub,
            WithReinvest = result.WithReinvest.Select(p => new TrajectoryPointDto
            {
                Month = p.Month,
                PortfolioValueRub = p.PortfolioValueRub,
                CumulativeIncomeRub = p.CumulativeIncomeRub,
            }).ToList(),
            WithoutReinvest = result.WithoutReinvest.Select(p => new TrajectoryPointDto
            {
                Month = p.Month,
                PortfolioValueRub = p.PortfolioValueRub,
                CumulativeIncomeRub = p.CumulativeIncomeRub,
            }).ToList(),
            ReinvestRateUsed = effectiveRate,
            Disclaimer = Disclaimers.Metrics,
        });
    }
}

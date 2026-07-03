using System.Security.Claims;
using Bonds.Api.Middleware;
using Bonds.Core.Analytics;
using Bonds.Core.Calculation;
using Bonds.Core.CashFlow;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Time;
using Bonds.Infrastructure.Analytics;

namespace Bonds.Api.Endpoints;

/// <summary>
/// Аналитические эндпоинты этапа 08 (spec §9, §6/§11 дисклеймер): XIRR/динамика, композиция,
/// scatter «дюрация×доходность» поверх Gcurve, сравнение позиций, анализ замены. Все опираются
/// на <see cref="PortfolioHoldingsBuilder"/> (этап 08) для сборки holdings из репозиториев и
/// на чистые сервисы этапа 06 для самого расчёта.
/// </summary>
public static class AnalyticsEndpoints
{
    public static void MapAnalyticsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/analytics/xirr", GetXirr);
        app.MapPost("/api/analytics/xirr/backfill", PostXirrBackfill);
        app.MapGet("/api/analytics/composition", GetComposition);
        app.MapGet("/api/analytics/scatter", GetScatter);
        app.MapGet("/api/analytics/comparison", GetComparison);
        app.MapPost("/api/analytics/replacement", PostReplacement);
        app.MapGet("/api/analytics/rate-scenario", GetRateScenario);
        app.MapGet("/api/analytics/trajectory", GetTrajectory);
        app.MapGet("/api/analytics/allocation", GetAllocation);
    }

    // ─── GET /api/analytics/xirr ────────────────────────────────────────────────────────────

    private static async Task<IResult> GetXirr(
        ClaimsPrincipal principal,
        IAccountRepository accountRepo,
        IPortfolioValueSnapshotRepository snapshotRepo,
        DateOnly? from,
        DateOnly? to)
    {
        var accountId = await PositionsEndpoints.ResolveAccountIdAsync(principal, accountRepo);
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
    /// Ретроспективный бэкфилл истории XIRR (plan/15 §B.4): восстанавливает недельный ряд
    /// стоимости/XIRR из журнала операций + дневных исторических цен MOEX ISS и дозаполняет
    /// <c>portfolio_value_snapshots</c> (идемпотентно — не трогает уже существующие даты, живые
    /// или от предыдущего запуска, см. doc-comment <see cref="PortfolioHistoryBackfillService"/>).
    /// Single-user, длительная операция — выполняется синхронно (портфель маленький, plan/15 §B.4);
    /// вызывающий (фронт) ждёт ответа и сам решает, показывать ли индикатор загрузки.
    /// </summary>
    private static async Task<IResult> PostXirrBackfill(
        ClaimsPrincipal principal,
        IAccountRepository accountRepo,
        PortfolioHistoryBackfillService backfillService)
    {
        var accountId = await PositionsEndpoints.ResolveAccountIdAsync(principal, accountRepo);
        if (accountId is null)
        {
            return Results.Ok(new XirrBackfillResponseDto { PointsWritten = 0 });
        }

        var asOf = BusinessClock.MoscowToday();
        var written = await backfillService.BackfillAsync(accountId.Value, asOf);

        return Results.Ok(new XirrBackfillResponseDto { PointsWritten = written });
    }

    // ─── GET /api/analytics/composition ─────────────────────────────────────────────────────

    private static async Task<IResult> GetComposition(
        ClaimsPrincipal principal,
        IAccountRepository accountRepo,
        PortfolioHoldingsBuilder holdingsBuilder)
    {
        var accountId = await PositionsEndpoints.ResolveAccountIdAsync(principal, accountRepo);
        if (accountId is null)
        {
            return Results.Ok(new CompositionResponseDto
            {
                TotalMarketValueRub = 0m,
                ByIssuer = [],
                BySector = [],
                ByCouponType = [],
                ByDurationBucket = [],
                Disclaimer = Disclaimers.Metrics,
            });
        }

        var asOf = BusinessClock.MoscowToday();
        var holdings = await holdingsBuilder.BuildForAccountAsync(accountId.Value, asOf);
        var composition = PortfolioCompositionService.Calculate(holdings);

        var dto = new CompositionResponseDto
        {
            TotalMarketValueRub = composition.TotalMarketValueRub,
            ByIssuer = composition.ByIssuer.Select(ToShareDto).ToList(),
            BySector = composition.BySector.Select(ToShareDto).ToList(),
            ByCouponType = composition.ByCouponType.Select(ToShareDto).ToList(),
            ByDurationBucket = composition.ByDurationBucket.Select(ToShareDto).ToList(),
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
    /// </summary>
    private static async Task<IResult> GetScatter(
        ClaimsPrincipal principal,
        IAccountRepository accountRepo,
        IYieldCurveRepository yieldCurveRepo,
        PortfolioHoldingsBuilder holdingsBuilder)
    {
        var accountId = await PositionsEndpoints.ResolveAccountIdAsync(principal, accountRepo);
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
            })
            .ToList();

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

    // ─── GET /api/analytics/comparison ──────────────────────────────────────────────────────

    private static async Task<IResult> GetComparison(
        ClaimsPrincipal principal,
        IAccountRepository accountRepo,
        PortfolioHoldingsBuilder holdingsBuilder)
    {
        var accountId = await PositionsEndpoints.ResolveAccountIdAsync(principal, accountRepo);
        if (accountId is null)
        {
            return Results.Ok(new ComparisonResponseDto { Rows = [], Disclaimer = PositionComparisonService.YieldDisclaimer });
        }

        var asOf = BusinessClock.MoscowToday();
        var holdings = await holdingsBuilder.BuildForAccountAsync(accountId.Value, asOf);
        var result = PositionComparisonService.Compare(holdings, asOf);

        var dto = new ComparisonResponseDto
        {
            Rows = result.Rows.Select(r => new ComparisonRowDto
            {
                PositionId = r.PositionId,
                InstrumentId = r.InstrumentId,
                Name = r.Name,
                Issuer = r.Issuer,
                EffectiveYield = r.EffectiveYield,
                YieldKind = r.YieldKind.ToString(),
                ModifiedDuration = r.ModifiedDuration,
                GSpread = r.GSpread,
                DaysToHorizon = r.DaysToHorizon,
                HorizonDate = r.HorizonDate,
                CalculatedToOffer = r.IsCalculatedToOffer,
                CouponType = r.CouponType.ToString(),
                IsEstimated = r.IsEstimated,
                DataIncomplete = r.DataIncomplete,
            }).ToList(),
            Disclaimer = result.Disclaimer,
        };

        return Results.Ok(dto);
    }

    // ─── POST /api/analytics/replacement ────────────────────────────────────────────────────

    private static async Task<IResult> PostReplacement(
        ReplacementRequestDto request,
        ClaimsPrincipal principal,
        IAccountRepository accountRepo,
        PortfolioHoldingsBuilder holdingsBuilder)
    {
        var accountId = await PositionsEndpoints.ResolveAccountIdAsync(principal, accountRepo);
        if (accountId is null) throw new NotFoundException("Счёт не найден");

        var asOf = BusinessClock.MoscowToday();
        var holdings = await holdingsBuilder.BuildForAccountAsync(accountId.Value, asOf);

        var holdPosition = holdings.FirstOrDefault(h => h.PositionId == request.HoldPositionId);
        var targetPosition = holdings.FirstOrDefault(h => h.PositionId == request.TargetPositionId);

        if (holdPosition is null) throw new NotFoundException($"Позиция {request.HoldPositionId} не найдена в портфеле");
        if (targetPosition is null) throw new NotFoundException($"Позиция {request.TargetPositionId} не найдена в портфеле");
        if (request.HorizonYears <= 0m) throw new ValidationException("HorizonYears должен быть положительным");

        var holdCandidate = new SwitchCandidate
        {
            PositionId = holdPosition.PositionId,
            MarketValueRub = holdPosition.MarketValueRub,
            EffectiveYield = (holdPosition.IsFloater || holdPosition.IsIndexed) ? holdPosition.CurrentYield : holdPosition.YtmEffective,
        };
        var targetCandidate = new SwitchCandidate
        {
            PositionId = targetPosition.PositionId,
            MarketValueRub = targetPosition.MarketValueRub,
            EffectiveYield = (targetPosition.IsFloater || targetPosition.IsIndexed) ? targetPosition.CurrentYield : targetPosition.YtmEffective,
        };

        var sellRate = request.SellCommissionRate ?? SwitchAnalysisService.DefaultCommissionRate;
        var buyRate = request.BuyCommissionRate ?? SwitchAnalysisService.DefaultCommissionRate;

        var result = SwitchAnalysisService.Compare(holdCandidate, targetCandidate, request.HorizonYears, sellRate, buyRate);

        var dto = new ReplacementResponseDto
        {
            HoldPositionId = result.HoldPositionId,
            TargetPositionId = result.TargetPositionId,
            HorizonYears = result.HorizonYears,
            SellCommissionRub = result.SellCommissionRub,
            BuyCommissionRub = result.BuyCommissionRub,
            TotalSwitchCostRub = result.TotalSwitchCostRub,
            NetBenefitRub = result.NetBenefitRub,
            IsSwitchFavorable = result.IsSwitchFavorable,
            BreakEvenYears = result.BreakEvenYears,
            YieldDataIncomplete = result.YieldDataIncomplete,
            Disclaimer = result.Disclaimer,
        };

        return Results.Ok(dto);
    }

    // ─── GET /api/analytics/rate-scenario ───────────────────────────────────────────────────

    private static async Task<IResult> GetRateScenario(
        ClaimsPrincipal principal,
        IAccountRepository accountRepo,
        PortfolioHoldingsBuilder holdingsBuilder,
        string? shiftsBp)
    {
        var accountId = await PositionsEndpoints.ResolveAccountIdAsync(principal, accountRepo);
        if (accountId is null)
        {
            return Results.Ok(new RateScenarioResponseDto
            {
                CurrentValueRub = 0,
                Scenarios = [],
                Disclaimer = Disclaimers.Metrics,
            });
        }

        var asOf = BusinessClock.MoscowToday();
        var holdings = (await holdingsBuilder.BuildForAccountAsync(accountId.Value, asOf)).ToList();
        var currentValue = holdings.Sum(h => h.MarketValueRub);

        var shifts = string.IsNullOrEmpty(shiftsBp)
            ? RateScenarioService.DefaultShiftsBp
            : shiftsBp.Split(',').Select(int.Parse).ToArray();

        var scenarios = RateScenarioService.Compute(holdings, shifts);

        return Results.Ok(new RateScenarioResponseDto
        {
            CurrentValueRub = currentValue,
            RateSensitiveValueRub = RateScenarioService.RateSensitiveValue(holdings),
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
        ClaimsPrincipal principal,
        IAccountRepository accountRepo,
        PortfolioHoldingsBuilder holdingsBuilder,
        IProjectedCashFlowRepository projectedCashFlows,
        int? horizonMonths,
        decimal? reinvestRate)
    {
        var accountId = await PositionsEndpoints.ResolveAccountIdAsync(principal, accountRepo);
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

    // ─── GET /api/analytics/allocation ──────────────────────────────────────────────────────

    /// <summary>
    /// «Куда вложить сумму» (plan/17 §B): жадное распределение <paramref name="amountRub"/> между
    /// текущими позициями портфеля через <see cref="CashAllocationService"/>. Кандидаты и их
    /// грязная цена лота строятся из <see cref="PortfolioHoldingsBuilder"/> holdings — тот же вход,
    /// что у comparison/replacement. Лот берётся равным 1 (в модели <c>Instrument</c> нет поля
    /// размера лота — см. doc-comment плана: при отсутствии источника лота считаем 1 и помечаем
    /// <c>lotSizeAssumed</c>, а не падаем/выдумываем данные).
    /// </summary>
    private static async Task<IResult> GetAllocation(
        ClaimsPrincipal principal,
        IAccountRepository accountRepo,
        PortfolioHoldingsBuilder holdingsBuilder,
        decimal amountRub)
    {
        if (amountRub <= 0m)
        {
            return Results.Json(
                new { error = "amountRub должен быть положительным", type = "ValidationException" },
                statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        var accountId = await PositionsEndpoints.ResolveAccountIdAsync(principal, accountRepo);
        if (accountId is null)
        {
            return Results.Ok(new AllocationResponseDto
            {
                AmountRub = amountRub,
                Allocations = [],
                Skipped = [],
                LeftoverRub = amountRub,
                Disclaimer = CashAllocationService.Disclaimer,
            });
        }

        var asOf = BusinessClock.MoscowToday();
        var holdings = (await holdingsBuilder.BuildForAccountAsync(accountId.Value, asOf)).ToList();

        var currentPortfolioValueRub = holdings.Sum(h => h.MarketValueRub);
        var issuerMarketValue = holdings
            .GroupBy(h => h.Issuer ?? $"__instrument_{h.InstrumentId}")
            .ToDictionary(g => g.Key, g => g.Sum(h => h.MarketValueRub));

        var candidates = holdings
            .Where(h => !h.IsOutOfScopeCurrency && h.Quantity > 0m)
            .Select(h =>
            {
                var pricePerUnitRub = h.MarketValueRub / h.Quantity;
                var pricePerLotWithCommission = pricePerUnitRub * (1m + SwitchAnalysisService.DefaultCommissionRate);
                var effectiveYield = (h.IsFloater || h.IsIndexed) ? h.CurrentYield : h.YtmEffective;
                var issuerKey = h.Issuer ?? $"__instrument_{h.InstrumentId}";

                return new CashAllocationCandidate
                {
                    InstrumentId = h.InstrumentId,
                    Name = h.Name,
                    Issuer = h.Issuer,
                    EffectiveYield = effectiveYield,
                    PricePerLotRub = pricePerLotWithCommission,
                    LotSize = 1m,
                    LotSizeIsAssumed = true,
                    CurrentIssuerMarketValueRub = issuerMarketValue.TryGetValue(issuerKey, out var v) ? v : 0m,
                };
            })
            // Одна позиция на инструмент — если счёт по ошибке содержит несколько записей на один
            // InstrumentId, берём первую (защита от дублей, не ожидается в норме).
            .DistinctBy(c => c.InstrumentId)
            .ToList();

        var result = CashAllocationService.Allocate(amountRub, candidates, currentPortfolioValueRub);

        var dto = new AllocationResponseDto
        {
            AmountRub = result.AmountRub,
            Allocations = result.Allocations.Select(a => new AllocationLineDto
            {
                InstrumentId = a.InstrumentId,
                Name = a.Name,
                Issuer = a.Issuer,
                Quantity = a.Quantity,
                EstimatedCostRub = a.EstimatedCostRub,
                EffectiveYield = a.EffectiveYield,
                LotSizeAssumed = a.LotSizeAssumed,
            }).ToList(),
            Skipped = result.Skipped.Select(s => new AllocationSkipDto
            {
                InstrumentId = s.InstrumentId,
                Name = s.Name,
                Issuer = s.Issuer,
                Reason = s.Reason.ToString(),
            }).ToList(),
            LeftoverRub = result.LeftoverRub,
            Disclaimer = result.Disclaimer,
        };

        return Results.Ok(dto);
    }
}

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

public sealed record CompositionResponseDto
{
    public required decimal TotalMarketValueRub { get; init; }
    public required IReadOnlyList<CompositionShareDto> ByIssuer { get; init; }
    public required IReadOnlyList<CompositionShareDto> BySector { get; init; }
    public required IReadOnlyList<CompositionShareDto> ByCouponType { get; init; }
    public required IReadOnlyList<CompositionShareDto> ByDurationBucket { get; init; }
    public required string Disclaimer { get; init; }
}

public sealed record CompositionShareDto
{
    public required string Key { get; init; }
    public required decimal MarketValueRub { get; init; }
    public required decimal SharePercent { get; init; }
}

public sealed record ScatterResponseDto
{
    public required IReadOnlyList<ScatterPointDto> Points { get; init; }
    public required IReadOnlyList<CurvePointDto> Curve { get; init; }
    public DateOnly? CurveAsOf { get; init; }
    public required string Disclaimer { get; init; }
}

public sealed record ScatterPointDto
{
    public required ulong PositionId { get; init; }
    public required ulong InstrumentId { get; init; }
    public string? Name { get; init; }
    public string? Issuer { get; init; }
    public required decimal ModifiedDuration { get; init; }

    /// <summary>T-7/L-1: дюрация Маколея — ось X scatter строится по ней, чтобы визуальное «над/под
    /// кривой» совпадало со знаком G-спреда (он тоже считается по Маколею).</summary>
    public required decimal MacaulayDuration { get; init; }
    public decimal? EffectiveYield { get; init; }
    public required string YieldKind { get; init; }
    public required bool IsFloater { get; init; }
    public required bool IsIndexed { get; init; }
    public required bool IsEstimated { get; init; }
    public required bool DataIncomplete { get; init; }
}

public sealed record CurvePointDto
{
    public required decimal TermYears { get; init; }
    public required decimal Yield { get; init; }
}

public sealed record ComparisonResponseDto
{
    public required IReadOnlyList<ComparisonRowDto> Rows { get; init; }
    public required string Disclaimer { get; init; }
}

public sealed record ComparisonRowDto
{
    public required ulong PositionId { get; init; }
    public required ulong InstrumentId { get; init; }
    public string? Name { get; init; }
    public string? Issuer { get; init; }
    public decimal? EffectiveYield { get; init; }
    public required string YieldKind { get; init; }
    public decimal? ModifiedDuration { get; init; }
    public decimal? GSpread { get; init; }
    public required int DaysToHorizon { get; init; }
    public required DateOnly HorizonDate { get; init; }
    public required bool CalculatedToOffer { get; init; }
    public required string CouponType { get; init; }
    public required bool IsEstimated { get; init; }
    public required bool DataIncomplete { get; init; }
}

public sealed record ReplacementRequestDto
{
    public required ulong HoldPositionId { get; init; }
    public required ulong TargetPositionId { get; init; }
    public required decimal HorizonYears { get; init; }
    public decimal? SellCommissionRate { get; init; }
    public decimal? BuyCommissionRate { get; init; }
}

public sealed record ReplacementResponseDto
{
    public required ulong HoldPositionId { get; init; }
    public required ulong TargetPositionId { get; init; }
    public required decimal HorizonYears { get; init; }
    public required decimal SellCommissionRub { get; init; }
    public required decimal BuyCommissionRub { get; init; }
    public required decimal TotalSwitchCostRub { get; init; }
    public required decimal NetBenefitRub { get; init; }
    public required bool IsSwitchFavorable { get; init; }
    public decimal? BreakEvenYears { get; init; }
    public required bool YieldDataIncomplete { get; init; }
    public required string Disclaimer { get; init; }
}

public sealed record RateScenarioResponseDto
{
    public required decimal CurrentValueRub { get; init; }

    /// <summary>H-1/M-1: процентно-чувствительная часть портфеля (бумаги с дюрацией). Δ считается
    /// только от неё; флоатеры/бумаги без дюрации входят в CurrentValueRub, но не в Δ.</summary>
    public decimal RateSensitiveValueRub { get; init; }
    public required IReadOnlyList<RateScenarioPointDto> Scenarios { get; init; }
    public required string Disclaimer { get; init; }
}

public sealed record RateScenarioPointDto
{
    public required int ShiftBp { get; init; }
    public required decimal NewValueRub { get; init; }
    public required decimal DeltaRub { get; init; }
    public required decimal DeltaPercent { get; init; }
}

public sealed record TrajectoryResponseDto
{
    public required decimal InitialValueRub { get; init; }
    public required IReadOnlyList<TrajectoryPointDto> WithReinvest { get; init; }
    public required IReadOnlyList<TrajectoryPointDto> WithoutReinvest { get; init; }
    public required decimal ReinvestRateUsed { get; init; }
    public required string Disclaimer { get; init; }
}

public sealed record TrajectoryPointDto
{
    public required string Month { get; init; }
    public required decimal PortfolioValueRub { get; init; }
    public required decimal CumulativeIncomeRub { get; init; }
}

public sealed record AllocationResponseDto
{
    public required decimal AmountRub { get; init; }
    public required IReadOnlyList<AllocationLineDto> Allocations { get; init; }
    public required IReadOnlyList<AllocationSkipDto> Skipped { get; init; }
    public required decimal LeftoverRub { get; init; }
    public required string Disclaimer { get; init; }
}

public sealed record AllocationLineDto
{
    public required ulong InstrumentId { get; init; }
    public string? Name { get; init; }
    public string? Issuer { get; init; }
    public required decimal Quantity { get; init; }
    public required decimal EstimatedCostRub { get; init; }
    public required decimal EffectiveYield { get; init; }
    public required bool LotSizeAssumed { get; init; }
}

public sealed record AllocationSkipDto
{
    public required ulong InstrumentId { get; init; }
    public string? Name { get; init; }
    public string? Issuer { get; init; }
    public required string Reason { get; init; }
}

using System.Security.Claims;
using Bonds.Api.Middleware;
using Bonds.Core.Analytics;
using Bonds.Core.Calculation;
using Bonds.Core.CashFlow;
using Bonds.Core.Interfaces;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Time;
using Bonds.Core.Universe;
using Bonds.Infrastructure.Analytics;
using Bonds.Infrastructure.Universe;
using static Bonds.Core.Analytics.RelativeValueService;

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
        app.MapGet("/api/analytics/replacement-matrix", GetReplacementMatrix);
        app.MapGet("/api/analytics/rate-scenario", GetRateScenario);
        app.MapGet("/api/analytics/trajectory", GetTrajectory);
        app.MapGet("/api/analytics/allocation", GetAllocation);
        app.MapPost("/api/analytics/basket", PostBasket);
        app.MapGet("/api/analytics/relative-value", GetRelativeValue);
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
    /// <para>
    /// <b>Задача 20:</b> к точкам портфеля добавляются watchlist-бумаги (<c>IsWatchlist=true</c>) —
    /// тот же расчётный путь (<see cref="PortfolioHoldingsBuilder.BuildForInstrumentsAsync"/>).
    /// Бумага, которая уже есть в портфеле (совпадает InstrumentId), не дублируется отдельной точкой.
    /// </para>
    /// </summary>
    private static async Task<IResult> GetScatter(
        ClaimsPrincipal principal,
        IAccountRepository accountRepo,
        IYieldCurveRepository yieldCurveRepo,
        IWatchlistItemRepository watchlistRepo,
        IInstrumentRepository instrumentRepo,
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
                IsWatchlist = false,
            })
            .ToList();

        var userIdClaim = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (ulong.TryParse(userIdClaim, out var userId))
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

    /// <summary>
    /// «Держать A vs переложиться в B» (plan/17 §A.2). Target по умолчанию — позиция портфеля
    /// (<see cref="ReplacementRequestDto.TargetPositionId"/>, контракт не изменён — существующие
    /// вызовы фронта продолжают работать без изменений).
    /// <para>
    /// <b>Задача 20:</b> если задан <see cref="ReplacementRequestDto.TargetInstrumentId"/> (взаимно
    /// исключим с TargetPositionId), target ищется среди watchlist-бумаг без позиции через
    /// <see cref="PortfolioHoldingsBuilder.BuildForInstrumentsAsync"/> — тот же расчётный путь.
    /// TargetPositionId в ответе для этого случая = 0 (синтетическое значение watchlist-holding'а,
    /// см. doc-comment BuildForInstrumentsAsync) — TargetInstrumentId в ответе однозначно определяет
    /// бумагу.
    /// </para>
    /// </summary>
    private static async Task<IResult> PostReplacement(
        ReplacementRequestDto request,
        ClaimsPrincipal principal,
        IAccountRepository accountRepo,
        PortfolioHoldingsBuilder holdingsBuilder,
        ICommissionRateProvider commissionRateProvider,
        CancellationToken ct)
    {
        var accountId = await PositionsEndpoints.ResolveAccountIdAsync(principal, accountRepo);
        if (accountId is null) throw new NotFoundException("Счёт не найден");

        var asOf = BusinessClock.MoscowToday();
        var holdings = await holdingsBuilder.BuildForAccountAsync(accountId.Value, asOf);

        var holdPosition = holdings.FirstOrDefault(h => h.PositionId == request.HoldPositionId);
        if (holdPosition is null) throw new NotFoundException($"Позиция {request.HoldPositionId} не найдена в портфеле");

        Core.Analytics.PortfolioHolding? targetPosition;
        if (request.TargetInstrumentId is not null)
        {
            var watchlistHoldings = await holdingsBuilder.BuildForInstrumentsAsync([request.TargetInstrumentId.Value], asOf);
            targetPosition = watchlistHoldings.FirstOrDefault(h => h.InstrumentId == request.TargetInstrumentId.Value)
                // Инструмент может быть и текущей позицией (watchlist-бумага, которую уже купили) —
                // тогда берём её из holdings портфеля, чтобы не терять реальный Quantity/MarketValueRub.
                ?? holdings.FirstOrDefault(h => h.InstrumentId == request.TargetInstrumentId.Value);
            if (targetPosition is null) throw new NotFoundException($"Инструмент {request.TargetInstrumentId} не найден");
        }
        else
        {
            targetPosition = holdings.FirstOrDefault(h => h.PositionId == request.TargetPositionId);
            if (targetPosition is null) throw new NotFoundException($"Позиция {request.TargetPositionId} не найдена в портфеле");
        }

        // Задача 31 часть B.4: цель-флоатер/индексируемая несравнима по доходности с фикс-купоном —
        // её "спред" = CurrentYield(флоатер) − YtmEffective(фикс), бессмысленная величина. Фронт
        // (задача 32) заранее исключит такие бумаги из выпадашки target'ов, это — защита контракта
        // на случай прямого вызова API. DataIncomplete НЕ блокирует здесь (в отличие от полного
        // ReplacementMatrixService.IsComparable) — та ветка уже штатно обслуживается через
        // YieldDataIncomplete=true с 200 (см. PostReplacement_BetweenTwoPositions..., Stage08).
        // HoldPosition намеренно не проверяется — план ограничивает 422 только целью (см. план
        // задачи 31 часть B.4).
        if (targetPosition.IsFloater || targetPosition.IsIndexed)
        {
            return ValidationError(
                "Бумага с плавающим/индексируемым купоном несравнима по доходности с фикс-купонной " +
                "бумагой — выберите фикс-купонную бумагу для сравнения.");
        }

        if (request.HorizonYears <= 0m) throw new ValidationException("HorizonYears должен быть положительным");

        var holdCandidate = new SwitchCandidate
        {
            PositionId = holdPosition.PositionId,
            MarketValueRub = holdPosition.MarketValueRub,
            EffectiveYield = (holdPosition.IsFloater || holdPosition.IsIndexed) ? holdPosition.CurrentYield : holdPosition.YtmEffective,
        };
        var targetCandidate = new SwitchCandidate
        {
            // SwitchAnalysisService.Compare не использует targetPosition.MarketValueRub в расчёте
            // (капитал перехода — netProceedsAfterSale от holdPosition, см. doc-comment сервиса) —
            // поле сохраняется как есть для watchlist-target'а (MarketValueRub holding'а без позиции
            // = грязная цена ОДНОЙ бумаги, см. BuildForInstrumentsAsync), значение не искажает результат.
            PositionId = targetPosition.PositionId,
            MarketValueRub = targetPosition.MarketValueRub,
            EffectiveYield = (targetPosition.IsFloater || targetPosition.IsIndexed) ? targetPosition.CurrentYield : targetPosition.YtmEffective,
        };

        // Plan/22 часть E: явные ставки в запросе по-прежнему побеждают резолвер (контракт не
        // ломается) — резолвер вызывается, только если хотя бы одна из ставок не передана явно.
        ResolvedCommissionRate? resolved = null;
        if (request.SellCommissionRate is null || request.BuyCommissionRate is null)
        {
            resolved = await commissionRateProvider.GetAsync(accountId.Value, ct);
        }

        var sellRate = request.SellCommissionRate ?? resolved!.Rate;
        var buyRate = request.BuyCommissionRate ?? resolved!.Rate;

        var result = SwitchAnalysisService.Compare(holdCandidate, targetCandidate, request.HorizonYears, sellRate, buyRate);

        // Задача 27 часть B: та же построчная формула-разбивка, что у матрицы замен (задача 23/25) —
        // спред → капитал → горизонт → валовая выгода → минус комиссии → чистая → минус налог.
        // ReplacementMatrixService.BuildMatrix не переиспользуется целиком (там перебор ВСЕХ пар
        // портфеля/watchlist), но считает эти же величины по той же формуле — здесь тот же расчёт
        // для ОДНОЙ явно выбранной пары hold/target (наведение сравнивалки задачи 27, а не матрица).
        var netProceedsAfterSale = holdPosition.MarketValueRub - result.SellCommissionRub;
        decimal? spreadFraction = null;
        decimal? grossGainRub = null;
        decimal? annualizedBenefitFraction = null;
        if (holdCandidate.EffectiveYield is { } holdYield && targetCandidate.EffectiveYield is { } targetYield)
        {
            spreadFraction = targetYield - holdYield;
            grossGainRub = netProceedsAfterSale * spreadFraction.Value * request.HorizonYears;
            annualizedBenefitFraction = (netProceedsAfterSale > 0m && request.HorizonYears > 0m)
                ? result.NetBenefitRub / netProceedsAfterSale / request.HorizonYears
                : null;
        }

        var sellTaxEstimate = SaleTaxEstimator.Estimate(
            netProceedsAfterSale, holdPosition.CostBasis?.InvestedRub, holdPosition.CostBasis?.HasUnknownLots ?? true);
        var sellTaxEstimateRub = sellTaxEstimate?.TaxRub;
        var netBenefitAfterTaxRub = sellTaxEstimateRub is decimal tax ? result.NetBenefitRub - tax : (decimal?)null;

        var dto = new ReplacementResponseDto
        {
            HoldPositionId = result.HoldPositionId,
            TargetPositionId = result.TargetPositionId,
            TargetInstrumentId = request.TargetInstrumentId,
            HorizonYears = result.HorizonYears,
            SellCommissionRub = result.SellCommissionRub,
            BuyCommissionRub = result.BuyCommissionRub,
            TotalSwitchCostRub = result.TotalSwitchCostRub,
            NetBenefitRub = result.NetBenefitRub,
            IsSwitchFavorable = result.IsSwitchFavorable,
            BreakEvenYears = result.BreakEvenYears,
            YieldDataIncomplete = result.YieldDataIncomplete,
            Disclaimer = result.Disclaimer,
            // Plan/22 часть E: ставка(и) и источник, который реально применился (явный запрос
            // выигрывает — тогда Source отражается как "явно передана запросом", см. doc-comment DTO).
            SellCommissionRateUsed = sellRate,
            BuyCommissionRateUsed = buyRate,
            CommissionRateSource = resolved?.Source.ToString() ?? "ExplicitRequest",
            // Задача 27 часть B: формула-разбивка (см. doc-comment выше) — null, если доходность
            // одной из сторон не определена (YieldDataIncomplete=true).
            SpreadFraction = spreadFraction,
            CapitalRub = netProceedsAfterSale,
            GrossGainRub = grossGainRub,
            AnnualizedBenefitFraction = annualizedBenefitFraction,
            SellTaxEstimateRub = sellTaxEstimateRub,
            NetBenefitAfterTaxRub = netBenefitAfterTaxRub,
        };

        return Results.Ok(dto);
    }

    // ─── GET /api/analytics/replacement-matrix ──────────────────────────────────────────────

    /// <summary>
    /// Задача 23 — честная матрица замен: серверный перебор ВСЕХ пар «держать hold vs переложиться
    /// в target» (заменяет фронтовый цикл до 6 запросов POST /replacement, plan/23 §A). Кандидаты
    /// hold — все сравнимые (не floater/indexed/dataIncomplete, см.
    /// <see cref="ReplacementMatrixService.IsComparable"/>) позиции портфеля; кандидаты target —
    /// те же сравнимые позиции + watchlist-бумаги (тот же путь, что <see cref="PostReplacement"/> с
    /// TargetInstrumentId — <see cref="PortfolioHoldingsBuilder.BuildForInstrumentsAsync"/>).
    /// Ставка комиссии — из <see cref="ICommissionRateProvider"/> (задача 22), НЕ константа.
    /// </summary>
    private static async Task<IResult> GetReplacementMatrix(
        ClaimsPrincipal principal,
        IAccountRepository accountRepo,
        PortfolioHoldingsBuilder holdingsBuilder,
        IWatchlistItemRepository watchlistRepo,
        IInstrumentRepository instrumentRepo,
        ICommissionRateProvider commissionRateProvider,
        CancellationToken ct)
    {
        var accountId = await PositionsEndpoints.ResolveAccountIdAsync(principal, accountRepo);
        if (accountId is null)
        {
            return Results.Ok(new ReplacementMatrixResponseDto
            {
                BestPairs = [],
                RejectedPairs = [],
                TotalConsideredPairs = 0,
                Disclaimer = ReplacementMatrixService.Disclaimer,
            });
        }

        var asOf = BusinessClock.MoscowToday();
        var holdings = (await holdingsBuilder.BuildForAccountAsync(accountId.Value, asOf)).ToList();

        var resolvedRate = await commissionRateProvider.GetAsync(accountId.Value, ct);

        var comparableHoldings = holdings
            .Where(h => ReplacementMatrixService.IsComparable(h.IsFloater, h.IsIndexed, h.DataIncomplete))
            .ToList();

        var holdCandidates = comparableHoldings.Select(h => ToMatrixCandidate(h, asOf, isWatchlist: false)).ToList();
        var targetCandidates = new List<ReplacementMatrixService.MatrixCandidate>(holdCandidates);

        // Plan/23 п.1: watchlist-бумаги — только как target, тот же путь, что PostReplacement
        // с TargetInstrumentId/GetScatter/GetAllocation (BuildForInstrumentsAsync).
        var userIdClaim = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (ulong.TryParse(userIdClaim, out var userId))
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

            var watchlistHoldings = await holdingsBuilder.BuildForInstrumentsAsync(watchlistInstrumentIds.Distinct().ToList(), asOf);

            targetCandidates.AddRange(watchlistHoldings
                .Where(h => ReplacementMatrixService.IsComparable(h.IsFloater, h.IsIndexed, h.DataIncomplete))
                .Select(h => ToMatrixCandidate(h, asOf, isWatchlist: true)));
        }

        var result = ReplacementMatrixService.BuildMatrix(
            holdCandidates, targetCandidates, resolvedRate.Rate, resolvedRate.Rate, resolvedRate.Source);

        var dto = new ReplacementMatrixResponseDto
        {
            BestPairs = result.BestPairs.Select(ToMatrixPairDto).ToList(),
            RejectedPairs = result.RejectedPairs.Select(ToRejectedPairDto).ToList(),
            TotalConsideredPairs = result.TotalConsideredPairs,
            Disclaimer = result.Disclaimer,
        };

        return Results.Ok(dto);
    }

    private static ReplacementMatrixService.MatrixCandidate ToMatrixCandidate(Core.Analytics.PortfolioHolding h, DateOnly asOf, bool isWatchlist) => new()
    {
        PositionId = h.PositionId,
        InstrumentId = h.InstrumentId,
        Name = h.Name,
        Issuer = h.Issuer,
        MarketValueRub = h.MarketValueRub,
        EffectiveYield = h.YtmEffective,
        ModifiedDuration = h.ModifiedDuration,
        DaysToHorizon = h.HorizonDate.DayNumber - asOf.DayNumber,
        IsWatchlist = isWatchlist,
        // Задача 25: cost basis нужен только у HOLD-стороны пары (продаётся именно она) — у
        // watchlist-holding'ов CostBasis всегда null (см. doc-comment PortfolioHoldingsBuilder.
        // BuildForInstrumentsAsync), поэтому HoldHasUnknownLots по умолчанию true (см. MatrixCandidate) —
        // корректная деградация "оценить нельзя".
        HoldInvestedRub = h.CostBasis?.InvestedRub,
        HoldHasUnknownLots = h.CostBasis?.HasUnknownLots ?? true,
    };

    private static MatrixPairDto ToMatrixPairDto(ReplacementMatrixService.MatrixPair p) => new()
    {
        HoldPositionId = p.HoldPositionId,
        HoldInstrumentId = p.HoldInstrumentId,
        HoldName = p.HoldName,
        TargetPositionId = p.TargetPositionId,
        TargetInstrumentId = p.TargetInstrumentId,
        TargetName = p.TargetName,
        IsWatchlistTarget = p.IsWatchlistTarget,
        SpreadFraction = p.SpreadFraction,
        CapitalRub = p.CapitalRub,
        HorizonYears = p.HorizonYears,
        GrossGainRub = p.GrossGainRub,
        SellCommissionRub = p.SellCommissionRub,
        BuyCommissionRub = p.BuyCommissionRub,
        NetBenefitRub = p.NetBenefitRub,
        AnnualizedBenefitFraction = p.AnnualizedBenefitFraction,
        CommissionRateUsed = p.CommissionRateUsed,
        CommissionRateSource = p.CommissionRateSource.ToString(),
        SellTaxEstimateRub = p.SellTaxEstimateRub,
        NetBenefitAfterTaxRub = p.NetBenefitAfterTaxRub,
    };

    private static RejectedPairDto ToRejectedPairDto(ReplacementMatrixService.RejectedPair p) => new()
    {
        HoldPositionId = p.HoldPositionId,
        HoldInstrumentId = p.HoldInstrumentId,
        HoldName = p.HoldName,
        TargetPositionId = p.TargetPositionId,
        TargetInstrumentId = p.TargetInstrumentId,
        TargetName = p.TargetName,
        IsWatchlistTarget = p.IsWatchlistTarget,
        Reason = p.Reason.ToString(),
        NetBenefitRub = p.NetBenefitRub,
    };

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
    /// <para>
    /// <b>Задача 20:</b> <paramref name="includeWatchlist"/>=true добавляет к кандидатам watchlist-
    /// бумаги (<see cref="PortfolioHoldingsBuilder.BuildForInstrumentsAsync"/> — тот же расчётный
    /// путь, holdings с Quantity=1/MarketValueRub=цена одной бумаги). Их
    /// <c>CurrentIssuerMarketValueRub</c> берётся из уже существующих позиций того же эмитента (0,
    /// если эмитента ещё нет в портфеле) — лимит концентрации считается от факта владения, не от
    /// присутствия в watchlist. Дефолт false — не ломает существующий вызов без параметра.
    /// </para>
    /// </summary>
    private static async Task<IResult> GetAllocation(
        ClaimsPrincipal principal,
        IAccountRepository accountRepo,
        PortfolioHoldingsBuilder holdingsBuilder,
        IWatchlistItemRepository watchlistRepo,
        IInstrumentRepository instrumentRepo,
        ICommissionRateProvider commissionRateProvider,
        decimal amountRub,
        bool includeWatchlist = false,
        CancellationToken ct = default)
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
                CommissionRateUsed = SwitchAnalysisService.DefaultCommissionRate,
                CommissionRateSource = nameof(CommissionRateSource.Default),
            });
        }

        // Plan/22 часть E: ставка покупки для оценки грязной цены лота — через резолвер (override →
        // оценка из журнала → дефолт), не захардкоженная константа.
        var resolvedRate = await commissionRateProvider.GetAsync(accountId.Value, ct);
        var commissionRate = resolvedRate.Rate;

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
                // Задача 24: pricePerUnitRub — грязная цена (чистая + НКД на бумагу, см. doc-comment
                // PortfolioHoldingsBuilder); раскладываем на компоненты для UI, не для расчёта.
                var accruedRub = h.AccruedPerBondRub;
                var cleanPriceRub = pricePerUnitRub - accruedRub;
                var commissionRub = pricePerUnitRub * commissionRate;
                var pricePerLotWithCommission = pricePerUnitRub + commissionRub;
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
                    CleanPriceRub = cleanPriceRub,
                    AccruedRub = accruedRub,
                    CommissionRub = commissionRub,
                    // Задача 31 часть B.3: флоатер/индексируемая исключаются из аллокации через skip
                    // (NotComparable), не ранжируются вместе с обычной бумагой по CurrentYield.
                    // Намеренно НЕ полный ReplacementMatrixService.IsComparable (тот же, что PostReplacement/
                    // GetRelativeValue используют) — DataIncomplete здесь сознательно не гейтится этим
                    // флагом: бумага с неполными данными и без цены уже естественно получает
                    // EffectiveYield=null и уходит в существующий скип NoYield (см. тест
                    // GetAllocation_SeededPositionWithoutYield_Returns200_WithSkippedReason); заводить
                    // для неё ещё и NotComparable — не предмет задачи 31 (только флоатер/индексируемая).
                    IsComparable = !h.IsFloater && !h.IsIndexed,
                };
            })
            // Одна позиция на инструмент — если счёт по ошибке содержит несколько записей на один
            // InstrumentId, берём первую (защита от дублей, не ожидается в норме).
            .DistinctBy(c => c.InstrumentId)
            .ToList();

        if (includeWatchlist)
        {
            var portfolioInstrumentIds = candidates.Select(c => c.InstrumentId).ToHashSet();
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            var watchlistItems = ulong.TryParse(userId, out var uid)
                ? (await watchlistRepo.GetByUserIdAsync(uid)).ToList()
                : [];

            var watchlistInstrumentIds = new List<ulong>();
            foreach (var item in watchlistItems)
            {
                var instrument = await instrumentRepo.GetByIsinAsync(item.Isin);
                // Бумага уже есть в портфеле — не дублируем кандидата (позиция уже покрыта циклом выше).
                if (instrument is not null && !portfolioInstrumentIds.Contains(instrument.Id))
                {
                    watchlistInstrumentIds.Add(instrument.Id);
                }
            }

            var watchlistHoldings = await holdingsBuilder.BuildForInstrumentsAsync(watchlistInstrumentIds.Distinct().ToList(), asOf);

            var watchlistCandidates = watchlistHoldings
                .Where(h => !h.IsOutOfScopeCurrency)
                .Select(h =>
                {
                    // Задача 24: Quantity=1 у watchlist-holding (см. doc-comment BuildForInstrumentsAsync)
                    // — MarketValueRub уже цена одной бумаги (грязная).
                    var accruedRub = h.AccruedPerBondRub;
                    var cleanPriceRub = h.MarketValueRub - accruedRub;
                    var commissionRub = h.MarketValueRub * commissionRate;
                    var pricePerLotWithCommission = h.MarketValueRub + commissionRub;
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
                        CleanPriceRub = cleanPriceRub,
                        AccruedRub = accruedRub,
                        CommissionRub = commissionRub,
                        // Задача 31 часть B.3: см. комментарий в портфельной ветке выше — тот же предикат.
                        IsComparable = !h.IsFloater && !h.IsIndexed,
                    };
                })
                .ToList();

            candidates.AddRange(watchlistCandidates);
        }

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
                CleanCostRub = a.CleanCostRub,
                AccruedCostRub = a.AccruedCostRub,
                CommissionCostRub = a.CommissionCostRub,
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
            CommissionRateUsed = resolvedRate.Rate,
            CommissionRateSource = resolvedRate.Source.ToString(),
        };

        return Results.Ok(dto);
    }

    // ─── POST /api/analytics/basket ─────────────────────────────────────────────────────────

    /// <summary>
    /// Конструктор портфеля (plan/29 §B.2) — «собрал корзину процентами → штуки + what-if». Вход —
    /// сумма + строки {instrumentId, weightFraction} (доля 0..1, Σ ≤ 1 — та же конвенция, что
    /// <see cref="BasketBuilderService"/>). Кандидаты собираются тем же путём, что
    /// <see cref="GetAllocation"/> (holdings портфеля через <see cref="PortfolioHoldingsBuilder.BuildForAccountAsync"/>,
    /// бумаги вне портфеля — через <see cref="PortfolioHoldingsBuilder.BuildForInstrumentsAsync"/>,
    /// цена лота — грязная (clean+НКД) + комиссия из <see cref="ICommissionRateProvider"/>).
    /// Вызывает <see cref="BasketBuilderService.Build"/>, затем <see cref="PortfolioWhatIfService.Evaluate"/>
    /// (текущие holdings "до" + строки корзины "после"), лимит концентрации для warnings —
    /// <c>UserSettings.DefaultMaxConcentrationPercent</c> (тот же дефолт, что у аллокации/сигналов).
    /// <para>
    /// <b>Валидация (422):</b> amountRub &gt; 0; каждая строка weightFraction ∈ (0,1]; Σ весов ≤ 1.0001;
    /// instrumentId должен существовать (иначе неясно, что показывать пользователю — лучше явная
    /// ошибка, чем молчаливый пропуск строки).
    /// </para>
    /// </summary>
    private static async Task<IResult> PostBasket(
        ClaimsPrincipal principal,
        BasketRequestDto request,
        IAccountRepository accountRepo,
        IUserSettingsRepository userSettingsRepo,
        PortfolioHoldingsBuilder holdingsBuilder,
        IInstrumentRepository instrumentRepo,
        ICommissionRateProvider commissionRateProvider,
        Microsoft.Extensions.Options.IOptions<Bonds.Core.Signals.SignalEngineOptions> defaultSignalOptions,
        CancellationToken ct = default)
    {
        if (request.AmountRub <= 0m)
        {
            return ValidationError("amountRub должен быть положительным");
        }

        if (request.Lines is null || request.Lines.Count == 0)
        {
            return ValidationError("Корзина должна содержать хотя бы одну строку");
        }

        foreach (var line in request.Lines)
        {
            if (line.WeightFraction <= 0m || line.WeightFraction > 1m)
            {
                return ValidationError($"Вес строки instrumentId={line.InstrumentId} должен быть в диапазоне (0, 1]");
            }
        }

        var totalWeight = request.Lines.Sum(l => l.WeightFraction);
        if (totalWeight > 1.0001m)
        {
            return ValidationError($"Сумма весов строк ({totalWeight:0.####}) не может превышать 1");
        }

        var requestedInstrumentIds = request.Lines.Select(l => l.InstrumentId).ToList();
        if (requestedInstrumentIds.Distinct().Count() != requestedInstrumentIds.Count)
        {
            return ValidationError("Инструмент не может встречаться в корзине дважды");
        }

        foreach (var instrumentId in requestedInstrumentIds)
        {
            var instrument = await instrumentRepo.GetByIdAsync(instrumentId);
            if (instrument is null)
            {
                return ValidationError($"Инструмент instrumentId={instrumentId} не найден");
            }
        }

        var accountId = await PositionsEndpoints.ResolveAccountIdAsync(principal, accountRepo);
        var asOf = BusinessClock.MoscowToday();

        var resolvedRate = accountId is not null
            ? await commissionRateProvider.GetAsync(accountId.Value, ct)
            : new ResolvedCommissionRate(SwitchAnalysisService.DefaultCommissionRate, CommissionRateSource.Default, null);
        var commissionRate = resolvedRate.Rate;

        var portfolioHoldings = accountId is not null
            ? (await holdingsBuilder.BuildForAccountAsync(accountId.Value, asOf)).ToList()
            : [];

        var portfolioHoldingsByInstrument = portfolioHoldings.ToDictionary(h => h.InstrumentId);
        var missingInstrumentIds = requestedInstrumentIds.Where(id => !portfolioHoldingsByInstrument.ContainsKey(id)).Distinct().ToList();
        var extraHoldings = missingInstrumentIds.Count > 0
            ? await holdingsBuilder.BuildForInstrumentsAsync(missingInstrumentIds, asOf)
            : [];
        var extraHoldingsByInstrument = extraHoldings.ToDictionary(h => h.InstrumentId);

        var basketLineInputs = new List<BasketLineInput>(request.Lines.Count);
        foreach (var line in request.Lines)
        {
            var holding = portfolioHoldingsByInstrument.TryGetValue(line.InstrumentId, out var owned)
                ? owned
                : extraHoldingsByInstrument.GetValueOrDefault(line.InstrumentId);

            if (holding is null)
            {
                // Ссылочная целостность нарушилась между проверкой выше и сборкой holdings — не ожидается
                // в норме (инструмент существует, но BuildForInstrumentsAsync его не вернул из-за гонки).
                return ValidationError($"Не удалось получить котировку для instrumentId={line.InstrumentId}");
            }

            basketLineInputs.Add(BuildBasketLineInput(holding, line.WeightFraction, commissionRate));
        }

        var basketResult = BasketBuilderService.Build(request.AmountRub, basketLineInputs);

        var userIdClaim = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        var userSettings = ulong.TryParse(userIdClaim, out var userId) ? await userSettingsRepo.GetByUserIdAsync(userId) : null;
        var maxConcentrationPercent = userSettings?.DefaultMaxConcentrationPercent ?? defaultSignalOptions.Value.DefaultMaxConcentrationPercent;

        var whatIfHoldings = portfolioHoldings
            .Select(h => new WhatIfHoldingInput
            {
                InstrumentId = h.InstrumentId,
                Issuer = h.Issuer,
                MarketValueRub = h.MarketValueRub,
                EffectiveYield = (h.IsFloater || h.IsIndexed) ? h.CurrentYield : h.YtmEffective,
                ModifiedDuration = h.ModifiedDuration,
                IsFloater = h.IsFloater || h.IsIndexed,
            })
            .ToList();

        var whatIfResult = PortfolioWhatIfService.Evaluate(whatIfHoldings, basketResult.Lines, maxConcentrationPercent);

        return Results.Ok(new BasketResponseDto
        {
            Basket = ToBasketDto(basketResult),
            WhatIf = ToWhatIfDto(whatIfResult),
            Disclaimer = BasketBuilderService.Disclaimer,
        });
    }

    private static BasketLineInput BuildBasketLineInput(
        PortfolioHolding holding, decimal weightFraction, decimal commissionRate)
    {
        // Тот же расчёт цены лота, что GetAllocation/BuildCandidate: MarketValueRub уже грязная цена
        // (clean+НКД) на Quantity штук; для holding'ов вне портфеля (BuildForInstrumentsAsync) Quantity=1,
        // поэтому pricePerUnitRub = MarketValueRub в обоих случаях (та же формула).
        var pricePerUnitRub = holding.Quantity > 0m ? holding.MarketValueRub / holding.Quantity : holding.MarketValueRub;
        var accruedRub = holding.AccruedPerBondRub;
        var cleanPriceRub = pricePerUnitRub - accruedRub;
        var commissionRub = pricePerUnitRub * commissionRate;
        var pricePerLotWithCommission = pricePerUnitRub + commissionRub;
        var effectiveYield = (holding.IsFloater || holding.IsIndexed) ? holding.CurrentYield : holding.YtmEffective;

        return new BasketLineInput
        {
            InstrumentId = holding.InstrumentId,
            Name = holding.Name,
            Issuer = holding.Issuer,
            TargetWeightFraction = weightFraction,
            PricePerLotRub = pricePerLotWithCommission,
            CleanPriceRub = cleanPriceRub,
            AccruedRub = accruedRub,
            CommissionRub = commissionRub,
            LotSize = 1m,
            LotSizeIsAssumed = true,
            EffectiveYield = effectiveYield,
            ModifiedDuration = holding.ModifiedDuration,
            IsFloater = holding.IsFloater || holding.IsIndexed,
        };
    }

    private static BasketDto ToBasketDto(BasketBuildResult result) => new()
    {
        AmountRub = result.AmountRub,
        Lines = result.Lines.Select(l => new BasketLineDto
        {
            InstrumentId = l.InstrumentId,
            Name = l.Name,
            Issuer = l.Issuer,
            TargetWeightFraction = l.TargetWeightFraction,
            ActualWeightFraction = l.ActualWeightFraction,
            Quantity = l.Quantity,
            ActualCostRub = l.ActualCostRub,
            EffectiveYield = l.EffectiveYield,
            ModifiedDuration = l.ModifiedDuration,
            IsFloater = l.IsFloater,
            LotSizeAssumed = l.LotSizeAssumed,
            CleanCostRub = l.CleanCostRub,
            AccruedCostRub = l.AccruedCostRub,
            CommissionCostRub = l.CommissionCostRub,
        }).ToList(),
        LeftoverRub = result.LeftoverRub,
        Metrics = new BasketMetricsDto
        {
            TotalCostRub = result.Metrics.TotalCostRub,
            WeightedYield = result.Metrics.WeightedYield,
            WeightedDuration = result.Metrics.WeightedDuration,
            HasExcludedFloaters = result.Metrics.HasExcludedFloaters,
        },
    };

    private static WhatIfDto ToWhatIfDto(PortfolioWhatIfResult result) => new()
    {
        Before = new WhatIfSnapshotDto
        {
            TotalValueRub = result.Before.TotalValueRub,
            WeightedYield = result.Before.WeightedYield,
            WeightedDuration = result.Before.WeightedDuration,
            HasExcludedFloaters = result.Before.HasExcludedFloaters,
        },
        After = new WhatIfSnapshotDto
        {
            TotalValueRub = result.After.TotalValueRub,
            WeightedYield = result.After.WeightedYield,
            WeightedDuration = result.After.WeightedDuration,
            HasExcludedFloaters = result.After.HasExcludedFloaters,
        },
        Concentrations = result.Concentrations.Select(c => new WhatIfConcentrationDto
        {
            Issuer = c.Issuer,
            SharePercentBefore = c.SharePercentBefore,
            SharePercentAfter = c.SharePercentAfter,
        }).ToList(),
        Warnings = result.Warnings.Select(w => new WhatIfWarningDto
        {
            Kind = w.Kind.ToString(),
            Issuer = w.Issuer,
            SharePercentAfter = w.SharePercentAfter,
        }).ToList(),
    };

    // ─── GET /api/analytics/relative-value ─────────────────────────────────────────────────────

    /// <summary>Порог |deviation| для Fair-вердикта (план часть C.1): меньше — «в рынке», не
    /// заслуживает бейджа. 0.0020 = 20 базисных пунктов (доля, см. общая конвенция единиц репо).</summary>
    public const decimal FairVerdictThresholdFraction = 0.0020m;

    /// <summary>Сколько дешёвых кандидатов из корзины показывать для каждой Rich-позиции (план часть C.2).</summary>
    public const int TopCheapCandidatesPerRichPosition = 3;

    public const string RelativeValueDisclaimer =
        "Относительная дешевизна/дороговизна считается против медианного G-спреда бумаг ТОЙ ЖЕ " +
        "корзины (сектор × срок) — это НЕ оценка кредитного качества эмитента: большой спред может " +
        "означать реальный риск дефолта, а не недооценку рынком. Аналитическая оценка, не " +
        "инвестиционная рекомендация.";

    /// <summary>
    /// Задача 30 часть C — для каждой сравнимой позиции портфеля (не floater/indexed/dataIncomplete —
    /// тот же предикат, что <see cref="ReplacementMatrixService.IsComparable"/>) считает отклонение
    /// точного G-спреда от сглаженной медианы её корзины (сектор × дюрационный бакет,
    /// <see cref="RelativeValueSnapshotBuilder"/>), верdict Cheap/Fair/Rich и для Rich-позиций —
    /// топ-3 дешёвых кандидатов ИЗ ИХ ЖЕ корзины (не скрытых бумаг банка облигаций).
    /// </summary>
    private static async Task<IResult> GetRelativeValue(
        ClaimsPrincipal principal,
        IAccountRepository accountRepo,
        PortfolioHoldingsBuilder holdingsBuilder,
        RelativeValueSnapshotBuilder snapshotBuilder,
        CancellationToken ct)
    {
        var accountId = await PositionsEndpoints.ResolveAccountIdAsync(principal, accountRepo);
        if (accountId is null)
        {
            return Results.Ok(new RelativeValueResponseDto { Positions = [], Disclaimer = RelativeValueDisclaimer });
        }

        var asOf = BusinessClock.MoscowToday();
        var holdings = await holdingsBuilder.BuildForAccountAsync(accountId.Value, asOf);
        var comparableHoldings = holdings
            .Where(h => ReplacementMatrixService.IsComparable(h.IsFloater, h.IsIndexed, h.DataIncomplete))
            .Where(h => h.GSpread is not null)
            .ToList();

        var snapshot = await snapshotBuilder.GetSnapshotAsync(ct);

        var positionDtos = new List<RelativeValuePositionDto>(comparableHoldings.Count);
        foreach (var holding in comparableHoldings)
        {
            var assessment = Assess(holding.Sector, holding.ModifiedDuration, holding.GSpread!.Value, snapshot.AllMembers, snapshot.BasketStats);
            var verdict = ResolveVerdict(assessment.DeviationFraction);

            var candidates = verdict == RelativeValueVerdict.Rich
                ? BuildCheapCandidates(assessment.Basket.EffectiveBasket, snapshot, holding.Isin)
                : [];

            positionDtos.Add(new RelativeValuePositionDto
            {
                PositionId = holding.PositionId,
                Basket = new RelativeValueBasketDto
                {
                    Sector = assessment.Basket.EffectiveBasket.Sector,
                    DurationBucket = assessment.Basket.EffectiveBasket.DurationBucket,
                    Count = assessment.Basket.Stats.Count,
                    Confidence = assessment.Basket.Confidence.ToString(),
                },
                DeviationFraction = assessment.DeviationFraction,
                Percentile = assessment.Percentile,
                Verdict = verdict.ToString(),
                BasedOnDays = snapshot.BasedOnDays,
                CheapCandidates = candidates,
            });
        }

        return Results.Ok(new RelativeValueResponseDto
        {
            Positions = positionDtos,
            Disclaimer = RelativeValueDisclaimer,
        });
    }

    /// <summary>Cheap/Fair/Rich по знаку и порогу <see cref="FairVerdictThresholdFraction"/> (план часть C.1):
    /// |deviation| &lt; порог → Fair (в рынке); иначе положительное — Cheap (спред выше медианы,
    /// рынок недооценивает/закладывает риск), отрицательное — Rich (спред ниже медианы, дорого
    /// относительно соседей по корзине, кандидат на пересмотр).</summary>
    private static RelativeValueVerdict ResolveVerdict(decimal deviationFraction)
    {
        if (Math.Abs(deviationFraction) < FairVerdictThresholdFraction) return RelativeValueVerdict.Fair;
        return deviationFraction > 0 ? RelativeValueVerdict.Cheap : RelativeValueVerdict.Rich;
    }

    /// <summary>
    /// Топ-N дешёвых кандидатов ИЗ ТОЙ ЖЕ эффективной корзины (после fallback — план часть C.2:
    /// "кандидаты — из корзины, не скрытые") для Rich-позиции, отсортированные по deviation убыв.
    /// Кандидаты берутся из <see cref="RelativeValueSnapshotBuilder.RelativeValueSnapshot.AllMembers"/>
    /// (уже НЕ скрытые гигиеническим фильтром — см. doc-comment билдера) и обогащаются
    /// именем/доходностью/ликвидностью из текущего <c>bond_universe</c> (<c>CurrentEntriesBySecid</c>)
    /// — <see cref="RelativeValueService.BasketMember"/> сам по себе несёт только поля, нужные
    /// для статистики корзин.
    /// <para>
    /// Задача 31 часть B.2: флоатеры сюда попасть не могут — <c>snapshot.AllMembers</c> уже не
    /// содержит их (исключены в <see cref="RelativeValueSnapshotBuilder"/> ДО конструирования
    /// <see cref="RelativeValueService.BasketMember"/>, см. doc-comment билдера), поэтому здесь не
    /// нужен ещё один фильтр — он был бы избыточен.
    /// </para>
    /// </summary>
    private static List<RelativeValueCandidateDto> BuildCheapCandidates(
        BasketKey effectiveBasket, RelativeValueSnapshotBuilder.RelativeValueSnapshot snapshot, string? positionIsin)
    {
        var basketMedian = snapshot.BasketStats.TryGetValue(effectiveBasket, out var stats) ? stats.Median : 0m;

        var basketMembers = snapshot.AllMembers
            .Where(m => string.Equals(m.Sector ?? UnknownSector, effectiveBasket.Sector, StringComparison.OrdinalIgnoreCase))
            .Where(m => effectiveBasket.DurationBucket is SectorWideBucketLabel or MarketWideLabel
                || Bonds.Core.Analytics.DurationBucketClassifier.Label(m.DurationYears) == effectiveBasket.DurationBucket)
            .Where(m => m.GSpreadFraction is not null)
            // Self-exclusion (ревью T-30, MAJOR): банк — вся вселенная MOEX, оцениваемая позиция
            // почти наверняка присутствует в нём под своим secid, а её approx-спред из банка
            // отличается от точного спреда движка — без исключения Rich-позиция могла бы
            // порекомендовать САМУ СЕБЯ как «дешёвого соседа» («купи то же самое дешевле»).
            // Исключаем по ISIN ДО Take, чтобы самоссылка не съедала слот кандидата.
            .Where(m => positionIsin is null
                || !snapshot.CurrentEntriesBySecid.TryGetValue(m.Secid, out var entryForIsin)
                || !string.Equals(entryForIsin.Isin, positionIsin, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(m => m.GSpreadFraction!.Value - basketMedian)
            .Take(TopCheapCandidatesPerRichPosition)
            .ToList();

        var result = new List<RelativeValueCandidateDto>(basketMembers.Count);
        foreach (var member in basketMembers)
        {
            snapshot.CurrentEntriesBySecid.TryGetValue(member.Secid, out var entry);
            var liquidity = LiquidityScoreCalculator.Assess(entry?.TurnoverRub, entry?.BidPercent, entry?.OfferPercent, entry?.NumTrades);

            result.Add(new RelativeValueCandidateDto
            {
                Secid = member.Secid,
                Name = entry?.ShortName ?? entry?.SecName,
                YieldFraction = entry?.YieldFraction,
                DeviationFraction = member.GSpreadFraction!.Value - basketMedian,
                LiquidityScore = liquidity.Score.ToString(),
            });
        }

        return result;
    }

    private static IResult ValidationError(string message) =>
        Results.Json(new { error = message, type = "ValidationException" }, statusCode: StatusCodes.Status422UnprocessableEntity);
}

/// <summary>Verdict одной позиции (план часть C.1): Cheap — спред заметно выше медианы корзины
/// (рынок недооценивает/закладывает риск), Rich — заметно ниже (дорого относительно соседей,
/// кандидат на пересмотр), Fair — в пределах порога (см. <see cref="AnalyticsEndpoints.FairVerdictThresholdFraction"/>).</summary>
public enum RelativeValueVerdict
{
    Cheap,
    Fair,
    Rich,
}

public sealed record RelativeValueResponseDto
{
    public required IReadOnlyList<RelativeValuePositionDto> Positions { get; init; }
    public required string Disclaimer { get; init; }
}

public sealed record RelativeValuePositionDto
{
    public required ulong PositionId { get; init; }
    public required RelativeValueBasketDto Basket { get; init; }

    /// <summary>ДОЛЯ (0.002 = 20 б.п., см. общая конвенция единиц репо — б.п. рисует фронт formatBp).</summary>
    public required decimal DeviationFraction { get; init; }

    /// <summary>Перцентиль внутри эффективной корзины (0-100).</summary>
    public required decimal Percentile { get; init; }

    public required string Verdict { get; init; }

    /// <summary>Сколько торговых дней истории легло в основу сглаженной медианы (0 — банк молодой,
    /// использован единственный текущий снимок, см. RelativeValueSnapshotBuilder).</summary>
    public required int BasedOnDays { get; init; }

    /// <summary>Топ-3 дешёвых кандидата из ЕЁ ЖЕ корзины — только для Rich-позиций, иначе пусто.</summary>
    public required IReadOnlyList<RelativeValueCandidateDto> CheapCandidates { get; init; }
}

public sealed record RelativeValueBasketDto
{
    public required string Sector { get; init; }
    public required string DurationBucket { get; init; }
    public required int Count { get; init; }

    /// <summary>High/Medium/Low — см. <see cref="RelativeValueService.RelativeValueConfidence"/>.</summary>
    public required string Confidence { get; init; }
}

public sealed record RelativeValueCandidateDto
{
    public required string Secid { get; init; }
    public string? Name { get; init; }
    public decimal? YieldFraction { get; init; }

    /// <summary>ДОЛЯ — положительное значение (кандидат «дешевле» медианы своей корзины).</summary>
    public required decimal DeviationFraction { get; init; }

    /// <summary>High/Medium/Low/None — см. <see cref="Bonds.Core.Universe.LiquidityScore"/>.</summary>
    public required string LiquidityScore { get; init; }
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

    /// <summary>
    /// Audit(portfolio) P-1: В ПРОЦЕНТАХ (0-100), НЕ в долях — осознанное исключение из
    /// конвенции «бэкенд = доли». Фронт применяет <c>formatSharePercent</c>, НЕ <c>formatPercent</c>.
    /// См. doc-comment <see cref="Bonds.Core.Analytics.CompositionShare.SharePercent"/>.
    /// </summary>
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

    /// <summary>Задача 20: true — точка watchlist-бумаги без позиции (полый маркер, отдельная категория легенды на фронте).</summary>
    public required bool IsWatchlist { get; init; }
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

    /// <summary>Target — позиция портфеля (контракт не менялся). Игнорируется, если задан <see cref="TargetInstrumentId"/> (задача 20).</summary>
    public ulong TargetPositionId { get; init; }

    /// <summary>Задача 20: target — бумага БЕЗ позиции (watchlist), ищется по InstrumentId. Взаимно исключим с TargetPositionId — если задан, он приоритетнее.</summary>
    public ulong? TargetInstrumentId { get; init; }
    public required decimal HorizonYears { get; init; }
    public decimal? SellCommissionRate { get; init; }
    public decimal? BuyCommissionRate { get; init; }
}

public sealed record ReplacementResponseDto
{
    public required ulong HoldPositionId { get; init; }

    /// <summary>0 — target был найден по TargetInstrumentId (watchlist-holding без позиции, см. doc-comment BuildForInstrumentsAsync), не по позиции портфеля.</summary>
    public required ulong TargetPositionId { get; init; }

    /// <summary>Эхо запроса — задан, только если target был watchlist-бумагой без позиции (задача 20).</summary>
    public ulong? TargetInstrumentId { get; init; }
    public required decimal HorizonYears { get; init; }
    public required decimal SellCommissionRub { get; init; }
    public required decimal BuyCommissionRub { get; init; }
    public required decimal TotalSwitchCostRub { get; init; }
    public required decimal NetBenefitRub { get; init; }
    public required bool IsSwitchFavorable { get; init; }
    public decimal? BreakEvenYears { get; init; }
    public required bool YieldDataIncomplete { get; init; }
    public required string Disclaimer { get; init; }

    /// <summary>Plan/22 часть E: фактически применённая ставка продажи — ДОЛЯ (явная ставка запроса, если была передана, иначе резолвер части C).</summary>
    public required decimal SellCommissionRateUsed { get; init; }

    /// <summary>Plan/22 часть E: фактически применённая ставка покупки — ДОЛЯ.</summary>
    public required decimal BuyCommissionRateUsed { get; init; }

    /// <summary>Plan/22 часть E: источник ставки — строка <see cref="CommissionRateSource"/> либо "ExplicitRequest" (обе ставки заданы явно в запросе — резолвер не вызывался).</summary>
    public required string CommissionRateSource { get; init; }

    /// <summary>Задача 27 часть B: спред эффективных доходностей (targetYield − holdYield) — ДОЛЯ. Null — YieldDataIncomplete.</summary>
    public decimal? SpreadFraction { get; init; }

    /// <summary>Задача 27 часть B: капитал, реально переходящий в target (MarketValueRub hold минус комиссия продажи), в рублях — та же величина, что MatrixPair.CapitalRub.</summary>
    public required decimal CapitalRub { get; init; }

    /// <summary>Задача 27 часть B: валовая выгода (спред × капитал × горизонт) ДО вычета комиссий, в рублях. Null — YieldDataIncomplete.</summary>
    public decimal? GrossGainRub { get; init; }

    /// <summary>Задача 27 часть B: NetBenefitRub / CapitalRub / HorizonYears — ДОЛЯ (не процент), та же формула, что MatrixPair.AnnualizedBenefitFraction.</summary>
    public decimal? AnnualizedBenefitFraction { get; init; }

    /// <summary>Задача 27 часть B: оценка НДФЛ от продажи hold-позиции (13% с прибыли к средней цене входа) — null, если cost basis недоступен/журнал неполон.</summary>
    public decimal? SellTaxEstimateRub { get; init; }

    /// <summary>Задача 27 часть B: NetBenefitRub − SellTaxEstimateRub — выгода после налога. Null, если SellTaxEstimateRub недоступен.</summary>
    public decimal? NetBenefitAfterTaxRub { get; init; }
}

/// <summary>Задача 23 — ответ GET /api/analytics/replacement-matrix (см. doc-comment <see cref="ReplacementMatrixService"/>).</summary>
public sealed record ReplacementMatrixResponseDto
{
    /// <summary>Пары с netBenefit &gt; 0, отсортированы по netBenefit убыв.</summary>
    public required IReadOnlyList<MatrixPairDto> BestPairs { get; init; }

    /// <summary>Пары с netBenefit &lt;= 0 (reason=notProfitable) или вне окна дюраций (reason=durationMismatch). Пары с targetYield &lt;= holdYield сюда не попадают вовсе (см. doc-comment сервиса).</summary>
    public required IReadOnlyList<RejectedPairDto> RejectedPairs { get; init; }

    /// <summary>BestPairs.Count + RejectedPairs.Count ДО срабатывания предохранителя — честное число рассмотренных пар для пустого состояния фронта.</summary>
    public required int TotalConsideredPairs { get; init; }
    public required string Disclaimer { get; init; }
}

public sealed record MatrixPairDto
{
    public required ulong HoldPositionId { get; init; }
    public required ulong HoldInstrumentId { get; init; }
    public string? HoldName { get; init; }

    public required ulong TargetPositionId { get; init; }
    public required ulong TargetInstrumentId { get; init; }
    public string? TargetName { get; init; }

    /// <summary>true — target это watchlist-бумага без позиции в портфеле.</summary>
    public required bool IsWatchlistTarget { get; init; }

    /// <summary>Спред эффективных доходностей (targetYield - holdYield) — ДОЛЯ.</summary>
    public required decimal SpreadFraction { get; init; }

    /// <summary>Капитал, реально переходящий в target (MarketValueRub hold минус комиссия продажи), в рублях.</summary>
    public required decimal CapitalRub { get; init; }
    public required decimal HorizonYears { get; init; }

    /// <summary>Валовая выгода (спред × капитал × горизонт), ДО вычета комиссий обеих сделок, в рублях.</summary>
    public required decimal GrossGainRub { get; init; }
    public required decimal SellCommissionRub { get; init; }
    public required decimal BuyCommissionRub { get; init; }

    /// <summary>GrossGainRub - SellCommissionRub - BuyCommissionRub — чистая выгода в рублях, всегда &gt; 0 для bestPairs.</summary>
    public required decimal NetBenefitRub { get; init; }

    /// <summary>NetBenefitRub / CapitalRub / HorizonYears — ДОЛЯ (не процент, фронт умножает на 100), "выгода в терминах годовой доходности" — см. doc-comment ReplacementMatrixService.</summary>
    public decimal? AnnualizedBenefitFraction { get; init; }

    /// <summary>Ставка комиссии, применённая к обеим сделкам пары — ДОЛЯ.</summary>
    public required decimal CommissionRateUsed { get; init; }

    /// <summary>Строка <see cref="CommissionRateSource"/> (задача 22) — источник CommissionRateUsed.</summary>
    public required string CommissionRateSource { get; init; }

    /// <summary>Задача 25: оценка НДФЛ от продажи hold-позиции (13% с прибыли к средней цене входа) — null, если cost basis hold-позиции недоступен/журнал неполон.</summary>
    public decimal? SellTaxEstimateRub { get; init; }

    /// <summary>Задача 25: NetBenefitRub − SellTaxEstimateRub — выгода после налога. Null, если SellTaxEstimateRub недоступен (ранжирование/фильтр используют NetBenefitAfterTaxRub ?? NetBenefitRub).</summary>
    public decimal? NetBenefitAfterTaxRub { get; init; }
}

public sealed record RejectedPairDto
{
    public required ulong HoldPositionId { get; init; }
    public required ulong HoldInstrumentId { get; init; }
    public string? HoldName { get; init; }

    public required ulong TargetPositionId { get; init; }
    public required ulong TargetInstrumentId { get; init; }
    public string? TargetName { get; init; }
    public required bool IsWatchlistTarget { get; init; }

    /// <summary>Строка <see cref="ReplacementMatrixService.RejectedPairReason"/>: "NotProfitable" | "DurationMismatch".</summary>
    public required string Reason { get; init; }

    /// <summary>Заполнено только для Reason="NotProfitable" — чистая выгода в рублях (&lt;= 0).</summary>
    public decimal? NetBenefitRub { get; init; }
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

    /// <summary>
    /// Audit(portfolio) P-1: В ПРОЦЕНТАХ (0-100), НЕ в долях — осознанное исключение из
    /// конвенции «бэкенд = доли». Фронт (Analytics.tsx) читает через <c>.toFixed(2)</c>
    /// напрямую, БЕЗ <c>formatPercent</c>. См. doc-comment
    /// <see cref="Bonds.Core.Analytics.RateScenarioPoint.DeltaPercent"/>.
    /// </summary>
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

    /// <summary>Plan/22 часть E: ставка комиссии покупки, применённая к цене лота — ДОЛЯ (резолвер части C).</summary>
    public required decimal CommissionRateUsed { get; init; }

    /// <summary>Plan/22 часть E: источник ставки — строка <see cref="CommissionRateSource"/>.</summary>
    public required string CommissionRateSource { get; init; }
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

    /// <summary>Задача 24: разложение EstimatedCostRub — чистая цена всей докупки (без НКД/комиссии). CleanCostRub + AccruedCostRub + CommissionCostRub = EstimatedCostRub.</summary>
    public decimal CleanCostRub { get; init; }

    /// <summary>Задача 24: НКД в составе EstimatedCostRub.</summary>
    public decimal AccruedCostRub { get; init; }

    /// <summary>Задача 24: комиссия покупки в составе EstimatedCostRub.</summary>
    public decimal CommissionCostRub { get; init; }
}

public sealed record AllocationSkipDto
{
    public required ulong InstrumentId { get; init; }
    public string? Name { get; init; }
    public string? Issuer { get; init; }
    public required string Reason { get; init; }
}

// ─── POST /api/analytics/basket (plan/29 §B) ───────────────────────────────────────────────

/// <summary>Тело запроса POST /api/analytics/basket — сумма + строки корзины (доля, НЕ проценты — конвенция репо).</summary>
public sealed record BasketRequestDto
{
    public required decimal AmountRub { get; init; }
    public required IReadOnlyList<BasketRequestLineDto> Lines { get; init; }
}

public sealed record BasketRequestLineDto
{
    public required ulong InstrumentId { get; init; }

    /// <summary>Целевая доля суммы корзины — доля (0, 1], НЕ проценты.</summary>
    public required decimal WeightFraction { get; init; }
}

/// <summary>Ответ POST /api/analytics/basket — собранная корзина + what-if всего портфеля.</summary>
public sealed record BasketResponseDto
{
    public required BasketDto Basket { get; init; }
    public required WhatIfDto WhatIf { get; init; }
    public required string Disclaimer { get; init; }
}

public sealed record BasketDto
{
    public required decimal AmountRub { get; init; }
    public required IReadOnlyList<BasketLineDto> Lines { get; init; }
    public required decimal LeftoverRub { get; init; }
    public required BasketMetricsDto Metrics { get; init; }
}

public sealed record BasketLineDto
{
    public required ulong InstrumentId { get; init; }
    public string? Name { get; init; }
    public string? Issuer { get; init; }
    public required decimal TargetWeightFraction { get; init; }
    public required decimal ActualWeightFraction { get; init; }
    public required decimal Quantity { get; init; }
    public required decimal ActualCostRub { get; init; }
    public decimal? EffectiveYield { get; init; }
    public decimal? ModifiedDuration { get; init; }
    public bool IsFloater { get; init; }
    public required bool LotSizeAssumed { get; init; }
    public decimal CleanCostRub { get; init; }
    public decimal AccruedCostRub { get; init; }
    public decimal CommissionCostRub { get; init; }
}

public sealed record BasketMetricsDto
{
    public required decimal TotalCostRub { get; init; }
    public decimal? WeightedYield { get; init; }
    public decimal? WeightedDuration { get; init; }
    public required bool HasExcludedFloaters { get; init; }
}

public sealed record WhatIfDto
{
    public required WhatIfSnapshotDto Before { get; init; }
    public required WhatIfSnapshotDto After { get; init; }
    public required IReadOnlyList<WhatIfConcentrationDto> Concentrations { get; init; }
    public required IReadOnlyList<WhatIfWarningDto> Warnings { get; init; }
}

public sealed record WhatIfSnapshotDto
{
    public required decimal TotalValueRub { get; init; }
    public decimal? WeightedYield { get; init; }
    public decimal? WeightedDuration { get; init; }
    public required bool HasExcludedFloaters { get; init; }
}

public sealed record WhatIfConcentrationDto
{
    public required string Issuer { get; init; }
    public required decimal SharePercentBefore { get; init; }
    public required decimal SharePercentAfter { get; init; }
}

public sealed record WhatIfWarningDto
{
    public required string Kind { get; init; }
    public required string Issuer { get; init; }
    public required decimal SharePercentAfter { get; init; }
}

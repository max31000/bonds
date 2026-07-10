using System.Security.Claims;
using Bonds.Api.Middleware;
using Bonds.Core.Analytics;
using Bonds.Core.Calculation;
using Bonds.Core.CashFlow;
using Bonds.Core.Interfaces;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Bonds.Core.Time;
using Bonds.Core.Universe;
using Bonds.Infrastructure.Analytics;
using Bonds.Infrastructure.Universe;
using Microsoft.Extensions.Options;
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
        app.MapGet("/api/analytics/replacement-candidates", GetReplacementCandidates);
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
        RelativeValueSnapshotBuilder snapshotBuilder,
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

        // Задача 33 часть B.4: риск-сигналы для ВЫБРАННОЙ цели (не hold — план ограничивает объём
        // до "для выбранной цели") по её банк-записи (bond_universe), найденной по ISIN — снимок RV
        // уже загружен (singleton-кэш, ~раз в час), лишний I/O не добавляется. Null, если целевая
        // бумага не найдена в банке (не покрыта биржевой статистикой MOEX — например, только что
        // купленный внебиржевой инструмент) — тогда фронт просто не показывает сигналы, это не ошибка.
        var snapshotForTarget = await snapshotBuilder.GetSnapshotAsync(ct);
        var targetRiskSignals = targetPosition.Isin is { } targetIsin
            ? BuildRiskSignalsForIsin(targetIsin, snapshotForTarget)
            : null;

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
            TargetRiskSignals = targetRiskSignals,
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
    /// «Куда вложить сумму» (plan/17 §B, задача 34 — источники кандидатов). Жадное распределение
    /// <paramref name="amountRub"/> через <see cref="CashAllocationService"/>. <paramref name="source"/>
    /// выбирает кандидатов: <c>portfolio</c> (дефолт, обратная совместимость — прежнее поведение)
    /// строит кандидатов из <see cref="PortfolioHoldingsBuilder"/> holdings — тот же вход, что у
    /// comparison/replacement, лот берётся равным 1 (в модели <c>Instrument</c> нет поля размера
    /// лота — см. doc-comment плана: при отсутствии источника лота считаем 1 и помечаем
    /// <c>lotSizeAssumed</c>, а не падаем/выдумываем данные); <c>market</c>/<c>recommended</c>
    /// (задача 34) строят кандидатов из ВСЕЙ фикс-купонной вселенной банка <c>bond_universe</c> —
    /// см. <see cref="BuildMarketAllocationResponseAsync"/>. <paramref name="includeWatchlist"/>
    /// осмыслен только для <c>portfolio</c> (для market/recommended игнорируется — вся вселенная и
    /// так шире watchlist).
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
        IBondUniverseRepository universeRepo,
        RelativeValueSnapshotBuilder snapshotBuilder,
        IOptions<Bonds.Infrastructure.Universe.BondUniverseRefreshOptions> universeOptions,
        decimal amountRub,
        string source = "portfolio",
        bool includeWatchlist = false,
        CancellationToken ct = default)
    {
        if (amountRub <= 0m)
        {
            return Results.Json(
                new { error = "amountRub должен быть положительным", type = "ValidationException" },
                statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        if (source is not ("portfolio" or "market" or "recommended"))
        {
            return ValidationError("source должен быть 'portfolio', 'market' или 'recommended'");
        }

        var accountId = await PositionsEndpoints.ResolveAccountIdAsync(principal, accountRepo);

        // Plan/22 часть E: ставка покупки для оценки грязной цены лота — через резолвер (override →
        // оценка из журнала → дефолт), не захардкоженная константа. Задача 34: market/recommended не
        // зависят от портфеля, но тоже платят комиссию за покупку — резолвим ставку, если есть
        // привязанный счёт, иначе дефолт (тот же фолбэк, что раньше был только у "нет аккаунта").
        var resolvedRate = accountId is not null
            ? await commissionRateProvider.GetAsync(accountId.Value, ct)
            : new ResolvedCommissionRate(SwitchAnalysisService.DefaultCommissionRate, CommissionRateSource.Default, null);
        var commissionRate = resolvedRate.Rate;

        if (source is "market" or "recommended")
        {
            return await BuildMarketAllocationResponseAsync(
                amountRub, source, commissionRate, resolvedRate, universeRepo, universeOptions.Value.Hygiene, snapshotBuilder, ct);
        }

        // ─── source=portfolio (прежнее поведение, обратная совместимость дефолта) ──────────────

        if (accountId is null)
        {
            return Results.Ok(new AllocationResponseDto
            {
                AmountRub = amountRub,
                Source = source,
                Allocations = [],
                Skipped = [],
                LeftoverRub = amountRub,
                Disclaimer = CashAllocationService.Disclaimer,
                CommissionRateUsed = resolvedRate.Rate,
                CommissionRateSource = resolvedRate.Source.ToString(),
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
            Source = source,
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
                // Задача 34 часть A.2: source=portfolio может тоже нести риск-сигналы, если бумага
                // нашлась в банке по ISIN — план явно допускает более простую альтернативу (null),
                // если не находится/не резолвится; здесь сознательно null (см. NEEDS DECISION в
                // отчёте задачи — потребует resolve-по-ISIN на КАЖДУЮ строку портфеля, план это не
                // требует буквально).
                RiskSignals = null,
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

    // ─── GET /api/analytics/allocation?source=market|recommended (задача 34) ───────────────────

    /// <summary>Задача 34 часть B.4 — верхняя граница числа кандидатов вселенной банка (после
    /// гигиенического + флоатер + [для recommended] риск-фильтра), реально участвующих в жадном
    /// проходе. Банк ~3400 строк — без потолка ответ разрастался бы; "не молча" — см.
    /// <see cref="AllocationResponseDto.CandidatePoolTruncated"/>/дисклеймер с фактическими числами.</summary>
    public const int MarketAllocationCandidatePoolLimit = 200;

    public const string MarketAllocationDisclaimer =
        "Оценка распределения свободных средств по всей фикс-купонной гигиенически-чистой вселенной " +
        "банка облигаций MOEX (флоатеры исключены — их доходность несравнима с фикс-купоном). " +
        "Кандидаты ранжируются по доходности убыв., без учёта вашего текущего портфеля. Оценки — по " +
        "биржевой статистике, не является индивидуальной инвестиционной рекомендацией.";

    public const string RecommendedAllocationDisclaimer =
        "Оценка распределения свободных средств по кандидатам вселенной банка облигаций MOEX, " +
        "отфильтрованным по позитивным риск-сигналам (ликвидность+листинг, спред к рынку — " +
        "информационные сигналы биржевой статистики, НЕ рейтинг рейтинговых агентств) и " +
        "диверсифицированным по секторам. Оценки — по биржевой статистике, не является " +
        "индивидуальной инвестиционной рекомендацией.";

    /// <summary>
    /// Задача 34 часть B — source=market/recommended: кандидаты из ВСЕЙ гигиенически-чистой
    /// фикс-купонной вселенной банка (<c>bond_universe</c>, через <see cref="UniverseHygieneFilter"/>),
    /// НЕ holdings портфеля — точный движок здесь не вызывается, только банк-статистика (тот же
    /// принцип "двухъярусной архитектуры", что <see cref="GetReplacementCandidates"/> mode=market;
    /// план явно запрещает материализовывать вселенную через движок). <paramref name="source"/>=
    /// <c>recommended</c> дополнительно исключает кандидатов с Caution-сигналом ликвидности ИЛИ
    /// спреда (<see cref="CandidateRiskSignalService"/>, задача 33) и ограничивает концентрацию по
    /// СЕКТОРУ (<see cref="CashAllocationConcentrationAxis.Sector"/> — issuer на банк-слое
    /// недоступен, см. doc-comment <see cref="Core.Analytics.CashAllocationCandidate.Sector"/>);
    /// <paramref name="source"/>=<c>market</c> — честный greedy по доходности БЕЗ секторного лимита
    /// (принятый дефолт NEEDS DECISION plan/34 часть B.3: "весь рынок" должен показывать самое
    /// доходное без искажений диверсификацией). Обе ветки жёстко ограничивают 1 лот на кандидата
    /// (<c>maxLotsPerCandidate=1</c> — диверсификация "не больше 1 позиции на secid", план часть B.3)
    /// и потолок пула кандидатов <see cref="MarketAllocationCandidatePoolLimit"/> — потолок применяется
    /// ПОСЛЕ риск-фильтра (для recommended), чтобы в пул из 200 попадали лучшие ПРОШЕДШИЕ фильтр
    /// кандидаты, а не обрезок топ-200 "по доходности вообще", из которого фильтр потом выбрасывает
    /// половину.
    /// </summary>
    private static async Task<IResult> BuildMarketAllocationResponseAsync(
        decimal amountRub,
        string source,
        decimal commissionRate,
        ResolvedCommissionRate resolvedRate,
        IBondUniverseRepository universeRepo,
        UniverseHygieneOptions hygieneOptions,
        RelativeValueSnapshotBuilder snapshotBuilder,
        CancellationToken ct)
    {
        var asOf = BusinessClock.MoscowToday();
        var all = await universeRepo.GetAllAsync();

        var eligible = all
            .Where(e => UniverseHygieneFilter.Evaluate(e, hygieneOptions, asOf) == HygieneHiddenReason.None)
            .Where(e => e.IsFloater != true)
            .Where(e => e.YieldFraction is not null && e.DurationYears is not null && e.PricePercent is not null)
            // Задача 34 часть B.1: FaceValue не входит в перечень плана буквально, но без него нечем
            // перевести PricePercent в рубли (PricePercent/100 × FaceValue) — тот же принцип "нечем
            // оценить лот", что и для DurationYears/PricePercent.
            .Where(e => e.FaceValue is > 0m)
            .ToList();

        var snapshot = await snapshotBuilder.GetSnapshotAsync(ct);
        // Задача 38 часть A.3: словарь хранит уже готовый RiskSignalsDto (не "сырой" CandidateRiskSignals)
        // — светофор надёжности (ToRiskSignalsDto→Aggregate) нуждается в ListLevel/Sector банк-записи,
        // которые есть здесь (entry), но не были бы доступны в точке потребления ниже (a.Secid).
        var signalsBySecid = new Dictionary<string, RiskSignalsDto>(StringComparer.OrdinalIgnoreCase);

        List<BondUniverseEntry> pool;
        int poolAvailable;

        if (source == "recommended")
        {
            var withSignals = eligible
                .Select(e => (Entry: e, Signals: AssessEntryRiskSignals(e, snapshot)))
                .Where(x => x.Signals.Liquidity != SignalLevel.Caution && x.Signals.Spread != SignalLevel.Caution)
                .ToList();

            foreach (var (entry, signals) in withSignals)
            {
                signalsBySecid[entry.Secid] = ToRiskSignalsDto(signals, entry.ListLevel, entry.Sector);
            }

            poolAvailable = withSignals.Count;
            pool = withSignals
                .OrderByDescending(x => x.Entry.YieldFraction!.Value)
                .Take(MarketAllocationCandidatePoolLimit)
                .Select(x => x.Entry)
                .ToList();
        }
        else
        {
            poolAvailable = eligible.Count;
            pool = eligible
                .OrderByDescending(e => e.YieldFraction!.Value)
                .Take(MarketAllocationCandidatePoolLimit)
                .ToList();

            foreach (var entry in pool)
            {
                signalsBySecid[entry.Secid] = ToRiskSignalsDto(AssessEntryRiskSignals(entry, snapshot), entry.ListLevel, entry.Sector);
            }
        }

        var candidates = pool.Select(e => ToAllocationCandidate(e, commissionRate)).ToList();

        var concentrationAxis = source == "recommended"
            ? CashAllocationConcentrationAxis.Sector
            : CashAllocationConcentrationAxis.None;
        var maxSectorSharePercent = source == "recommended"
            ? (decimal?)CashAllocationService.DefaultMaxSectorSharePercent
            : null;

        var result = CashAllocationService.Allocate(
            amountRub,
            candidates,
            currentPortfolioValueRub: 0m, // задача 34: рыночный скринер не знает состав портфеля — база лимита растёт только за счёт вносимых денег (см. doc-comment CashAllocationConcentrationAxis.Sector).
            concentrationAxis: concentrationAxis,
            maxSectorSharePercent: maxSectorSharePercent,
            maxLotsPerCandidate: 1);

        var truncated = poolAvailable > MarketAllocationCandidatePoolLimit;
        var baseDisclaimer = source == "recommended" ? RecommendedAllocationDisclaimer : MarketAllocationDisclaimer;
        var disclaimer = truncated
            ? $"{baseDisclaimer} Показаны топ-{MarketAllocationCandidatePoolLimit} из {poolAvailable} подходящих бумаг по доходности — не все кандидаты участвовали в распределении."
            : baseDisclaimer;

        var dto = new AllocationResponseDto
        {
            AmountRub = result.AmountRub,
            Source = source,
            Allocations = result.Allocations.Select(a => new AllocationLineDto
            {
                InstrumentId = a.InstrumentId,
                Secid = a.Secid,
                Name = a.Name,
                Issuer = a.Issuer,
                Sector = a.Sector,
                Quantity = a.Quantity,
                EstimatedCostRub = a.EstimatedCostRub,
                EffectiveYield = a.EffectiveYield,
                LotSizeAssumed = a.LotSizeAssumed,
                CleanCostRub = a.CleanCostRub,
                AccruedCostRub = a.AccruedCostRub,
                CommissionCostRub = a.CommissionCostRub,
                RiskSignals = a.Secid is not null && signalsBySecid.TryGetValue(a.Secid, out var lineSignals)
                    ? lineSignals
                    : null,
            }).ToList(),
            Skipped = result.Skipped.Select(s => new AllocationSkipDto
            {
                InstrumentId = s.InstrumentId,
                Secid = s.Secid,
                Name = s.Name,
                Issuer = s.Issuer,
                Reason = s.Reason.ToString(),
            }).ToList(),
            LeftoverRub = result.LeftoverRub,
            Disclaimer = disclaimer,
            CommissionRateUsed = resolvedRate.Rate,
            CommissionRateSource = resolvedRate.Source.ToString(),
            CandidatePoolAvailable = poolAvailable,
            CandidatePoolLimit = MarketAllocationCandidatePoolLimit,
            CandidatePoolTruncated = truncated,
        };

        return Results.Ok(dto);
    }

    /// <summary>
    /// Задача 34 часть B.1: маппинг банк-записи в кандидата аллокации. Цена лота (рубли) =
    /// PricePercent/100 × FaceValue (контракт единиц — см. docs/CODEBASE-GUIDE.md) + комиссия
    /// покупки; банк НЕ хранит НКД отдельной строкой на бумагу (нет поля в <c>BondUniverseEntry</c>)
    /// — приближение "чистая цена без НКД" (AccruedRub=0), задокументировано, тот же уровень
    /// точности, что и остальная банк-статистика (не точный движок). InstrumentId — null (см.
    /// doc-comment <c>CashAllocationCandidate.InstrumentId</c>), идентификатор — Secid. Issuer — null
    /// (банк эмитента не хранит), Sector — из банка (альтернативная ось концентрации).
    /// IsComparable=true безусловно: флоатеры уже исключены фильтром вызывающего кода
    /// (<c>entry.IsFloater != true</c>), других "несравнимых" типов на банк-слое нет (тот же принцип,
    /// что <see cref="GetReplacementCandidates"/> mode=market).
    /// </summary>
    private static CashAllocationCandidate ToAllocationCandidate(BondUniverseEntry entry, decimal commissionRate)
    {
        var cleanPriceRub = entry.PricePercent!.Value / 100m * entry.FaceValue!.Value;
        var commissionRub = cleanPriceRub * commissionRate;
        var pricePerLotRub = cleanPriceRub + commissionRub;

        return new CashAllocationCandidate
        {
            InstrumentId = null,
            Secid = entry.Secid,
            Name = entry.ShortName ?? entry.SecName ?? entry.Secid,
            Issuer = null,
            Sector = entry.Sector,
            EffectiveYield = entry.YieldFraction,
            PricePerLotRub = pricePerLotRub,
            LotSize = 1m,
            LotSizeIsAssumed = true,
            CurrentIssuerMarketValueRub = 0m,
            CleanPriceRub = cleanPriceRub,
            AccruedRub = 0m,
            CommissionRub = commissionRub,
            IsComparable = true,
        };
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
        var basketMembers = ResolveCheapBasketMembers(effectiveBasket, basketMedian, snapshot, positionIsin, TopCheapCandidatesPerRichPosition);

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

    /// <summary>
    /// Задача 33 — общая выборка «дешёвых соседей» одной эффективной корзины (сектор × дюрация
    /// после fallback), вынесена из <see cref="BuildCheapCandidates"/> (задача 30) для повторного
    /// использования <see cref="BuildRvModeCandidates"/> (задача 33 часть B.3) — та же фильтрация
    /// (своя корзина, есть G-спред, self-exclusion по ISIN до Take) и сортировка по deviation убыв.,
    /// только с настраиваемым <paramref name="take"/> вместо константы
    /// <see cref="TopCheapCandidatesPerRichPosition"/>.
    /// </summary>
    private static List<BasketMember> ResolveCheapBasketMembers(
        BasketKey effectiveBasket,
        decimal basketMedian,
        RelativeValueSnapshotBuilder.RelativeValueSnapshot snapshot,
        string? excludeIsin,
        int take)
    {
        return snapshot.AllMembers
            .Where(m => string.Equals(m.Sector ?? UnknownSector, effectiveBasket.Sector, StringComparison.OrdinalIgnoreCase))
            .Where(m => effectiveBasket.DurationBucket is SectorWideBucketLabel or MarketWideLabel
                || Bonds.Core.Analytics.DurationBucketClassifier.Label(m.DurationYears) == effectiveBasket.DurationBucket)
            .Where(m => m.GSpreadFraction is not null)
            // Self-exclusion (ревью T-30, MAJOR): банк — вся вселенная MOEX, оцениваемая позиция
            // почти наверняка присутствует в нём под своим secid, а её approx-спред из банка
            // отличается от точного спреда движка — без исключения позиция могла бы порекомендовать
            // САМУ СЕБЯ как «дешёвого соседа» («купи то же самое дешевле»). Исключаем по ISIN ДО
            // Take, чтобы самоссылка не съедала слот кандидата.
            .Where(m => excludeIsin is null
                || !snapshot.CurrentEntriesBySecid.TryGetValue(m.Secid, out var entryForIsin)
                || !string.Equals(entryForIsin.Isin, excludeIsin, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(m => m.GSpreadFraction!.Value - basketMedian)
            .Take(take)
            .ToList();
    }

    // ─── GET /api/analytics/replacement-candidates ──────────────────────────────────────────

    /// <summary>Дефолтный размер выдачи, если <c>limit</c> не передан (план часть B.1: "дефолт напр. 20").</summary>
    public const int DefaultReplacementCandidatesLimit = 20;

    /// <summary>Верхняя граница <c>limit</c> — защита от случайного запроса всей вселенной (~3400 бумаг) одним ответом.</summary>
    public const int MaxReplacementCandidatesLimit = 100;

    public const string ReplacementCandidatesDisclaimer =
        "Кандидаты и оценки — аналитическая информация, не инвестиционная рекомендация. Точная " +
        "выгода считается для выбранной бумаги через сравнение (POST /api/analytics/replacement). " +
        "Риск-сигналы (ликвидность/листинг, спред к рынку) — по биржевой статистике MOEX, не рейтинг " +
        "рейтинговых агентств.";

    /// <summary>Задача 38 часть B — валидирует значение query-параметра <c>reliability</c> (общий для
    /// GET /api/analytics/replacement-candidates и GET /api/universe). Null/пусто — валиден (без
    /// фильтра). <c>internal</c> — переиспользуется <c>UniverseEndpoints</c> (та же сборка).</summary>
    internal static bool IsValidReliabilityFilterValue(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.Trim().ToLowerInvariant() is "green" or "yellow" or "red";

    /// <summary>
    /// Задача 38 часть B — «не хуже уровня»: green = только Green; yellow = Green+Yellow; red = все
    /// уровни (Red — худший, значит "не хуже красного" ничего не отсекает — то же самое, что
    /// отсутствие фильтра); null/пусто — без фильтра (все уровни). <paramref name="levelString"/> —
    /// сериализованное значение <see cref="Bonds.Core.Analytics.ReliabilityLight"/>
    /// (<c>RiskSignalsDto.Reliability</c>/<c>UniverseRowDto.Reliability</c>), сравнивается строкой,
    /// чтобы не тянуть повторный парсинг enum на каждый вызов.
    /// </summary>
    internal static bool ReliabilityMeetsFilter(string levelString, string? filterValue)
    {
        if (string.IsNullOrWhiteSpace(filterValue)) return true;

        return filterValue.Trim().ToLowerInvariant() switch
        {
            "green" => levelString == nameof(ReliabilityLight.Green),
            "yellow" => levelString is nameof(ReliabilityLight.Green) or nameof(ReliabilityLight.Yellow),
            _ => true, // "red" (или уже отфильтрованное валидатором прочее) — все уровни проходят.
        };
    }

    /// <summary>
    /// Задача 33 часть B — единый источник кандидатов-замен для ОДНОЙ позиции портфеля (блок 1
    /// переработки «Рекомендаций»): <c>mode=market</c> — вся фикс-купонная гигиенически-чистая
    /// вселенная банка (<see cref="UniverseHygieneFilter"/>, БЕЗ флоатеров), отсортированная по
    /// доходности убыв.; <c>mode=rv</c> — дешёвые соседи по корзине сектор×дюрация ПОЗИЦИИ
    /// (переиспользует <see cref="RelativeValueService"/>/<see cref="ResolveCheapBasketMembers"/> —
    /// та же инфраструктура, что <see cref="GetRelativeValue"/>). Оба режима отдают дешёвую
    /// банк-статистику + информационные риск-сигналы (<see cref="CandidateRiskSignalService"/>) —
    /// точную выгоду конкретного выбранного кандидата считает существующий POST
    /// /api/analytics/replacement (двухъярусная архитектура, см. doc-comment plan/26); эта ручка
    /// НЕ материализует вселенную через точный движок.
    /// </summary>
    private static async Task<IResult> GetReplacementCandidates(
        ClaimsPrincipal principal,
        IAccountRepository accountRepo,
        PortfolioHoldingsBuilder holdingsBuilder,
        IBondUniverseRepository universeRepo,
        RelativeValueSnapshotBuilder snapshotBuilder,
        IOptions<Bonds.Infrastructure.Universe.BondUniverseRefreshOptions> universeOptions,
        ulong positionId,
        string mode,
        int? limit,
        string? reliability,
        CancellationToken ct)
    {
        if (mode != "market" && mode != "rv")
        {
            return ValidationError("mode должен быть 'market' или 'rv'");
        }

        if (!IsValidReliabilityFilterValue(reliability))
        {
            return ValidationError("reliability должен быть 'green', 'yellow' или 'red'");
        }

        var accountId = await PositionsEndpoints.ResolveAccountIdAsync(principal, accountRepo);
        if (accountId is null) throw new NotFoundException($"Позиция {positionId} не найдена в портфеле");

        var asOf = BusinessClock.MoscowToday();
        var holdings = await holdingsBuilder.BuildForAccountAsync(accountId.Value, asOf);
        var position = holdings.FirstOrDefault(h => h.PositionId == positionId);
        if (position is null) throw new NotFoundException($"Позиция {positionId} не найдена в портфеле");

        var effectiveLimit = Math.Clamp(limit ?? DefaultReplacementCandidatesLimit, 1, MaxReplacementCandidatesLimit);
        var snapshot = await snapshotBuilder.GetSnapshotAsync(ct);

        var candidates = mode == "market"
            ? await BuildMarketCandidates(position, universeRepo, universeOptions.Value.Hygiene, asOf, snapshot, effectiveLimit, reliability)
            : BuildRvModeCandidates(position, snapshot, effectiveLimit, reliability);

        return Results.Ok(new ReplacementCandidatesResponseDto
        {
            Mode = mode,
            // Позиция портфеля всегда привязана к инструменту с ISIN (см. doc-comment Instrument) —
            // пустая строка здесь не должна происходить в норме, но не бросаем 500 из-за пробела в данных.
            PositionIsin = position.Isin ?? string.Empty,
            Candidates = candidates,
            Disclaimer = ReplacementCandidatesDisclaimer,
        });
    }

    /// <summary>
    /// mode=market (план часть B.2): гигиенически-чистая (<see cref="UniverseHygieneFilter"/>)
    /// фикс-купонная (<c>IsFloater != true</c>) вселенная банка, БЕЗ записей с
    /// <c>YieldFraction == null</c> (иначе нечем ранжировать), БЕЗ самой позиции (по ISIN),
    /// топ-<paramref name="limit"/> по <see cref="BondUniverseEntry.YieldFraction"/> убыв. Точную
    /// выгоду каждого кандидата эта ручка НЕ считает — та задача существующего POST
    /// /api/analytics/replacement (см. doc-comment <see cref="GetReplacementCandidates"/>).
    /// <para>
    /// Задача 38 часть B.1: <paramref name="reliabilityFilter"/> ("не хуже уровня") применяется ДО
    /// <c>Take(limit)</c>, иначе топ-N по доходности мог бы целиком не пройти фильтр и вернуть
    /// меньше <paramref name="limit"/> кандидатов, хотя более доходные-но-дальше кандидаты бы
    /// прошли. Без фильтра (null) поведение НЕ меняется — сигналы считаются только для уже
    /// усечённого топ-N (дёшево); с фильтром — лениво для всей <c>eligible</c> (LINQ
    /// Select→Where→Take не материализует лишнего — вычисление останавливается, как только
    /// набрано <paramref name="limit"/> подходящих), тот же приём масштаба, что source=recommended
    /// аллокации (задача 34).
    /// </para>
    /// </summary>
    private static async Task<List<ReplacementCandidateDto>> BuildMarketCandidates(
        Core.Analytics.PortfolioHolding position,
        IBondUniverseRepository universeRepo,
        UniverseHygieneOptions hygieneOptions,
        DateOnly asOf,
        RelativeValueSnapshotBuilder.RelativeValueSnapshot snapshot,
        int limit,
        string? reliabilityFilter)
    {
        var all = await universeRepo.GetAllAsync();

        var eligible = all
            .Where(e => UniverseHygieneFilter.Evaluate(e, hygieneOptions, asOf) == HygieneHiddenReason.None)
            .Where(e => e.IsFloater != true)
            .Where(e => e.YieldFraction is not null)
            .Where(e => position.Isin is null || !string.Equals(e.Isin, position.Isin, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.YieldFraction!.Value);

        if (string.IsNullOrWhiteSpace(reliabilityFilter))
        {
            return eligible.Take(limit).Select(e => ToReplacementCandidateDto(e, snapshot)).ToList();
        }

        return eligible
            .Select(e => ToReplacementCandidateDto(e, snapshot))
            .Where(dto => ReliabilityMeetsFilter(dto.RiskSignals.Reliability, reliabilityFilter))
            .Take(limit)
            .ToList();
    }

    /// <summary>
    /// mode=rv (план часть B.3): дешёвые соседи ИЗ КОРЗИНЫ ПОЗИЦИИ (сектор × дюрация, тот же путь,
    /// что <see cref="GetRelativeValue"/>/<see cref="BuildCheapCandidates"/>), топ-<paramref
    /// name="limit"/> по deviation убыв. Если у позиции нет валидной корзины/спреда (не comparable —
    /// floater/indexed, или G-спред/дюрация не посчитались движком) — пустой список, НЕ 500 (план
    /// явно требует понятный признак вместо ошибки; пустой список сам по себе — понятный признак
    /// "нет данных для RV-режима у этой позиции").
    /// <para>
    /// Задача 38 часть B.1: <paramref name="reliabilityFilter"/> — тот же принцип "фильтр ДО
    /// усечения", что <see cref="BuildMarketCandidates"/>: с активным фильтром корзина резолвится
    /// БЕЗ внутреннего лимита (<c>ResolveCheapBasketMembers</c> получает размер всей вселенной как
    /// верхнюю границу — корзина сектор×дюрация обычно << всей вселенной, реального урезания не
    /// происходит), фильтруется, и только потом урезается до <paramref name="limit"/>.
    /// </para>
    /// </summary>
    private static List<ReplacementCandidateDto> BuildRvModeCandidates(
        Core.Analytics.PortfolioHolding position,
        RelativeValueSnapshotBuilder.RelativeValueSnapshot snapshot,
        int limit,
        string? reliabilityFilter)
    {
        if (position.GSpread is not { } bondSpread || position.ModifiedDuration is null)
        {
            return [];
        }

        var assessment = Assess(position.Sector, position.ModifiedDuration, bondSpread, snapshot.AllMembers, snapshot.BasketStats);
        var effectiveBasket = assessment.Basket.EffectiveBasket;
        var basketMedian = assessment.Basket.Stats.Median;

        var take = string.IsNullOrWhiteSpace(reliabilityFilter) ? limit : snapshot.AllMembers.Count;
        var basketMembers = ResolveCheapBasketMembers(effectiveBasket, basketMedian, snapshot, position.Isin, take);

        var dtos = basketMembers.Select(m => ToReplacementCandidateDto(m, basketMedian, snapshot));
        if (!string.IsNullOrWhiteSpace(reliabilityFilter))
        {
            dtos = dtos.Where(dto => ReliabilityMeetsFilter(dto.RiskSignals.Reliability, reliabilityFilter));
        }

        return dtos.Take(limit).ToList();
    }

    /// <summary>
    /// Задача 34 переиспользует эту резолюцию (source=recommended, риск-фильтр Caution +
    /// RiskSignalsDto на строках аллокации) — вынесено из <see cref="ToReplacementCandidateDto(BondUniverseEntry, RelativeValueSnapshotBuilder.RelativeValueSnapshot)"/>,
    /// чтобы не дублировать резолюцию корзины/ликвидности. Медиана корзины — СВОЯ у каждой записи
    /// (сектор × дюрация ЗАПИСИ, не позиции/другого кандидата), резолвится через
    /// <see cref="RelativeValueService.ResolveBasket"/> той же fallback-цепочкой, что RV-вердикт позиций.
    /// Задача 38: <c>internal</c> (не <c>private</c>) — переиспользуется <c>UniverseEndpoints.GetUniverse</c>
    /// (тот же сборка Bonds.Api) для светофора надёжности каждой строки скринера.
    /// </summary>
    internal static CandidateRiskSignals AssessEntryRiskSignals(
        BondUniverseEntry entry, RelativeValueSnapshotBuilder.RelativeValueSnapshot snapshot)
    {
        var liquidity = LiquidityScoreCalculator.Assess(entry.TurnoverRub, entry.BidPercent, entry.OfferPercent, entry.NumTrades);

        decimal? basketMedian = null;
        if (entry.GspreadApproxFraction is not null)
        {
            var key = new BasketKey
            {
                Sector = string.IsNullOrWhiteSpace(entry.Sector) ? UnknownSector : entry.Sector!,
                DurationBucket = Bonds.Core.Analytics.DurationBucketClassifier.Label(entry.DurationYears),
            };
            basketMedian = ResolveBasket(key, snapshot.AllMembers, snapshot.BasketStats).Stats.Median;
        }

        return CandidateRiskSignalService.Assess(liquidity.Score, entry.ListLevel, entry.GspreadApproxFraction, basketMedian);
    }

    /// <summary>mode=market: обогащает банк-запись кандидата риск-сигналами.</summary>
    private static ReplacementCandidateDto ToReplacementCandidateDto(
        BondUniverseEntry entry, RelativeValueSnapshotBuilder.RelativeValueSnapshot snapshot)
    {
        var signals = AssessEntryRiskSignals(entry, snapshot);

        return new ReplacementCandidateDto
        {
            Secid = entry.Secid,
            Isin = entry.Isin,
            Name = entry.ShortName ?? entry.SecName ?? entry.Secid,
            // Задача 33 часть B.2: банк-слой (bond_universe) не хранит эмитента (issuer-поле —
            // задача 34, НЕ заводить здесь) — null осознанно, не "не удалось определить".
            Issuer = null,
            Sector = entry.Sector,
            YieldFraction = entry.YieldFraction,
            DurationYears = entry.DurationYears,
            GSpreadFraction = entry.GspreadApproxFraction,
            OfferDate = entry.OfferDate,
            RiskSignals = ToRiskSignalsDto(signals, entry.ListLevel, entry.Sector),
        };
    }

    /// <summary>mode=rv: обогащает члена корзины именем/доходностью из текущего <c>bond_universe</c>
    /// (тот же путь, что <see cref="BuildCheapCandidates"/>) + риск-сигналами относительно ОБЩЕЙ
    /// медианы корзины позиции (все RV-кандидаты — из ОДНОЙ эффективной корзины, поэтому медиана
    /// одна на всех, в отличие от mode=market).</summary>
    private static ReplacementCandidateDto ToReplacementCandidateDto(
        BasketMember member, decimal basketMedian, RelativeValueSnapshotBuilder.RelativeValueSnapshot snapshot)
    {
        snapshot.CurrentEntriesBySecid.TryGetValue(member.Secid, out var entry);
        var liquidity = LiquidityScoreCalculator.Assess(entry?.TurnoverRub, entry?.BidPercent, entry?.OfferPercent, entry?.NumTrades);
        var signals = CandidateRiskSignalService.Assess(liquidity.Score, entry?.ListLevel, member.GSpreadFraction, basketMedian);

        return new ReplacementCandidateDto
        {
            Secid = member.Secid,
            Isin = entry?.Isin,
            Name = entry?.ShortName ?? entry?.SecName ?? member.Secid,
            Issuer = null,
            Sector = member.Sector,
            YieldFraction = entry?.YieldFraction,
            DurationYears = member.DurationYears,
            GSpreadFraction = member.GSpreadFraction,
            OfferDate = entry?.OfferDate,
            // Задача 38: светофор по листингу/сектору ЗАПИСИ банка (не сектору члена корзины —
            // member.Sector может быть недоступен для гособлигаций-фоллбэка UnknownSector, entry —
            // тот же банк-снимок, что несёт ListLevel; entry может быть null, если secid не резолвится
            // в текущем снимке банка — тогда листинг/сектор неизвестны, Aggregate трактует как Yellow).
            RiskSignals = ToRiskSignalsDto(signals, entry?.ListLevel, entry?.Sector ?? member.Sector),
        };
    }

    /// <summary>Задача 38 часть A.2: собирает RiskSignalsDto из двух информационных сигналов +
    /// агрегированного светофора надёжности (<see cref="CandidateRiskSignalService.Aggregate"/>).
    /// <paramref name="listLevel"/>/<paramref name="sector"/> — те же входы, что уже использованы
    /// для расчёта <paramref name="signals"/> (листинг банк-записи, сектор банк-записи).</summary>
    private static RiskSignalsDto ToRiskSignalsDto(CandidateRiskSignals signals, int? listLevel, string? sector)
    {
        var (reliability, reliabilityReason) = CandidateRiskSignalService.Aggregate(signals, listLevel, sector);

        return new RiskSignalsDto
        {
            Liquidity = signals.Liquidity.ToString(),
            LiquidityLabel = signals.LiquidityLabel,
            Spread = signals.Spread.ToString(),
            GSpreadFraction = signals.GSpreadFraction,
            SpreadVsBasketMedianFraction = signals.SpreadVsBasketMedianFraction,
            Reliability = reliability.ToString(),
            ReliabilityReason = reliabilityReason,
        };
    }

    /// <summary>
    /// Задача 33 часть B.4: риск-сигналы бумаги по её банк-записи, найденной по ISIN среди ТЕКУЩЕГО
    /// снимка bond_universe (<see cref="RelativeValueSnapshotBuilder.RelativeValueSnapshot.CurrentEntriesBySecid"/>
    /// индексирован по secid, не по ISIN — банк маленький (~3400 строк), линейный поиск по значению
    /// один раз за запрос не оправдывает отдельный индекс/репозиторный метод ради единственного
    /// потребителя). Null, если ISIN не найден в банке (бумага вне биржевой статистики MOEX).
    /// </summary>
    private static RiskSignalsDto? BuildRiskSignalsForIsin(string isin, RelativeValueSnapshotBuilder.RelativeValueSnapshot snapshot)
    {
        var entry = snapshot.CurrentEntriesBySecid.Values
            .FirstOrDefault(e => string.Equals(e.Isin, isin, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return null;

        return ToReplacementCandidateDto(entry, snapshot).RiskSignals;
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

/// <summary>
/// Задача 33 часть B — ответ GET /api/analytics/replacement-candidates. Схема — контракт для
/// фронтовой задачи 35 (зеркалит буквально), не менять без согласования.
/// </summary>
public sealed record ReplacementCandidatesResponseDto
{
    /// <summary>"market" | "rv" — эхо запрошенного режима.</summary>
    public required string Mode { get; init; }

    public required string PositionIsin { get; init; }
    public required IReadOnlyList<ReplacementCandidateDto> Candidates { get; init; }
    public required string Disclaimer { get; init; }
}

/// <summary>Один кандидат-замена — дешёвая банк-статистика (bond_universe), НЕ точный расчёт
/// движка (см. doc-comment <see cref="AnalyticsEndpoints.GetReplacementCandidates"/>).</summary>
public sealed record ReplacementCandidateDto
{
    public required string Secid { get; init; }
    public string? Isin { get; init; }
    public required string Name { get; init; }

    /// <summary>Задача 33 часть B.2: банк-слой не хранит эмитента — всегда null (issuer-поле — задача 34).</summary>
    public string? Issuer { get; init; }

    public string? Sector { get; init; }

    /// <summary>ДОЛЯ (см. общая конвенция единиц репо).</summary>
    public decimal? YieldFraction { get; init; }

    /// <summary>Годы.</summary>
    public decimal? DurationYears { get; init; }

    /// <summary>ДОЛЯ; приближённый G-спред банка (<c>BondUniverseEntry.GspreadApproxFraction</c>).</summary>
    public decimal? GSpreadFraction { get; init; }

    /// <summary>Задача 37 часть A: дата оферты банк-записи (<see cref="BondUniverseEntry.OfferDate"/>),
    /// если она есть — доходность бумаги с офертой считается движком К ОФЕРТЕ, не к погашению,
    /// горизонт другой (см. plan/37). Null — оферты нет/не известна.</summary>
    public DateOnly? OfferDate { get; init; }

    public required RiskSignalsDto RiskSignals { get; init; }
}

/// <summary>
/// Задача 33 часть A — два ИНФОРМАЦИОННЫХ риск-сигнала кандидата (не рейтинг кредитного качества,
/// не «надёжность» — см. doc-comment <see cref="Bonds.Core.Analytics.SignalLevel"/>): ликвидность+
/// листинг и отклонение спреда от медианы корзины. Уровни не ранжируют кандидатов — ранжирование
/// mode=market идёт по доходности.
/// </summary>
public sealed record RiskSignalsDto
{
    /// <summary>"Good"|"Neutral"|"Caution" — см. <see cref="Bonds.Core.Analytics.SignalLevel"/>.</summary>
    public required string Liquidity { get; init; }

    /// <summary>Человекочитаемая подпись, напр. "Высокая ликвидность, листинг 1".</summary>
    public required string LiquidityLabel { get; init; }

    /// <summary>"Good"|"Neutral"|"Caution".</summary>
    public required string Spread { get; init; }

    /// <summary>ДОЛЯ; эхо G-спреда кандидата. Null, если MOEX не отдал спред.</summary>
    public decimal? GSpreadFraction { get; init; }

    /// <summary>ДОЛЯ; GSpreadFraction − медиана корзины кандидата. Знак: положительное — спред
    /// выше медианы (рынок закладывает бОльшую премию/риск). Null вместе с GSpreadFraction.</summary>
    public decimal? SpreadVsBasketMedianFraction { get; init; }

    /// <summary>
    /// Задача 38 часть A.2 — светофор надёжности: "Green"|"Yellow"|"Red" (см.
    /// <see cref="Bonds.Core.Analytics.ReliabilityLight"/>/<see cref="Bonds.Core.Analytics.CandidateRiskSignalService.Aggregate"/>
    /// для точной матрицы). Аддитивное поле — существующие Liquidity/Spread не меняются.
    /// <b>НЕ кредитный рейтинг</b> — сигнал по биржевой статистике, формулировка "рейтинг"
    /// запрещена планом задачи 38 везде в UI/API.
    /// </summary>
    public required string Reliability { get; init; }

    /// <summary>Человекочитаемое обоснование светофора (что притянуло уровень вниз) — задача 38
    /// часть A.1. Дисклеймер "не кредитный рейтинг" сюда НЕ входит — он отдельный элемент UI
    /// (тултип фронта, задача 38 часть C), не часть этой строки.</summary>
    public required string ReliabilityReason { get; init; }
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

    /// <summary>
    /// Задача 33 часть B.4: информационные риск-сигналы (ликвидность+листинг, спред) для ЦЕЛИ
    /// сравнения — по её банк-записи (<c>bond_universe</c>, найдена по ISIN). Null, если целевая
    /// бумага не найдена в банке (не покрыта биржевой статистикой MOEX) — НЕ ошибка, фронт просто
    /// не показывает сигналы. См. doc-comment <see cref="RiskSignalsDto"/> — НЕ рейтинг агентств.
    /// </summary>
    public RiskSignalsDto? TargetRiskSignals { get; init; }
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

    /// <summary>Задача 34: эхо запрошенного источника кандидатов — "portfolio"|"market"|"recommended".</summary>
    public required string Source { get; init; }

    public required IReadOnlyList<AllocationLineDto> Allocations { get; init; }
    public required IReadOnlyList<AllocationSkipDto> Skipped { get; init; }
    public required decimal LeftoverRub { get; init; }
    public required string Disclaimer { get; init; }

    /// <summary>Plan/22 часть E: ставка комиссии покупки, применённая к цене лота — ДОЛЯ (резолвер части C).</summary>
    public required decimal CommissionRateUsed { get; init; }

    /// <summary>Plan/22 часть E: источник ставки — строка <see cref="CommissionRateSource"/>.</summary>
    public required string CommissionRateSource { get; init; }

    /// <summary>Задача 34 часть B.4: только для Source=market/recommended — сколько кандидатов
    /// реально ПОДХОДИЛО (после гигиенического + [для recommended] риск-фильтра) ДО отсечения
    /// потолком <see cref="AnalyticsEndpoints.MarketAllocationCandidatePoolLimit"/>. Null для
    /// Source=portfolio.</summary>
    public int? CandidatePoolAvailable { get; init; }

    /// <summary>Задача 34 часть B.4: только для Source=market/recommended — верхняя граница числа
    /// кандидатов, реально участвовавших в жадном проходе
    /// (<see cref="AnalyticsEndpoints.MarketAllocationCandidatePoolLimit"/>). Null для Source=portfolio.</summary>
    public int? CandidatePoolLimit { get; init; }

    /// <summary>Задача 34 часть B.4: true — <see cref="CandidatePoolAvailable"/> &gt;
    /// <see cref="CandidatePoolLimit"/>, потолок реально обрезал часть подходящих кандидатов ДО
    /// жадного прохода (план требует "не молча" — см. также текст <see cref="Disclaimer"/>, который
    /// в этом случае дописывает фактические числа). Null для Source=portfolio.</summary>
    public bool? CandidatePoolTruncated { get; init; }
}

public sealed record AllocationLineDto
{
    /// <summary>Null для Source=market/recommended (задача 34) — банк-кандидат не связан с таблицей
    /// Instrument, см. doc-comment <c>CashAllocationCandidate.InstrumentId</c>. Идентификатор для
    /// таких строк — <see cref="Secid"/>.</summary>
    public required ulong? InstrumentId { get; init; }

    /// <summary>Задача 34: биржевой SECID — только для Source=market/recommended. Null для Source=portfolio.</summary>
    public string? Secid { get; init; }

    public string? Name { get; init; }
    public string? Issuer { get; init; }

    /// <summary>Задача 34: грубая секторная классификация банка ("Гособлигации"/"Муниципальные"/
    /// "Корпоративные") — заполнена для Source=market/recommended, null для Source=portfolio.</summary>
    public string? Sector { get; init; }

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

    /// <summary>Задача 34 часть A.2: риск-сигналы (см. <see cref="RiskSignalsDto"/>, задача 33) —
    /// заполнены для Source=market/recommended, null для Source=portfolio (план допускает эту
    /// более простую альтернативу вместо resolve-по-ISIN на каждую строку портфеля).</summary>
    public RiskSignalsDto? RiskSignals { get; init; }
}

public sealed record AllocationSkipDto
{
    /// <summary>Null для Source=market/recommended — см. doc-comment <see cref="AllocationLineDto.InstrumentId"/>.</summary>
    public required ulong? InstrumentId { get; init; }

    /// <summary>Задача 34: см. <see cref="AllocationLineDto.Secid"/>.</summary>
    public string? Secid { get; init; }

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

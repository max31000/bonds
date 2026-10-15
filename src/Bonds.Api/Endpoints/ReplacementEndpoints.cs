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

/// <summary>Сравнение позиций, анализ замены, relative-value, кандидаты на замену.</summary>
public static class ReplacementEndpoints
{
    public static void MapReplacementEndpoints(this WebApplication app)
    {
        app.MapGet("/api/analytics/comparison", GetComparison);
        app.MapPost("/api/analytics/replacement", PostReplacement);
        app.MapGet("/api/analytics/relative-value", GetRelativeValue);
        app.MapGet("/api/analytics/replacement-candidates", GetReplacementCandidates);
    }

    // ─── GET /api/analytics/comparison ──────────────────────────────────────────────────────

    private static async Task<IResult> GetComparison(
        ICurrentUser currentUser,
        IAccountRepository accountRepo,
        PortfolioHoldingsBuilder holdingsBuilder)
    {
        var accountId = await PositionsEndpoints.ResolveAccountIdAsync(currentUser, accountRepo);
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
    /// «Держать A vs переложиться в B». Target по умолчанию — позиция портфеля
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
        ICurrentUser currentUser,
        IAccountRepository accountRepo,
        PortfolioHoldingsBuilder holdingsBuilder,
        ICommissionRateProvider commissionRateProvider,
        RelativeValueSnapshotBuilder snapshotBuilder,
        CancellationToken ct)
    {
        var accountId = await PositionsEndpoints.ResolveAccountIdAsync(currentUser, accountRepo);
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

        // Цель-флоатер/индексируемая несравнима по доходности с фикс-купоном —
        // её "спред" = CurrentYield(флоатер) − YtmEffective(фикс), бессмысленная величина. Фронт
        // заранее исключит такие бумаги из выпадашки target'ов, это — защита контракта
        // на случай прямого вызова API. DataIncomplete НЕ блокирует здесь (в отличие от полного
        // YieldComparability.IsComparable) — та ветка уже штатно обслуживается через
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

        // Явные ставки в запросе по-прежнему побеждают резолвер (контракт не
        // ломается) — резолвер вызывается, только если хотя бы одна из ставок не передана явно.
        ResolvedCommissionRate? resolved = null;
        if (request.SellCommissionRate is null || request.BuyCommissionRate is null)
        {
            resolved = await commissionRateProvider.GetAsync(accountId.Value, ct);
        }

        var sellRate = request.SellCommissionRate ?? resolved!.Rate;
        var buyRate = request.BuyCommissionRate ?? resolved!.Rate;

        var result = SwitchAnalysisService.Compare(holdCandidate, targetCandidate, request.HorizonYears, sellRate, buyRate);

        // Построчная формула-разбивка для ОДНОЙ явно выбранной пары hold/target:
        // спред → капитал → горизонт → валовая выгода → минус комиссии → чистая → минус налог.
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

        // Риск-сигналы для ВЫБРАННОЙ цели (не hold — план ограничивает объём
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
            // Ставка(и) и источник, который реально применился (явный запрос
            // выигрывает — тогда Source отражается как "явно передана запросом", см. doc-comment DTO).
            SellCommissionRateUsed = sellRate,
            BuyCommissionRateUsed = buyRate,
            CommissionRateSource = resolved?.Source.ToString() ?? "ExplicitRequest",
            // Формула-разбивка (см. doc-comment выше) — null, если доходность
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

    // ─── GET /api/analytics/relative-value ─────────────────────────────────────────────────────

    /// <summary>Сколько дешёвых кандидатов из корзины показывать для каждой Rich-позиции (план часть C.2).</summary>
    public const int TopCheapCandidatesPerRichPosition = 3;

    public const string RelativeValueDisclaimer =
        "Относительная дешевизна/дороговизна считается против медианного G-спреда бумаг ТОЙ ЖЕ " +
        "корзины (сектор × срок) — это НЕ оценка кредитного качества эмитента: большой спред может " +
        "означать реальный риск дефолта, а не недооценку рынком. Аналитическая оценка, не " +
        "инвестиционная рекомендация.";

    /// <summary>
    /// Для каждой сравнимой позиции портфеля (не floater/indexed/dataIncomplete —
    /// тот же предикат, что <see cref="YieldComparability.IsComparable"/>) считает отклонение
    /// точного G-спреда от сглаженной медианы её корзины (сектор × дюрационный бакет,
    /// <see cref="RelativeValueSnapshotBuilder"/>), верdict Cheap/Fair/Rich и для Rich-позиций —
    /// топ-3 дешёвых кандидатов ИЗ ИХ ЖЕ корзины (не скрытых бумаг банка облигаций).
    /// </summary>
    private static async Task<IResult> GetRelativeValue(
        ICurrentUser currentUser,
        IAccountRepository accountRepo,
        PortfolioHoldingsBuilder holdingsBuilder,
        RelativeValueSnapshotBuilder snapshotBuilder,
        CancellationToken ct)
    {
        var accountId = await PositionsEndpoints.ResolveAccountIdAsync(currentUser, accountRepo);
        if (accountId is null)
        {
            return Results.Ok(new RelativeValueResponseDto { Positions = [], Disclaimer = RelativeValueDisclaimer });
        }

        var asOf = BusinessClock.MoscowToday();
        var holdings = await holdingsBuilder.BuildForAccountAsync(accountId.Value, asOf);
        var comparableHoldings = holdings
            .Where(h => YieldComparability.IsComparable(h.IsFloater, h.IsIndexed, h.DataIncomplete))
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

    /// <summary>Cheap/Fair/Rich по знаку и порогу <see cref="SpreadThresholds.DeviationFraction"/> (план часть C.1):
    /// |deviation| &lt; порог → Fair (в рынке); иначе положительное — Cheap (спред выше медианы,
    /// рынок недооценивает/закладывает риск), отрицательное — Rich (спред ниже медианы, дорого
    /// относительно соседей по корзине, кандидат на пересмотр).</summary>
    private static RelativeValueVerdict ResolveVerdict(decimal deviationFraction)
    {
        if (Math.Abs(deviationFraction) < SpreadThresholds.DeviationFraction) return RelativeValueVerdict.Fair;
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
    /// Флоатеры сюда попасть не могут — <c>snapshot.AllMembers</c> уже не
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
    /// Общая выборка «дешёвых соседей» одной эффективной корзины (сектор × дюрация
    /// после fallback), вынесена из <see cref="BuildCheapCandidates"/> для повторного
    /// использования <see cref="BuildRvModeCandidates"/> — та же фильтрация
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

    /// <summary>
    /// Единый источник кандидатов-замен для ОДНОЙ позиции портфеля (блок 1
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
        ICurrentUser currentUser,
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

        var accountId = await PositionsEndpoints.ResolveAccountIdAsync(currentUser, accountRepo);
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
    /// <paramref name="reliabilityFilter"/> ("не хуже уровня") применяется ДО
    /// <c>Take(limit)</c>, иначе топ-N по доходности мог бы целиком не пройти фильтр и вернуть
    /// меньше <paramref name="limit"/> кандидатов, хотя более доходные-но-дальше кандидаты бы
    /// прошли. Без фильтра (null) поведение НЕ меняется — сигналы считаются только для уже
    /// усечённого топ-N (дёшево); с фильтром — лениво для всей <c>eligible</c> (LINQ
    /// Select→Where→Take не материализует лишнего — вычисление останавливается, как только
    /// набрано <paramref name="limit"/> подходящих), тот же приём масштаба, что source=recommended
    /// аллокации.
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
    /// <paramref name="reliabilityFilter"/> — тот же принцип "фильтр ДО
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
            // Банк-слой (bond_universe) не хранит эмитента (issuer-поле —
            // задача 34, НЕ заводить здесь) — null осознанно, не "не удалось определить".
            Issuer = null,
            Sector = entry.Sector,
            YieldFraction = entry.YieldFraction,
            DurationYears = entry.DurationYears,
            GSpreadFraction = entry.EffectiveGspreadFraction,
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
            // Светофор по листингу/сектору ЗАПИСИ банка (не сектору члена корзины —
            // member.Sector может быть недоступен для гособлигаций-фоллбэка UnknownSector, entry —
            // тот же банк-снимок, что несёт ListLevel; entry может быть null, если secid не резолвится
            // в текущем снимке банка — тогда листинг/сектор неизвестны, Aggregate трактует как Yellow).
            RiskSignals = ToRiskSignalsDto(signals, entry?.ListLevel, entry?.Sector ?? member.Sector),
        };
    }

    /// <summary>
    /// Риск-сигналы бумаги по её банк-записи, найденной по ISIN среди ТЕКУЩЕГО
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
}

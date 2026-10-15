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
                MacaulayDurationYears = r.MacaulayDurationYears,
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
        ReliabilityScoreProvider scoreProvider,
        IUserSettingsRepository settingsRepo,
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

        // Горизонт: явное значение запроса побеждает, иначе — настройка пользователя (user_settings.replacement_horizon_years,
        // дефолт 2.0), задача 48. Единица — ГОДЫ.
        decimal horizonYears;
        string horizonSource;
        if (request.HorizonYears is { } requestedHorizon)
        {
            if (!SettingsEndpoints.IsValidReplacementHorizonRequest(requestedHorizon))
            {
                return ValidationError(
                    $"HorizonYears должен быть больше 0 и не больше {SettingsEndpoints.MaxReplacementHorizonYears:0.#} лет");
            }

            horizonYears = requestedHorizon;
            horizonSource = "Request";
        }
        else
        {
            var userId = await currentUser.GetUserIdAsync();
            var settings = userId is { } uid ? await settingsRepo.GetByUserIdAsync(uid) : null;
            horizonYears = settings?.ReplacementHorizonYears ?? UserSettings.DefaultReplacementHorizonYears;
            horizonSource = "Settings";
        }

        // Банк (bond_universe) — источник bid/offer для слиппеджа обеих ног (по ISIN; снимок — singleton-кэш, лишнего I/O нет).
        var snapshotForTarget = await snapshotBuilder.GetSnapshotAsync(ct);
        var bookForTarget = await scoreProvider.GetBookAsync(ct);

        var holdCandidate = BuildHoldSwitchCandidate(holdPosition, snapshotForTarget);
        var targetCandidate = new SwitchCandidate
        {
            // SwitchAnalysisService.Compare не использует targetPosition.MarketValueRub в расчёте
            // (капитал перехода идёт от holdPosition, см. doc-comment сервиса) — поле сохраняется как есть
            // для watchlist-target'а (MarketValueRub holding'а без позиции = грязная цена ОДНОЙ бумаги,
            // см. BuildForInstrumentsAsync), значение не искажает результат.
            PositionId = targetPosition.PositionId,
            MarketValueRub = targetPosition.MarketValueRub,
            EffectiveYield = (targetPosition.IsFloater || targetPosition.IsIndexed) ? targetPosition.CurrentYield : targetPosition.YtmEffective,
            MacaulayDurationYears = targetPosition.MacaulayDuration,
            HalfSpreadFraction = HalfSpreadForIsin(targetPosition.Isin, snapshotForTarget),
            // HorizonDate движка — ближайшая оферта (IsCalculatedToOffer) либо погашение: после неё доходность
            // цели «закончится», горизонт сравнения режется (задача 48).
            TargetHorizonDate = targetPosition.HorizonDate,
            TargetHorizonIsOffer = targetPosition.IsCalculatedToOffer,
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

        var result = SwitchAnalysisService.Compare(holdCandidate, targetCandidate, horizonYears, sellRate, buyRate, asOf);

        // Риск-сигналы для ВЫБРАННОЙ цели (не hold — план ограничивает объём
        // до "для выбранной цели") по её банк-записи (bond_universe), найденной по ISIN — снимок RV
        // уже загружен (singleton-кэш, ~раз в час), лишний I/O не добавляется. Null, если целевая
        // бумага не найдена в банке (не покрыта биржевой статистикой MOEX — например, только что
        // купленный внебиржевой инструмент) — тогда фронт просто не показывает сигналы, это не ошибка.
        var targetRiskSignals = targetPosition.Isin is { } targetIsin
            ? BuildRiskSignalsForIsin(targetIsin, snapshotForTarget, bookForTarget)
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
            // Формула-разбивка — доходностные поля null, если доходность
            // одной из сторон не определена (YieldDataIncomplete=true).
            SpreadFraction = result.SpreadFraction,
            CapitalRub = result.CapitalRub,
            GrossGainRub = result.GrossGainRub,
            AnnualizedBenefitFraction = result.AnnualizedBenefitFraction,
            SellTaxEstimateRub = result.SellTaxEstimateRub,
            NetBenefitAfterTaxRub = result.NetBenefitAfterTaxRub,
            TargetRiskSignals = targetRiskSignals,
            // Задача 48 (additive): горизонт, налог на купоны, слиппедж, будущие стоимости ног, дюрации.
            HorizonSource = horizonSource,
            EffectiveHorizonYears = result.EffectiveHorizonYears,
            HorizonLimitedByTarget = result.HorizonLimitedByTarget,
            TargetHorizonDate = result.TargetHorizonDate,
            TargetHorizonIsOffer = result.TargetHorizonIsOffer,
            CouponTaxRate = SwitchAnalysisService.CouponTaxRate,
            HoldYieldAfterTaxFraction = result.HoldYieldAfterTaxFraction,
            TargetYieldAfterTaxFraction = result.TargetYieldAfterTaxFraction,
            SellSlippageRub = result.SellSlippageRub,
            BuySlippageRub = result.BuySlippageRub,
            SlippageUnknown = result.SlippageUnknown,
            ProceedsRub = result.ProceedsRub,
            HoldFutureValueRub = result.HoldFutureValueRub,
            SwitchFutureValueRub = result.SwitchFutureValueRub,
            HoldMacaulayDurationYears = result.HoldMacaulayDurationYears,
            TargetMacaulayDurationYears = result.TargetMacaulayDurationYears,
            DurationMismatch = result.DurationMismatch,
            DurationMismatchWarning = result.DurationMismatchWarning,
        };

        return Results.Ok(dto);
    }

    /// <summary>
    /// Нога «удерживать» анализа замены по позиции портфеля: стоимость, доходность (YTM; для флоатера — текущая), Маколей,
    /// полуспред банка (слиппедж) и cost basis для налога продажи. Общая сборка для POST /replacement и вердиктов позиций
    /// (задача 49) — чтобы оба считали «hold» одинаково.
    /// </summary>
    internal static SwitchCandidate BuildHoldSwitchCandidate(
        Core.Analytics.PortfolioHolding holdPosition, RelativeValueSnapshotBuilder.RelativeValueSnapshot snapshot) => new()
    {
        PositionId = holdPosition.PositionId,
        MarketValueRub = holdPosition.MarketValueRub,
        EffectiveYield = (holdPosition.IsFloater || holdPosition.IsIndexed) ? holdPosition.CurrentYield : holdPosition.YtmEffective,
        MacaulayDurationYears = holdPosition.MacaulayDuration,
        HalfSpreadFraction = HalfSpreadForIsin(holdPosition.Isin, snapshot),
        // Налог продажи — из cost basis позиции (average cost); без него/при неполном журнале налог не оценивается.
        InvestedRub = holdPosition.CostBasis?.InvestedRub,
        HasUnknownLots = holdPosition.CostBasis?.HasUnknownLots ?? true,
    };

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
            // Задача 44: корзины банка строятся по MOEX DURATION = дюрация МАКОЛЕЯ (годы), поэтому бакет
            // позиции определяется тоже по Маколею (не по модифицированной — иначе позиция у границы
            // бакета попадала в чужую корзину).
            var assessment = Assess(holding.Sector, holding.MacaulayDuration, holding.GSpread!.Value, snapshot.AllMembers, snapshot.BasketStats);
            var verdict = assessment.Verdict;

            var candidates = verdict == RelativeValueVerdict.Rich
                ? BuildCheapCandidates(assessment.Basket, snapshot, holding.Isin)
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
                ThresholdFraction = assessment.ThresholdFraction,
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
        BasketResolution basket, RelativeValueSnapshotBuilder.RelativeValueSnapshot snapshot, string? positionIsin)
    {
        // Медиана — из РЕЗОЛЮЦИИ (а не поиском эффективной корзины в snapshot.BasketStats: у fallback
        // «весь сектор» ключа там нет, и отклонение считалось бы от 0).
        var effectiveBasket = basket.EffectiveBasket;
        var basketMedian = basket.Stats.Median;
        var basketMembers = ResolveCheapBasketMembers(effectiveBasket, basketMedian, snapshot, positionIsin, TopCheapCandidatesPerRichPosition);

        var result = new List<RelativeValueCandidateDto>(basketMembers.Count);
        foreach (var member in basketMembers)
        {
            snapshot.CurrentEntriesBySecid.TryGetValue(member.Secid, out var entry);
            var liquidity = entry is null
                ? LiquidityScoreCalculator.Assess(null, null, null, null)
                : AssessEntryLiquidity(entry, snapshot);

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
    internal static List<BasketMember> ResolveCheapBasketMembers(
        BasketKey effectiveBasket,
        decimal basketMedian,
        RelativeValueSnapshotBuilder.RelativeValueSnapshot snapshot,
        string? excludeIsin,
        int take)
    {
        return snapshot.AllMembers
            // «Весь рынок» (Low-откат) — все члены банка (раньше фильтр по Sector=="Весь рынок" отсекал всех, mode=rv был молча пуст).
            .Where(m => IsBasketMember(m, effectiveBasket))
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

    /// <summary>Значения <c>durationScope</c> (задача 50): кандидаты из бакета дюрации позиции ±1 соседний / весь рынок.</summary>
    public const string DurationScopeBucket = "bucket";
    public const string DurationScopeAll = "all";

    public const string ReplacementCandidatesDisclaimer =
        "Кандидаты и оценки — аналитическая информация, не инвестиционная рекомендация. Доходность " +
        "кандидатов ранжирована консервативно — к худшему из погашения и оферты, с учётом НДФЛ 13% (упрощённо, " +
        "ко всей доходности); по умолчанию — среди бумаг похожего срока. Точная " +
        "выгода считается для выбранной бумаги через сравнение (POST /api/analytics/replacement). " +
        "Риск-сигналы (ликвидность/листинг, спред к рынку) — по биржевой статистике MOEX, не рейтинг " +
        "рейтинговых агентств.";

    /// <summary>
    /// Единый источник кандидатов-замен для ОДНОЙ позиции портфеля (блок 1
    /// переработки «Рекомендаций»): <c>mode=market</c> — вся фикс-купонная гигиенически-чистая
    /// вселенная банка (<see cref="UniverseHygieneFilter"/>, БЕЗ флоатеров), отсортированная по
    /// после-налоговой доходности к worst убыв. (задача 50), по умолчанию — из бакета дюрации позиции ±1; <c>mode=rv</c> — дешёвые соседи по корзине сектор×дюрация ПОЗИЦИИ
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
        ReliabilityScoreProvider scoreProvider,
        IOptions<Bonds.Infrastructure.Universe.BondUniverseRefreshOptions> universeOptions,
        ulong positionId,
        string mode,
        int? limit,
        string? reliability,
        string? durationScope,
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

        var requestedScope = string.IsNullOrWhiteSpace(durationScope) ? DurationScopeBucket : durationScope.Trim().ToLowerInvariant();
        if (requestedScope is not (DurationScopeBucket or DurationScopeAll))
        {
            return ValidationError("durationScope должен быть 'bucket' или 'all'");
        }

        var accountId = await PositionsEndpoints.ResolveAccountIdAsync(currentUser, accountRepo);
        if (accountId is null) throw new NotFoundException($"Позиция {positionId} не найдена в портфеле");

        var asOf = BusinessClock.MoscowToday();
        var holdings = await holdingsBuilder.BuildForAccountAsync(accountId.Value, asOf);
        var position = holdings.FirstOrDefault(h => h.PositionId == positionId);
        if (position is null) throw new NotFoundException($"Позиция {positionId} не найдена в портфеле");

        var effectiveLimit = Math.Clamp(limit ?? DefaultReplacementCandidatesLimit, 1, MaxReplacementCandidatesLimit);
        var snapshot = await snapshotBuilder.GetSnapshotAsync(ct);
        var book = await scoreProvider.GetBookAsync(ct);

        // Задача 50: сопоставимость по сроку — только mode=market. Бакет позиции — по дюрации МАКОЛЕЯ (как у банка);
        // дюрации у позиции нет (флоатер/неполные данные) — бакет не определить, честно откатываемся на «весь рынок»
        // (эффективный scope в ответе подскажет UI, что запрос "bucket" не был применён).
        var positionBucketIndex = DurationBucketClassifier.Index(position.MacaulayDuration);
        var scopeToBucket = mode == "market" && requestedScope == DurationScopeBucket && positionBucketIndex is not null;

        var candidates = mode == "market"
            ? await BuildMarketCandidates(position, universeRepo, universeOptions.Value.Hygiene, asOf, snapshot, book, effectiveLimit, reliability, scopeToBucket)
            : BuildRvModeCandidates(position, snapshot, book, effectiveLimit, reliability);

        return Results.Ok(new ReplacementCandidatesResponseDto
        {
            Mode = mode,
            // Позиция портфеля всегда привязана к инструменту с ISIN (см. doc-comment Instrument) —
            // пустая строка здесь не должна происходить в норме, но не бросаем 500 из-за пробела в данных.
            PositionIsin = position.Isin ?? string.Empty,
            Candidates = candidates,
            Disclaimer = ReplacementCandidatesDisclaimer,
            DurationScope = mode == "market" ? (scopeToBucket ? DurationScopeBucket : DurationScopeAll) : null,
            PositionDurationBucket = positionBucketIndex is null ? null : DurationBucketClassifier.Label(position.MacaulayDuration),
            AfterTaxRateFraction = AfterTaxYield.TaxRate,
        });
    }

    /// <summary>
    /// mode=market (план часть B.2): гигиенически-чистая (<see cref="UniverseHygieneFilter"/>)
    /// фикс-купонная (<c>IsFloater != true</c>) вселенная банка, БЕЗ записей с
    /// <c>YieldFraction == null</c> (иначе нечем ранжировать), БЕЗ самой позиции (по ISIN),
    /// топ-<paramref name="limit"/> по ПОСЛЕ-НАЛОГОВОЙ доходности к worst (<see cref="CandidateYieldRanking"/>, задача 50:
    /// <c>min(YTM, к оферте) × (1 − 13%)</c>) убыв.; при <paramref name="restrictToDurationBucket"/> — только бакет дюрации
    /// позиции ±1 соседний (<see cref="DurationBucketClassifier.AreSameOrAdjacent"/>) Точную
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
        ReliabilityBook book,
        int limit,
        string? reliabilityFilter,
        bool restrictToDurationBucket)
    {
        var all = await universeRepo.GetAllAsync();

        var eligible = all
            .Where(e => UniverseHygieneFilter.Evaluate(e, hygieneOptions, asOf) == HygieneHiddenReason.None)
            .Where(e => e.IsFloater != true)
            .Where(e => e.YieldFraction is not null)
            .Where(e => position.Isin is null || !string.Equals(e.Isin, position.Isin, StringComparison.OrdinalIgnoreCase))
            // Задача 50: сопоставимость по сроку — бакет дюрации позиции ±1 соседний (оба по Маколею); без дюрации кандидат скрыт.
            .Where(e => !restrictToDurationBucket || DurationBucketClassifier.AreSameOrAdjacent(position.MacaulayDuration, e.DurationYears));

        // Задача 50: сортировка по после-налоговой доходности к worst (а не по сырой YTM), детерминированно.
        var ranked = CandidateYieldRanking.Rank(eligible).Select(x => x.Entry);

        if (string.IsNullOrWhiteSpace(reliabilityFilter))
        {
            return ranked.Take(limit).Select(e => ToReplacementCandidateDto(e, snapshot, book)).ToList();
        }

        return ranked
            .Select(e => ToReplacementCandidateDto(e, snapshot, book))
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
        ReliabilityBook book,
        int limit,
        string? reliabilityFilter)
    {
        // Задача 44: бакет позиции — по дюрации МАКОЛЕЯ (банк хранит MOEX DURATION = Маколей).
        if (position.GSpread is not { } bondSpread || position.MacaulayDuration is null)
        {
            return [];
        }

        var assessment = Assess(position.Sector, position.MacaulayDuration, bondSpread, snapshot.AllMembers, snapshot.BasketStats);
        var effectiveBasket = assessment.Basket.EffectiveBasket;
        var basketStats = assessment.Basket.Stats;
        var basketMedian = basketStats.Median;

        var take = string.IsNullOrWhiteSpace(reliabilityFilter) ? limit : snapshot.AllMembers.Count;
        var basketMembers = ResolveCheapBasketMembers(effectiveBasket, basketMedian, snapshot, position.Isin, take);

        var dtos = basketMembers.Select(m => ToReplacementCandidateDto(m, basketStats, snapshot, book));
        if (!string.IsNullOrWhiteSpace(reliabilityFilter))
        {
            dtos = dtos.Where(dto => ReliabilityMeetsFilter(dto.RiskSignals.Reliability, reliabilityFilter));
        }

        return dtos.Take(limit).ToList();
    }

    /// <summary>mode=market: обогащает банк-запись кандидата риск-сигналами.</summary>
    private static ReplacementCandidateDto ToReplacementCandidateDto(
        BondUniverseEntry entry, RelativeValueSnapshotBuilder.RelativeValueSnapshot snapshot, ReliabilityBook book)
    {
        var rank = CandidateYieldRanking.Evaluate(entry);
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
            AfterTaxYieldFraction = rank?.AfterTaxYieldFraction,
            YieldBasis = rank?.Basis,
            DurationYears = entry.DurationYears,
            GSpreadFraction = entry.EffectiveGspreadFraction,
            OfferDate = entry.OfferDate,
            RiskSignals = BuildRiskSignalsDto(entry, snapshot, book),
        };
    }

    /// <summary>mode=rv: обогащает члена корзины именем/доходностью из текущего <c>bond_universe</c>
    /// (тот же путь, что <see cref="BuildCheapCandidates"/>) + риск-сигналами относительно ОБЩЕЙ
    /// медианы корзины позиции (все RV-кандидаты — из ОДНОЙ эффективной корзины, поэтому медиана
    /// одна на всех, в отличие от mode=market).</summary>
    private static ReplacementCandidateDto ToReplacementCandidateDto(
        BasketMember member, BasketStats basketStats, RelativeValueSnapshotBuilder.RelativeValueSnapshot snapshot, ReliabilityBook book)
    {
        snapshot.CurrentEntriesBySecid.TryGetValue(member.Secid, out var entry);
        var liquidity = entry is null
            ? LiquidityScoreCalculator.Assess(null, null, null, null)
            : AssessEntryLiquidity(entry, snapshot);
        var signals = AssessRiskSignals(liquidity, entry?.ListLevel, member.GSpreadFraction, basketStats);
        var rank = entry is null ? null : CandidateYieldRanking.Evaluate(entry);

        return new ReplacementCandidateDto
        {
            Secid = member.Secid,
            Isin = entry?.Isin,
            Name = entry?.ShortName ?? entry?.SecName ?? member.Secid,
            Issuer = null,
            Sector = member.Sector,
            YieldFraction = entry?.YieldFraction,
            AfterTaxYieldFraction = rank?.AfterTaxYieldFraction,
            YieldBasis = rank?.Basis,
            DurationYears = member.DurationYears,
            GSpreadFraction = member.GSpreadFraction,
            OfferDate = entry?.OfferDate,
            // Светофор — скоркарта по банк-записи (entry — тот же банк-снимок, что несёт листинг/сектор/эмитента);
            // entry может быть null, если secid не резолвится в текущем снимке банка — тогда Unknown (Yellow).
            RiskSignals = ToRiskSignalsDto(signals, book.Get(member.Secid), AssessValue(entry, snapshot)),
        };
    }

    /// <summary>
    /// Риск-сигналы бумаги по её банк-записи, найденной по ISIN среди ТЕКУЩЕГО
    /// снимка bond_universe (<see cref="RelativeValueSnapshotBuilder.RelativeValueSnapshot.CurrentEntriesBySecid"/>
    /// индексирован по secid, не по ISIN — банк маленький (~3400 строк), линейный поиск по значению
    /// один раз за запрос не оправдывает отдельный индекс/репозиторный метод ради единственного
    /// потребителя). Null, если ISIN не найден в банке (бумага вне биржевой статистики MOEX).
    /// </summary>
    private static RiskSignalsDto? BuildRiskSignalsForIsin(
        string isin, RelativeValueSnapshotBuilder.RelativeValueSnapshot snapshot, ReliabilityBook book)
    {
        var entry = FindBankEntryByIsin(isin, snapshot);
        if (entry is null) return null;

        return ToReplacementCandidateDto(entry, snapshot, book).RiskSignals;
    }

    private static BondUniverseEntry? FindBankEntryByIsin(string isin, RelativeValueSnapshotBuilder.RelativeValueSnapshot snapshot) =>
        snapshot.CurrentEntriesBySecid.Values
            .FirstOrDefault(e => string.Equals(e.Isin, isin, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Полуспред стакана бумаги (ДОЛЯ) по её банк-записи — оценка слиппеджа одной стороны сделки для
    /// <see cref="SwitchAnalysisService"/>. Формула — единая, <see cref="LiquidityScoreCalculator.HalfSpreadFraction"/>
    /// (bid/offer банка — % от номинала, единица сокращается). Null — бумаги нет в банке или стакан не виден.
    /// </summary>
    internal static decimal? HalfSpreadForIsin(string? isin, RelativeValueSnapshotBuilder.RelativeValueSnapshot snapshot)
    {
        if (isin is null) return null;
        var entry = FindBankEntryByIsin(isin, snapshot);
        return entry is null ? null : LiquidityScoreCalculator.HalfSpreadFraction(entry.BidPercent, entry.OfferPercent);
    }
}

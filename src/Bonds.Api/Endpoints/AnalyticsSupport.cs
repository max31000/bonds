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

/// <summary>Общие хелперы аналитических эндпоинтов (валидация, риск-сигналы кандидатов, фильтр надёжности).</summary>
internal static class AnalyticsSupport
{
    /// <summary>Валидирует значение query-параметра <c>reliability</c> (общий для
    /// GET /api/analytics/replacement-candidates и GET /api/universe). Null/пусто — валиден (без
    /// фильтра). <c>internal</c> — переиспользуется <c>UniverseEndpoints</c> (та же сборка).</summary>
    internal static bool IsValidReliabilityFilterValue(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.Trim().ToLowerInvariant() is "green" or "yellow" or "red";

    /// <summary>
    /// «не хуже уровня»: green = только Green; yellow = Green+Yellow; red = все
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
    /// Задача 34 переиспользует эту резолюцию (source=recommended, риск-фильтр Caution +
    /// RiskSignalsDto на строках аллокации) — вынесено из <see cref="ToReplacementCandidateDto(BondUniverseEntry, RelativeValueSnapshotBuilder.RelativeValueSnapshot)"/>,
    /// чтобы не дублировать резолюцию корзины/ликвидности. Медиана корзины — СВОЯ у каждой записи
    /// (сектор × дюрация ЗАПИСИ, не позиции/другого кандидата), резолвится через
    /// <see cref="RelativeValueService.ResolveBasket"/> той же fallback-цепочкой, что RV-вердикт позиций.
    /// <c>internal</c> (не <c>private</c>) — переиспользуется <c>UniverseEndpoints.GetUniverse</c>
    /// (тот же сборка Bonds.Api) для светофора надёжности каждой строки скринера.
    /// </summary>
    internal static CandidateRiskSignals AssessEntryRiskSignals(
        BondUniverseEntry entry, RelativeValueSnapshotBuilder.RelativeValueSnapshot snapshot)
    {
        var liquidity = AssessEntryLiquidity(entry, snapshot);

        BasketStats? basketStats = null;
        if (entry.EffectiveGspreadFraction is not null)
        {
            var key = new BasketKey
            {
                Sector = string.IsNullOrWhiteSpace(entry.Sector) ? UnknownSector : entry.Sector!,
                DurationBucket = Bonds.Core.Analytics.DurationBucketClassifier.Label(entry.DurationYears),
            };
            basketStats = ResolveBasket(key, snapshot.AllMembers, snapshot.BasketStats).Stats;
        }

        return AssessRiskSignals(liquidity, entry.ListLevel, entry.EffectiveGspreadFraction, basketStats);
    }

    /// <summary>
    /// Ликвидность записи банка (задача 44): оборот — медиана за 5 торговых дней из истории
    /// (<see cref="RelativeValueSnapshotBuilder.RelativeValueSnapshot.LiquidityTurnoverRub"/>, fallback —
    /// сегодняшний VALTODAY), а не «оборот на сейчас» — утренний и вечерний прогон дают одну оценку.
    /// </summary>
    internal static LiquidityAssessment AssessEntryLiquidity(
        BondUniverseEntry entry, RelativeValueSnapshotBuilder.RelativeValueSnapshot snapshot) =>
        LiquidityScoreCalculator.Assess(snapshot.LiquidityTurnoverRub(entry), entry.BidPercent, entry.OfferPercent, entry.NumTrades);

    /// <summary>
    /// Единая сборка риск-сигналов из готовой ликвидности и статистики корзины кандидата: порог оси
    /// спреда — тот же <see cref="SpreadThresholds.ForBasket"/> (max(20 б.п., 0.25 × IQR)), что и у
    /// RV-вердикта; подпись ликвидности различает «тонкий стакан» и «стакан не виден в снимке».
    /// <paramref name="basketStats"/> null (нет спреда/корзины) → медиана и порог null, ось спреда Neutral.
    /// </summary>
    internal static CandidateRiskSignals AssessRiskSignals(
        LiquidityAssessment liquidity, int? listLevel, decimal? gSpreadFraction, BasketStats? basketStats) =>
        CandidateRiskSignalService.Assess(
            liquidity.Score,
            listLevel,
            gSpreadFraction,
            basketStats?.Median,
            basketStats is null ? null : ThresholdFor(basketStats),
            liquidity.BookSpreadFraction,
            bookMissing: liquidity.BookSpreadFraction is null);


    /// <summary>Собирает RiskSignalsDto из двух информационных сигналов +
    /// агрегированного светофора надёжности (<see cref="CandidateRiskSignalService.Aggregate"/>).
    /// <paramref name="listLevel"/>/<paramref name="sector"/> — те же входы, что уже использованы
    /// для расчёта <paramref name="signals"/> (листинг банк-записи, сектор банк-записи).</summary>
    internal static RiskSignalsDto ToRiskSignalsDto(CandidateRiskSignals signals, int? listLevel, string? sector)
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

    internal static IResult ValidationError(string message) =>
        Results.Json(new { error = message, type = "ValidationException" }, statusCode: StatusCodes.Status422UnprocessableEntity);
}

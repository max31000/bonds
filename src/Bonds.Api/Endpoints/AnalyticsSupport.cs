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


    /// <summary>
    /// Ось Value (задача 46): вердикт цены бумаги — G-спред против медианы ЕЁ корзины (сектор × дюрация Маколея),
    /// порог — IQR корзины, Confidence Low → InsufficientData (<see cref="RelativeValueService.Assess"/>). ОТДЕЛЬНО
    /// от риска: уровень надёжности из него НЕ строится. Нет записи/спреда/корзины → InsufficientData.
    /// </summary>
    internal static string AssessValueVerdict(BondUniverseEntry? entry, RelativeValueSnapshotBuilder.RelativeValueSnapshot snapshot) =>
        AssessValue(entry, snapshot).Verdict;

    /// <summary>Ось Value с числами для тултипа (задача 47): вердикт + медиана корзины и порог — те же, по которым он
    /// вынесен. Медиана и порог — ДОЛИ (0.003 = 30 б.п.); null, когда вердикта нет из-за отсутствия спреда.</summary>
    internal readonly record struct ValueAssessment(string Verdict, decimal? MedianFraction, decimal? ThresholdFraction);

    /// <summary>См. <see cref="AssessValueVerdict"/>; дополнительно отдаёт медиану корзины (= G-спред − отклонение) и порог.</summary>
    internal static ValueAssessment AssessValue(BondUniverseEntry? entry, RelativeValueSnapshotBuilder.RelativeValueSnapshot snapshot)
    {
        if (entry?.EffectiveGspreadFraction is not { } spread)
        {
            return new ValueAssessment(RelativeValueVerdict.InsufficientData.ToString(), null, null);
        }

        var assessment = Assess(entry.Sector, entry.DurationYears, spread, snapshot.AllMembers, snapshot.BasketStats);
        return new ValueAssessment(assessment.Verdict.ToString(), spread - assessment.DeviationFraction, assessment.ThresholdFraction);
    }

    /// <summary>Собирает <see cref="RiskSignalsDto"/>: два информационных сигнала (ликвидность/спред) +
    /// скоркарта надёжности v2 (светофор, score, coverage, компоненты, флаги) + ось Value.</summary>
    internal static RiskSignalsDto ToRiskSignalsDto(CandidateRiskSignals signals, ReliabilityScore score, ValueAssessment value) =>
        new()
        {
            Liquidity = signals.Liquidity.ToString(),
            LiquidityLabel = signals.LiquidityLabel,
            Spread = signals.Spread.ToString(),
            GSpreadFraction = signals.GSpreadFraction,
            SpreadVsBasketMedianFraction = signals.SpreadVsBasketMedianFraction,
            Reliability = score.Level.ToString(),
            ReliabilityReason = score.Reason,
            Score = score.Score,
            Coverage = score.Coverage,
            Components = score.Components.Select(ToComponentDto).ToList(),
            CriticalFlags = score.CriticalFlags,
            WarningFlags = score.WarningFlags,
            ValueVerdict = value.Verdict,
            ValueMedianFraction = value.MedianFraction,
            ValueThresholdFraction = value.ThresholdFraction,
        };

    /// <summary>Полная карточка банк-записи: сигналы + скоркарта из <paramref name="book"/> + ось Value.</summary>
    internal static RiskSignalsDto BuildRiskSignalsDto(
        BondUniverseEntry entry, RelativeValueSnapshotBuilder.RelativeValueSnapshot snapshot, ReliabilityBook book) =>
        ToRiskSignalsDto(AssessEntryRiskSignals(entry, snapshot), book.Get(entry.Secid), AssessValue(entry, snapshot));

    internal static ScoreComponentDto ToComponentDto(ScoreComponent c) => new()
    {
        Key = c.Key,
        Title = c.Title,
        RawValue = c.RawValue,
        RawDisplay = c.RawDisplay,
        Points = c.Points,
        MaxPoints = c.MaxPoints,
        Source = c.Source,
        Missing = c.Missing,
        Layer = c.Layer,
        Stale = c.Stale,
    };

    internal static IResult ValidationError(string message) =>
        Results.Json(new { error = message, type = "ValidationException" }, statusCode: StatusCodes.Status422UnprocessableEntity);
}

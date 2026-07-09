using Bonds.Core.Universe;

namespace Bonds.Core.Analytics;

/// <summary>
/// Задача 33 часть A — уровень одного риск-сигнала кандидата-замены. <b>ИНФОРМАЦИОННЫЙ, не
/// «надёжность»</b>: владелец явно ограничил объём задачи 33 — не считать «надёжность рейтинговых
/// агентств», в будущем добавится отдельный тег кредитного рейтинга. <see cref="Good"/> —
/// позитивный сигнал (спокойнее рынка своей группы), <see cref="Caution"/> — негативный (заметно
/// рискованнее/неликвиднее своей группы), ни один уровень не ранжирует кандидатов — ранжирование
/// (mode=market) идёт по доходности, сигналы только сопровождают карточку.
/// </summary>
public enum SignalLevel
{
    Good,
    Neutral,
    Caution,
}

/// <summary>
/// Задача 33 часть A — два риск-сигнала одного кандидата-замены (не рейтинг, см. doc-comment
/// <see cref="SignalLevel"/>). Единицы: <see cref="GSpreadFraction"/> и
/// <see cref="SpreadVsBasketMedianFraction"/> — ДОЛИ (контракт репо, см. docs/CODEBASE-GUIDE.md).
/// </summary>
/// <param name="Liquidity">Ликвидность+листинг — см. <see cref="CandidateRiskSignalService.AssessLiquidity"/>.</param>
/// <param name="LiquidityLabel">Человекочитаемая подпись, например "Высокая ликвидность, листинг 1"
/// или "Низкий оборот, листинг 3" — без слова «надёжно».</param>
/// <param name="Spread">Отклонение G-спреда кандидата от медианы ЕГО ЖЕ корзины (сектор × дюрация).</param>
/// <param name="GSpreadFraction">ДОЛЯ; из <c>BondUniverseEntry.GspreadApproxFraction</c> кандидата.
/// Null, если MOEX не отдал спред/кривую (см. doc-comment поля источника) — тогда
/// <see cref="Spread"/> = Neutral, а не Caution (нет данных ≠ негативный сигнал).</param>
/// <param name="SpreadVsBasketMedianFraction">ДОЛЯ; = GSpreadFraction − медиана корзины кандидата.
/// Знак: положительное — спред кандидата ВЫШЕ медианы (рынок закладывает бОльшую премию/риск),
/// отрицательное — НИЖЕ (спокойнее, но и доходность обычно ниже). Null вместе с
/// <see cref="GSpreadFraction"/>.</param>
public sealed record CandidateRiskSignals(
    SignalLevel Liquidity,
    string LiquidityLabel,
    SignalLevel Spread,
    decimal? GSpreadFraction,
    decimal? SpreadVsBasketMedianFraction);

/// <summary>
/// Задача 33 часть A — чистый сервис двух информационных риск-сигналов кандидата-замены поверх
/// уже посчитанных данных (переиспользует <see cref="Bonds.Core.Universe.LiquidityScoreCalculator"/>
/// и медиану корзины <see cref="RelativeValueService"/> — сам НЕ делает I/O и не строит корзины
/// заново, вызывающий слой (Infrastructure/Api) передаёт готовые входы).
/// </summary>
public static class CandidateRiskSignalService
{
    /// <summary>
    /// Порог |отклонение спреда от медианы корзины| для Caution/Good (согласовано с RV-вердиктом
    /// Cheap/Fair/Rich, см. <c>AnalyticsEndpoints.FairVerdictThresholdFraction</c> в Bonds.Api —
    /// Bonds.Core не может ссылаться на Api-слой, поэтому здесь отдельная константа с тем же
    /// значением; держать оба места синхронными при пересмотре порога, см. план задачи 33 часть A).
    /// 0.0020 = 20 базисных пунктов (доля, общая конвенция единиц репо).
    /// </summary>
    public const decimal SpreadDeviationThresholdFraction = 0.0020m;

    /// <summary>
    /// Матрица ликвидность×листинг → уровень (план задачи 33 часть A.1, зафиксировано тестами
    /// <c>CandidateRiskSignalServiceTests</c>):
    /// <list type="bullet">
    /// <item>High, ListLevel ∈ {1, 2, null} → Good.</item>
    /// <item>High, ListLevel == 3 → Neutral (листинг 3 тянет на один шаг к Caution).</item>
    /// <item>Medium, ListLevel ∈ {1, 2, null} → Neutral.</item>
    /// <item>Medium, ListLevel == 3 → Caution.</item>
    /// <item>Low, любой ListLevel → Caution (уже худший уровень, листинг не может понизить дальше).</item>
    /// <item>None (недостаточно данных — не "неликвидная бумага", см. doc-comment
    /// <see cref="LiquidityScore.None"/>) → Neutral независимо от ListLevel — план прямым текстом:
    /// null-данные не должны читаться как негативный сигнал.</item>
    /// </list>
    /// </summary>
    public static (SignalLevel Level, string Label) AssessLiquidity(LiquidityScore liquidityScore, int? listLevel)
    {
        var baseLevel = liquidityScore switch
        {
            LiquidityScore.High => SignalLevel.Good,
            LiquidityScore.Medium => SignalLevel.Neutral,
            LiquidityScore.Low => SignalLevel.Caution,
            _ => SignalLevel.Neutral, // None — недостаточно данных, не Caution.
        };

        var level = listLevel == 3 && liquidityScore != LiquidityScore.None
            ? StepTowardCaution(baseLevel)
            : baseLevel;

        return (level, BuildLiquidityLabel(liquidityScore, listLevel));
    }

    private static SignalLevel StepTowardCaution(SignalLevel level) => level switch
    {
        SignalLevel.Good => SignalLevel.Neutral,
        SignalLevel.Neutral => SignalLevel.Caution,
        _ => SignalLevel.Caution,
    };

    private static string BuildLiquidityLabel(LiquidityScore liquidityScore, int? listLevel)
    {
        var liquidityPart = liquidityScore switch
        {
            LiquidityScore.High => "Высокая ликвидность",
            LiquidityScore.Medium => "Средняя ликвидность",
            LiquidityScore.Low => "Низкий оборот",
            _ => "Недостаточно данных по ликвидности",
        };

        return listLevel is { } level ? $"{liquidityPart}, листинг {level}" : liquidityPart;
    }

    /// <summary>
    /// Отклонение G-спреда кандидата от медианы ЕГО ЖЕ корзины (план часть A.1): заметно выше
    /// (&gt; <see cref="SpreadDeviationThresholdFraction"/>) → Caution (рынок закладывает повышенный
    /// риск/премию), в пределах порога → Neutral, заметно ниже → Good (спокойнее рынка своей группы).
    /// <paramref name="gSpreadFraction"/> или <paramref name="basketMedianGSpreadFraction"/> == null
    /// (MOEX не отдал спред/кривую или корзина не резолвится) → Neutral, оба значения в ответе null —
    /// план прямым текстом требует не путать "нет данных" с негативным сигналом.
    /// </summary>
    public static SignalLevel AssessSpread(decimal? gSpreadFraction, decimal? basketMedianGSpreadFraction)
    {
        if (gSpreadFraction is not { } gSpread || basketMedianGSpreadFraction is not { } median)
        {
            return SignalLevel.Neutral;
        }

        var deviation = gSpread - median;
        if (deviation > SpreadDeviationThresholdFraction) return SignalLevel.Caution;
        if (deviation < -SpreadDeviationThresholdFraction) return SignalLevel.Good;
        return SignalLevel.Neutral;
    }

    /// <summary>
    /// Собирает оба риск-сигнала кандидата. <paramref name="basketMedianGSpreadFraction"/> —
    /// медиана ЕГО ЖЕ корзины (сектор × дюрация кандидата, НЕ позиции, для которой ищем замену) —
    /// вызывающий слой резолвит её через <see cref="RelativeValueService.ResolveBasket"/> по
    /// снимку <c>RelativeValueSnapshotBuilder</c> (переиспользование инфраструктуры корзин RV, план
    /// часть A).
    /// </summary>
    public static CandidateRiskSignals Assess(
        LiquidityScore liquidityScore,
        int? listLevel,
        decimal? gSpreadFraction,
        decimal? basketMedianGSpreadFraction)
    {
        var (liquidityLevel, liquidityLabel) = AssessLiquidity(liquidityScore, listLevel);
        var spreadLevel = AssessSpread(gSpreadFraction, basketMedianGSpreadFraction);

        // Отклонение считается только когда известны ОБА входа — если по какой-то причине известен
        // только спред кандидата, но не медиана его корзины (не должно происходить в норме — корзина
        // всегда резолвится fallback-цепочкой RelativeValueService, но защищаемся явно), отклонение
        // не показываем частично посчитанным. GSpreadFraction в ответе — эхо входа (не зависит от
        // медианы) кроме единственного случая по плану: спред == null → медиана тоже игнорируется.
        decimal? deviation = gSpreadFraction is not null && basketMedianGSpreadFraction is not null
            ? gSpreadFraction.Value - basketMedianGSpreadFraction.Value
            : null;

        return new CandidateRiskSignals(liquidityLevel, liquidityLabel, spreadLevel, gSpreadFraction, deviation);
    }
}

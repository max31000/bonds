using Bonds.Core.Universe;

namespace Bonds.Core.Analytics;

/// <summary>
/// Уровень одного риск-сигнала кандидата-замены. <b>ИНФОРМАЦИОННЫЙ, не
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
/// Два риск-сигнала одного кандидата-замены (не рейтинг, см. doc-comment
/// <see cref="SignalLevel"/>). Единицы: <see cref="GSpreadFraction"/> и
/// <see cref="SpreadVsBasketMedianFraction"/> — ДОЛИ (контракт репо, см. docs/CODEBASE-GUIDE.md).
/// </summary>
/// <param name="Liquidity">Ликвидность+листинг — см. <see cref="CandidateRiskSignalService.AssessLiquidity"/>.</param>
/// <param name="LiquidityLabel">Человекочитаемая подпись, например "Высокая ликвидность, листинг 1"
/// или "Низкий оборот, листинг 3" — без слова «надёжно».</param>
/// <param name="Spread">Отклонение G-спреда кандидата от медианы ЕГО ЖЕ корзины (сектор × дюрация).</param>
/// <param name="GSpreadFraction">ДОЛЯ; из <c>BondUniverseEntry.EffectiveGspreadFraction</c> кандидата (биржевой, при его отсутствии приближённый).
/// Null, если MOEX не отдал спред/кривую (см. doc-comment поля источника) — тогда
/// <see cref="Spread"/> = Neutral, а не Caution (нет данных ≠ негативный сигнал).</param>
/// <param name="SpreadVsBasketMedianFraction">ДОЛЯ; = GSpreadFraction − медиана корзины кандидата.
/// Знак: положительное — спред кандидата ВЫШЕ медианы (рынок закладывает бОльшую премию/риск),
/// отрицательное — НИЖЕ (спокойнее, но и доходность обычно ниже). Null вместе с
/// <see cref="GSpreadFraction"/>.</param>
/// <param name="LiquidityScoreRaw">Задача 38 часть A: сырой скор ликвидности ДО свёртки в
/// <see cref="SignalLevel"/> — нужен <see cref="CandidateRiskSignalService.Aggregate"/>, чтобы
/// отличить <see cref="Bonds.Core.Universe.LiquidityScore.None"/> (нет данных) от
/// <see cref="Bonds.Core.Universe.LiquidityScore.Medium"/> (оба дают одинаковый
/// <see cref="SignalLevel.Neutral"/> — см. <see cref="CandidateRiskSignalService.AssessLiquidity"/>,
/// но для Green-критерия задачи 38 "ликвидность не None" это разные состояния).</param>
public sealed record CandidateRiskSignals(
    SignalLevel Liquidity,
    string LiquidityLabel,
    SignalLevel Spread,
    decimal? GSpreadFraction,
    decimal? SpreadVsBasketMedianFraction,
    LiquidityScore LiquidityScoreRaw);

/// <summary>
/// Чистый сервис двух информационных риск-сигналов кандидата-замены поверх
/// уже посчитанных данных (переиспользует <see cref="Bonds.Core.Universe.LiquidityScoreCalculator"/>
/// и медиану корзины <see cref="RelativeValueService"/> — сам НЕ делает I/O и не строит корзины
/// заново, вызывающий слой (Infrastructure/Api) передаёт готовые входы).
/// </summary>
public static class CandidateRiskSignalService
{
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
    /// <param name="liquidityScore">Скор (по сглаженному обороту — задача 44).</param>
    /// <param name="listLevel">Уровень листинга.</param>
    /// <param name="bookSpreadFraction">Спред стакана, ДОЛЯ (0.021 = 2.1%); null — не передан/не виден.</param>
    /// <param name="bookMissing">true — bid/offer отсутствуют в снимке («стакан не виден»); подпись это
    /// различает с «тонким стаканом» (известный широкий спред). По умолчанию false — подпись как раньше.</param>
    public static (SignalLevel Level, string Label) AssessLiquidity(
        LiquidityScore liquidityScore, int? listLevel, decimal? bookSpreadFraction = null, bool bookMissing = false)
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

        return (level, BuildLiquidityLabel(liquidityScore, listLevel, bookSpreadFraction, bookMissing));
    }

    private static SignalLevel StepTowardCaution(SignalLevel level) => level switch
    {
        SignalLevel.Good => SignalLevel.Neutral,
        SignalLevel.Neutral => SignalLevel.Caution,
        _ => SignalLevel.Caution,
    };

    /// <summary>Подпись ликвидности. Задача 44: различает «тонкий стакан 2.1%» (известный спред ≥ порога
    /// Medium) и «стакан не виден в снимке» (bid/offer нет) — для тултипа UI. Процент в подписи =
    /// <paramref name="bookSpreadFraction"/> × 100 (доля → проценты для человека, одна конверсия здесь).</summary>
    private static string BuildLiquidityLabel(
        LiquidityScore liquidityScore, int? listLevel, decimal? bookSpreadFraction, bool bookMissing)
    {
        var thinBook = bookSpreadFraction is { } spread && spread >= LiquidityScoreCalculator.MediumSpreadThresholdFraction
            ? $"тонкий стакан {(spread * 100m).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}%"
            : null;
        const string bookNotVisible = "стакан не виден в снимке";

        var liquidityPart = liquidityScore switch
        {
            LiquidityScore.High => "Высокая ликвидность",
            LiquidityScore.Medium => bookMissing ? $"Средняя ликвидность, {bookNotVisible}" : "Средняя ликвидность",
            LiquidityScore.Low => thinBook is not null
                ? $"Низкая ликвидность, {thinBook}"
                : bookMissing ? $"Низкий оборот, {bookNotVisible}" : "Низкий оборот",
            _ => "Недостаточно данных по ликвидности",
        };

        return listLevel is { } level ? $"{liquidityPart}, листинг {level}" : liquidityPart;
    }

    /// <summary>
    /// Отклонение G-спреда кандидата от медианы ЕГО ЖЕ корзины (план часть A.1): заметно выше
    /// (&gt; <see cref="SpreadThresholds.DeviationFraction"/>) → Caution (рынок закладывает повышенный
    /// риск/премию), в пределах порога → Neutral, заметно ниже → Good (спокойнее рынка своей группы).
    /// <paramref name="gSpreadFraction"/> или <paramref name="basketMedianGSpreadFraction"/> == null
    /// (MOEX не отдал спред/кривую или корзина не резолвится) → Neutral, оба значения в ответе null —
    /// план прямым текстом требует не путать "нет данных" с негативным сигналом.
    /// </summary>
    /// <param name="thresholdFraction">Порог корзины, ДОЛЯ (<see cref="SpreadThresholds.ForBasket"/> =
    /// max(20 б.п., 0.25 × IQR) — задача 44, тот же порог, что RV-вердикт). null → нижняя планка
    /// <see cref="SpreadThresholds.DeviationFraction"/>.</param>
    public static SignalLevel AssessSpread(
        decimal? gSpreadFraction, decimal? basketMedianGSpreadFraction, decimal? thresholdFraction = null)
    {
        if (gSpreadFraction is not { } gSpread || basketMedianGSpreadFraction is not { } median)
        {
            return SignalLevel.Neutral;
        }

        var threshold = thresholdFraction ?? SpreadThresholds.DeviationFraction;
        var deviation = gSpread - median;
        if (deviation > threshold) return SignalLevel.Caution;
        if (deviation < -threshold) return SignalLevel.Good;
        return SignalLevel.Neutral;
    }

    /// <summary>
    /// Собирает оба риск-сигнала кандидата. <paramref name="basketMedianGSpreadFraction"/> —
    /// медиана ЕГО ЖЕ корзины (сектор × дюрация кандидата, НЕ позиции, для которой ищем замену) —
    /// вызывающий слой резолвит её через <see cref="RelativeValueService.ResolveBasket"/> по
    /// снимку <c>RelativeValueSnapshotBuilder</c> (переиспользование инфраструктуры корзин RV, план
    /// часть A). <paramref name="spreadThresholdFraction"/> — порог корзины (ДОЛЯ, задача 44);
    /// <paramref name="bookSpreadFraction"/>/<paramref name="bookMissing"/> — для подписи ликвидности.
    /// </summary>
    public static CandidateRiskSignals Assess(
        LiquidityScore liquidityScore,
        int? listLevel,
        decimal? gSpreadFraction,
        decimal? basketMedianGSpreadFraction,
        decimal? spreadThresholdFraction = null,
        decimal? bookSpreadFraction = null,
        bool bookMissing = false)
    {
        var (liquidityLevel, liquidityLabel) = AssessLiquidity(liquidityScore, listLevel, bookSpreadFraction, bookMissing);
        var spreadLevel = AssessSpread(gSpreadFraction, basketMedianGSpreadFraction, spreadThresholdFraction);

        // Отклонение считается только когда известны ОБА входа — если по какой-то причине известен
        // только спред кандидата, но не медиана его корзины (не должно происходить в норме — корзина
        // всегда резолвится fallback-цепочкой RelativeValueService, но защищаемся явно), отклонение
        // не показываем частично посчитанным. GSpreadFraction в ответе — эхо входа (не зависит от
        // медианы) кроме единственного случая по плану: спред == null → медиана тоже игнорируется.
        decimal? deviation = gSpreadFraction is not null && basketMedianGSpreadFraction is not null
            ? gSpreadFraction.Value - basketMedianGSpreadFraction.Value
            : null;

        return new CandidateRiskSignals(liquidityLevel, liquidityLabel, spreadLevel, gSpreadFraction, deviation, liquidityScore);
    }

    // ─── Сектора суверенных эмитентов (используются скоркартой, задача 46) ──────────────────

    /// <summary>Классификация "Гособлигации" банка (<c>BondUniverseEntry.Sector</c>) — см.
    /// <c>Bonds.Infrastructure.Connectors.Moex.BondUniverseSectorMapper</c>/<c>MoexSegmentMapper</c>.
    /// Bonds.Core не может ссылаться на Infrastructure (layering) — строка продублирована здесь,
    /// тот же приём, что <see cref="SpreadThresholds.DeviationFraction"/>.</summary>
    public const string GovernmentSectorLabel = "Гособлигации";

    /// <summary>Классификация "Муниципальные" банка — см. doc-comment <see cref="GovernmentSectorLabel"/>.</summary>
    public const string MunicipalSectorLabel = "Муниципальные";
}

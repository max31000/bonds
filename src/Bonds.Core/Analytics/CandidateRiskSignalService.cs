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
/// Задача 38 часть A — светофор надёжности: ОДИН агрегат поверх двух информационных риск-сигналов
/// (<see cref="SignalLevel"/>) кандидата. <b>НЕ кредитный рейтинг</b> — см. doc-comment
/// <see cref="CandidateRiskSignalService.Aggregate"/> для точной матрицы и владельческих рамок
/// задачи (никаких внешних источников, никаких формулировок "рейтинг"/"надёжность эмитента").
/// </summary>
public enum ReliabilityLight
{
    Green,
    Yellow,
    Red,
}

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

        return new CandidateRiskSignals(liquidityLevel, liquidityLabel, spreadLevel, gSpreadFraction, deviation, liquidityScore);
    }

    // ─── Задача 38 часть A — светофор надёжности ────────────────────────────────────────────

    /// <summary>Классификация "Гособлигации" банка (<c>BondUniverseEntry.Sector</c>) — см.
    /// <c>Bonds.Infrastructure.Connectors.Moex.BondUniverseSectorMapper</c>/<c>MoexSegmentMapper</c>.
    /// Bonds.Core не может ссылаться на Infrastructure (layering) — строка продублирована здесь,
    /// тот же приём, что <see cref="SpreadDeviationThresholdFraction"/> (см. её doc-comment).</summary>
    public const string GovernmentSectorLabel = "Гособлигации";

    /// <summary>Классификация "Муниципальные" банка — см. doc-comment <see cref="GovernmentSectorLabel"/>.</summary>
    public const string MunicipalSectorLabel = "Муниципальные";

    private static bool IsSovereignSector(string? sector) =>
        string.Equals(sector, GovernmentSectorLabel, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(sector, MunicipalSectorLabel, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Задача 38 часть A.1 — светофор надёжности: ОДИН агрегат поверх двух риск-сигналов
    /// (<see cref="SignalLevel"/> ликвидности и спреда) + листинга + сектора. <b>Информационный
    /// сигнал по биржевой статистике, НЕ кредитный рейтинг</b> (владелец задачи 38 явно запретил
    /// эту формулировку — нет надёжного бесплатного источника кредитного качества, см. план).
    /// <para>
    /// <b>Матрица (зафиксирована тестами <c>CandidateRiskSignalServiceTests.Aggregate_MatchesMatrix</c>):</b>
    /// <list type="bullet">
    /// <item><b>Red</b> — <see cref="CandidateRiskSignals.Liquidity"/> == Caution, ИЛИ
    /// <see cref="CandidateRiskSignals.Spread"/> == Caution И сектор НЕ суверенный (см. ниже).
    /// Любой Caution — красный, независимо от второй оси.</item>
    /// <item><b>Green</b> — ни один из случаев Red не сработал, И <c>LiquidityScoreRaw != None</c>
    /// (данные по ликвидности есть — None даёт тот же <see cref="SignalLevel.Neutral"/>, что Medium,
    /// но НЕ считается "нормой" для зелёного), И <paramref name="listLevel"/> ∈ {1, 2} (листинг 3 или
    /// неизвестный листинг не дотягивают до зелёного, даже если оба сигнала Good/Neutral).</item>
    /// <item><b>Yellow</b> — всё остальное: смешанные сигналы недостающего листинга/ликвидности.
    /// В частности, null-спред И null-ликвидность (<c>LiquidityScoreRaw == None</c>) ОДНОВРЕМЕННО —
    /// Yellow, НЕ Red (нехватка данных ≠ негативный сигнал, тот же принцип, что часть A задачи 33).</item>
    /// <item><b>Суверенное исключение</b> — сектор "Гособлигации"/"Муниципальные"
    /// (<see cref="GovernmentSectorLabel"/>/<see cref="MunicipalSectorLabel"/>): спред НЕ участвует
    /// ни в Red, ни в требованиях Green ("любой спред" — суверенный кредит РФ, спред к кривой ОФЗ не
    /// несёт того же кредитного смысла, что у корпоративного эмитента). Исключение касается ТОЛЬКО
    /// оси спреда — ликвидность/листинг по-прежнему могут дать Red/Yellow для гособлигации.</item>
    /// </list>
    /// </para>
    /// <para>Возвращает уровень + строку-обоснование (что именно притянуло вниз) — обоснование НЕ
    /// содержит слова "рейтинг"/"надёжность эмитента" (закреплено тестом), финальный обязательный
    /// дисклеймер "оценка по биржевой статистике, не кредитный рейтинг" — на фронте (задача 38 часть C),
    /// рядом с обоснованием, не внутри строки.</para>
    /// </summary>
    public static (ReliabilityLight Level, string Reason) Aggregate(
        CandidateRiskSignals signals, int? listLevel, string? sector)
    {
        var isSovereign = IsSovereignSector(sector);
        var liquidityCaution = signals.Liquidity == SignalLevel.Caution;
        // Спред суверенного сектора не считается Caution ни для Red, ни для Green-требований —
        // единственное исключение матрицы (см. doc-comment выше).
        var spreadCountsAsCaution = signals.Spread == SignalLevel.Caution && !isSovereign;

        if (liquidityCaution || spreadCountsAsCaution)
        {
            var causes = new List<string>();
            if (liquidityCaution) causes.Add("ликвидность/листинг в зоне риска (сигнал Caution)");
            if (spreadCountsAsCaution) causes.Add("спред заметно выше медианы корзины (сигнал Caution)");
            return (ReliabilityLight.Red, $"Красный: {string.Join("; ", causes)}.");
        }

        var liquidityDataMissing = signals.LiquidityScoreRaw == LiquidityScore.None;
        var listingOutsideCore = listLevel is not (1 or 2);

        if (!liquidityDataMissing && !listingOutsideCore)
        {
            return (ReliabilityLight.Green, isSovereign
                ? "Зелёный: гособлигация/муниципальная — суверенный кредит, спред не учитывается; ликвидность и листинг в норме."
                : "Зелёный: оба риск-сигнала не хуже Neutral, листинг 1-2, ликвидность с данными.");
        }

        var gaps = new List<string>();
        if (liquidityDataMissing) gaps.Add("недостаточно данных по ликвидности (оборот/сделки не покрывают порог оценки)");
        if (listingOutsideCore)
        {
            gaps.Add(listLevel is null ? "листинг неизвестен" : $"листинг {listLevel} (для зелёного нужен листинг 1 или 2)");
        }

        return (ReliabilityLight.Yellow, $"Жёлтый: {string.Join("; ", gaps)}.");
    }
}

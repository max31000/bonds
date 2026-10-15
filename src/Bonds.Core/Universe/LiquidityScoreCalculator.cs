namespace Bonds.Core.Universe;

/// <summary>Скор ликвидности бумаги банка облигаций — грубая эвристика по
/// обороту/спреду/числу сделок, НЕ модель рыночного воздействия. None — недостаточно данных
/// (нет оборота/спреда вовсе), не "неликвидная бумага".</summary>
public enum LiquidityScore
{
    None = 0,
    Low = 1,
    Medium = 2,
    High = 3,
}

/// <summary>Результат оценки ликвидности — скор + оценка проскальзывания + спред стакана.</summary>
/// <param name="Score">Скор.</param>
/// <param name="SlippageEstimateFraction">Половина спреда стакана, ДОЛЯ; null — стакан не виден.</param>
/// <param name="BookSpreadFraction">Задача 44: спред стакана (offer−bid)/mid, ДОЛЯ; null — bid/offer нет в
/// снимке («стакан не виден»), а не «спред нулевой». Нужен подписи ликвидности в UI.</param>
public readonly record struct LiquidityAssessment(
    LiquidityScore Score, decimal? SlippageEstimateFraction, decimal? BookSpreadFraction = null);

/// <summary>
/// Чистая функция оценки ликвидности бумаги банка облигаций по готовой
/// биржевой статистике (оборот — медиана за 5 торговых дней, см. MedianTurnover; bid-offer спред/число сделок «на сейчас»), БЕЗ обращения к точному
/// движку/сети. Пороги — намеренно грубые константы уровня "разумного дефолта" (не откалиброваны
/// на исторических данных о проскальзывании) — цель не точный расчёт market impact, а быстрая
/// сортировка "торгуется активно / средне / почти не торгуется" для скринера/сравнивалки.
/// </summary>
public static class LiquidityScoreCalculator
{
    /// <summary>High: оборот &gt; 5 млн ₽/день И спред &lt; 0.3% от цены.</summary>
    public const decimal HighTurnoverThresholdRub = 5_000_000m;
    public const decimal HighSpreadThresholdFraction = 0.003m;

    /// <summary>Medium: оборот &gt; 300 тыс ₽/день И спред &lt; 1.5% от цены.</summary>
    public const decimal MediumTurnoverThresholdRub = 300_000m;
    public const decimal MediumSpreadThresholdFraction = 0.015m;

    /// <summary>Low: оборот &gt; 0 (были сделки) ИЛИ есть котировки bid/offer — что-то торгуется,
    /// но не дотягивает до Medium.</summary>
    public const int LowNumTradesThreshold = 1;

    /// <summary>
    /// Оценивает ликвидность по обороту (РУБЛИ за день), bid/offer (в % от номинала —
    /// спред считается относительно средней цены bid/offer) и числу сделок. Null-параметры
    /// означают "MOEX не отдал данные" — не считаем это неликвидностью, возвращаем
    /// <see cref="LiquidityScore.None"/> (недостаточно данных), а не Low.
    /// <para>
    /// <b>Оборот</b> вызывающий передаёт сглаженный — медиану за 5 торговых дней
    /// (<see cref="MedianTurnover"/>, <c>RelativeValueSnapshot.LiquidityTurnoverRub</c>), а не сегодняшний
    /// VALTODAY «на сейчас» (утром он мал и занижал бы оценку); fallback на VALTODAY — если истории нет.
    /// </para>
    /// <para>
    /// <b>Стакан без данных ≠ плохая ликвидность</b> (задача 44): отсутствие bid/offer в снимке не
    /// понижает до Low бумагу с живым оборотом. High требует ИЗВЕСТНОГО узкого спреда (&lt; 0.3%);
    /// Medium — оборот &gt; 300 тыс. и (спред &lt; 1.5% ИЛИ спред неизвестен); Low — оборот &gt; 0, сделки или
    /// стакан, не дотянувшие до Medium (в т.ч. известный широкий спред ≥ 1.5%); None — все входы null.
    /// Пороги не откалиброваны.
    /// </para>
    /// </summary>
    public static LiquidityAssessment Assess(decimal? turnoverRub, decimal? bidPercent, decimal? offerPercent, int? numTrades)
    {
        var spreadFraction = ComputeSpreadFraction(bidPercent, offerPercent);
        var slippageEstimate = HalfSpreadFraction(bidPercent, offerPercent);

        if (turnoverRub is null && spreadFraction is null && numTrades is null)
        {
            return new LiquidityAssessment(LiquidityScore.None, slippageEstimate, spreadFraction);
        }

        var turnover = turnoverRub ?? 0m;
        var trades = numTrades ?? 0;

        if (turnover > HighTurnoverThresholdRub && spreadFraction is { } hs && hs < HighSpreadThresholdFraction)
        {
            return new LiquidityAssessment(LiquidityScore.High, slippageEstimate, spreadFraction);
        }

        if (turnover > MediumTurnoverThresholdRub && (spreadFraction is null || spreadFraction < MediumSpreadThresholdFraction))
        {
            return new LiquidityAssessment(LiquidityScore.Medium, slippageEstimate, spreadFraction);
        }

        if (turnover > 0m || trades >= LowNumTradesThreshold || spreadFraction is not null)
        {
            return new LiquidityAssessment(LiquidityScore.Low, slippageEstimate, spreadFraction);
        }

        return new LiquidityAssessment(LiquidityScore.None, slippageEstimate, spreadFraction);
    }

    /// <summary>
    /// Медиана оборота (РУБЛИ) по дневным значениям; null-значения пропускаются. Чётное число
    /// значений — среднее двух центральных. Null, если не осталось ни одного значения (→ вызывающий
    /// использует сегодняшний VALTODAY).
    /// </summary>
    public static decimal? MedianTurnover(IEnumerable<decimal?> dailyTurnoversRub)
    {
        var sorted = dailyTurnoversRub.Where(v => v.HasValue).Select(v => v!.Value).OrderBy(v => v).ToList();
        if (sorted.Count == 0) return null;

        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 0 ? (sorted[mid - 1] + sorted[mid]) / 2m : sorted[mid];
    }

    /// <summary>
    /// Половина спреда стакана — оценка проскальзывания одной стороны сделки (по рынку покупаешь по offer,
    /// продаёшь по bid, «справедливая» цена ≈ mid): (offer − bid) / ((offer + bid) / 2) / 2 = (offer − bid) /
    /// (offer + bid). ДОЛЯ от цены. Единый источник формулы для <see cref="Assess"/> и анализа замены
    /// (<c>SwitchAnalysisService</c>, задача 48) — не дублировать. bid/offer — в % от номинала (единица
    /// сокращается, важно только отношение). Null — стакан не виден (bid/offer нет или ≤ 0).
    /// </summary>
    public static decimal? HalfSpreadFraction(decimal? bidPercent, decimal? offerPercent) =>
        ComputeSpreadFraction(bidPercent, offerPercent) is { } s ? s / 2m : null;

    /// <summary>Спред в долях от средней цены bid/offer: (offer − bid) / ((offer + bid) / 2).
    /// Null, если bid или offer отсутствует, либо средняя цена ≤ 0 (защита от деления на ноль).</summary>
    private static decimal? ComputeSpreadFraction(decimal? bidPercent, decimal? offerPercent)
    {
        if (bidPercent is not { } bid || offerPercent is not { } offer) return null;
        if (bid <= 0m || offer <= 0m) return null;

        var mid = (bid + offer) / 2m;
        if (mid <= 0m) return null;

        return (offer - bid) / mid;
    }
}

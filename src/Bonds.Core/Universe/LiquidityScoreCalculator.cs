namespace Bonds.Core.Universe;

/// <summary>Скор ликвидности бумаги банка облигаций (задача 26 часть C.2) — грубая эвристика по
/// обороту/спреду/числу сделок, НЕ модель рыночного воздействия. None — недостаточно данных
/// (нет оборота/спреда вовсе), не "неликвидная бумага".</summary>
public enum LiquidityScore
{
    None = 0,
    Low = 1,
    Medium = 2,
    High = 3,
}

/// <summary>Результат оценки ликвидности — скор + оценка проскальзывания.</summary>
public readonly record struct LiquidityAssessment(LiquidityScore Score, decimal? SlippageEstimateFraction);

/// <summary>
/// Задача 26 часть C.2 — чистая функция оценки ликвидности бумаги банка облигаций по готовой
/// биржевой статистике (оборот/bid-offer спред/число сделок за сегодня), БЕЗ обращения к точному
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
    /// Оценивает ликвидность по обороту (в рублях за сегодня), bid/offer (в % от номинала —
    /// спред считается относительно средней цены bid/offer) и числу сделок. Null-параметры
    /// означают "MOEX не отдал данные" — не считаем это неликвидностью, возвращаем
    /// <see cref="LiquidityScore.None"/> (недостаточно данных), а не Low.
    /// </summary>
    public static LiquidityAssessment Assess(decimal? turnoverRub, decimal? bidPercent, decimal? offerPercent, int? numTrades)
    {
        var spreadFraction = ComputeSpreadFraction(bidPercent, offerPercent);
        var slippageEstimate = spreadFraction is { } s ? s / 2m : (decimal?)null;

        if (turnoverRub is null && spreadFraction is null && numTrades is null)
        {
            return new LiquidityAssessment(LiquidityScore.None, slippageEstimate);
        }

        var turnover = turnoverRub ?? 0m;
        var trades = numTrades ?? 0;

        if (turnover > HighTurnoverThresholdRub && spreadFraction is { } hs && hs < HighSpreadThresholdFraction)
        {
            return new LiquidityAssessment(LiquidityScore.High, slippageEstimate);
        }

        if (turnover > MediumTurnoverThresholdRub && spreadFraction is { } ms && ms < MediumSpreadThresholdFraction)
        {
            return new LiquidityAssessment(LiquidityScore.Medium, slippageEstimate);
        }

        if (turnover > 0m || trades >= LowNumTradesThreshold || spreadFraction is not null)
        {
            return new LiquidityAssessment(LiquidityScore.Low, slippageEstimate);
        }

        return new LiquidityAssessment(LiquidityScore.None, slippageEstimate);
    }

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

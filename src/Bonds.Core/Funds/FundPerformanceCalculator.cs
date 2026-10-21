using Bonds.Core.Models;

namespace Bonds.Core.Funds;

/// <summary>
/// Доходность фонда за окно по рыночным данным. <see cref="ReturnFraction"/> — ДОЛЯ за окно целиком (0.046 = 4.6%),
/// <see cref="AnnualizedFraction"/> — ДОЛЯ годовых <c>(1+TR)^(365/Days) − 1</c>, <see cref="Days"/> — календарные дни окна.
/// </summary>
public sealed record FundWindowReturn(decimal ReturnFraction, decimal? AnnualizedFraction, int Days);

/// <summary>Фактическая доходность по стандартным окнам. Окно без цены на старте = null.</summary>
public sealed record FundPerformance(
    FundWindowReturn? M1,
    FundWindowReturn? M3,
    FundWindowReturn? M6,
    FundWindowReturn? M12,
    FundWindowReturn? SinceFirstBuy);

/// <summary>
/// Фактическая (тотал-ретёрн) доходность фонда по рыночным данным — НЕ зависит от владения (задача 53):
/// <c>TR = (P_end + Σ выплат на пай с record_date в (start, end]) / P_start − 1</c>. Цены — рубли за пай (MOEX TQBR),
/// выплаты — рубли на пай (T-Invest, после TER фонда); результат — ДОЛИ. <c>P_start</c> — последняя цена ≤ даты старта
/// (forward fill), <c>P_end</c> — последняя цена ≤ даты конца; нет цены на старте — окно null. Налоги не учитываются.
/// Чистая функция.
/// </summary>
public static class FundPerformanceCalculator
{
    public static FundPerformance Compute(
        IReadOnlyList<FundPricePoint> prices,
        IReadOnlyList<FundDistribution> distributions,
        DateOnly asOf,
        DateOnly? firstBuyDate)
    {
        FundWindowReturn? Window(DateOnly start) => ComputeWindow(prices, distributions, start, asOf);

        return new FundPerformance(
            Window(asOf.AddMonths(-1)),
            Window(asOf.AddMonths(-3)),
            Window(asOf.AddMonths(-6)),
            Window(asOf.AddMonths(-12)),
            firstBuyDate is { } d ? Window(d) : null);
    }

    /// <summary>Доходность окна <c>(start, end]</c>; null — нет цены на старте/конце, окно вырожденное или цена старта ≤ 0.</summary>
    public static FundWindowReturn? ComputeWindow(
        IReadOnlyList<FundPricePoint> prices,
        IReadOnlyList<FundDistribution> distributions,
        DateOnly start,
        DateOnly end)
    {
        var days = end.DayNumber - start.DayNumber;
        if (days <= 0) return null;

        var pStart = LastPriceOnOrBefore(prices, start);
        var pEnd = LastPriceOnOrBefore(prices, end);
        if (pStart is not { } ps || pEnd is not { } pe || ps <= 0m) return null;

        var distSum = distributions.Where(x => x.RecordDate > start && x.RecordDate <= end).Sum(x => x.AmountPerShareRub);
        var tr = (pe + distSum) / ps - 1m;
        return new FundWindowReturn(tr, Annualize(tr, days), days);
    }

    /// <summary>Минимум дней окна для пересчёта в годовую: на коротких окнах экстраполяция бессмысленна (месяц роста ×12 ≠ год).</summary>
    public const int MinAnnualizeDays = 30;

    /// <summary>
    /// ДОЛЯ за <paramref name="days"/> дней → ДОЛЯ годовых: <c>(1+tr)^(365/days) − 1</c>; при tr ≤ −1 → −1;
    /// окно короче <see cref="MinAnnualizeDays"/> дней → null (не аннуализируем).
    /// </summary>
    public static decimal? Annualize(decimal totalReturn, int days)
    {
        if (days < MinAnnualizeDays) return null;
        if (totalReturn <= -1m) return -1m;
        var value = Math.Pow((double)(1m + totalReturn), 365.0 / days) - 1.0;
        // Защита от переполнения decimal на экстремальных коротких окнах: такие оценки всё равно бессмысленны.
        if (double.IsNaN(value) || double.IsInfinity(value) || Math.Abs(value) > 1e6) return value > 0 ? 1_000_000m : -1m;
        return (decimal)value;
    }

    private static decimal? LastPriceOnOrBefore(IReadOnlyList<FundPricePoint> prices, DateOnly date)
    {
        FundPricePoint? best = null;
        foreach (var p in prices)
        {
            if (p.TradeDate <= date && (best is null || p.TradeDate > best.TradeDate)) best = p;
        }
        return best?.CloseRub;
    }
}

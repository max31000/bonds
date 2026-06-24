using Bonds.Core.Models;

namespace Bonds.Core.Analytics;

/// <summary>
/// Композиция портфеля (plan/06 B2, spec §9 «Композиция портфеля») — доли рыночной стоимости
/// по эмитенту, сектору, типу купона и корзинам дюрации. Сумма долей в каждом разрезе = 100%
/// (с округлением последнего элемента, чтобы сумма не "уплывала" из-за округления каждой доли
/// по отдельности — см. <see cref="NormalizeShares"/>). Чистый сервис, без I/O.
/// </summary>
public static class PortfolioCompositionService
{
    /// <summary>
    /// Границы корзин дюрации в годах (spec §9 «корзины дюрации» — конкретные границы не заданы
    /// спекой, выбраны как разумные дефолты профильных калькуляторов; см. финальный отчёт этапа,
    /// помечено как требующее согласования с владельцем, если потребуется другая разбивка):
    /// 0–1, 1–3, 3–5, 5–7, 7+ лет. Holding без дюрации (флоатер/неполные данные) попадает
    /// в отдельную корзину "Без оценки", не отбрасывается молча (spec §4.4).
    /// </summary>
    private static readonly (decimal UpperBoundYears, string Label)[] DurationBuckets =
    [
        (1m, "0–1 года"),
        (3m, "1–3 года"),
        (5m, "3–5 лет"),
        (7m, "5–7 лет"),
        (decimal.MaxValue, "7+ лет"),
    ];

    private const string UnknownLabel = "Не определено";

    public static PortfolioComposition Calculate(IReadOnlyList<PortfolioHolding> holdings)
    {
        var totalMarketValue = holdings.Sum(h => h.MarketValueRub);

        return new PortfolioComposition
        {
            TotalMarketValueRub = totalMarketValue,
            ByIssuer = GroupByShare(holdings, totalMarketValue, h => h.Issuer ?? UnknownLabel),
            BySector = GroupByShare(holdings, totalMarketValue, h => h.Sector ?? UnknownLabel),
            ByCouponType = GroupByShare(holdings, totalMarketValue, h => h.CouponType.ToString()),
            ByDurationBucket = GroupByShare(holdings, totalMarketValue, h => DurationBucketLabel(h.ModifiedDuration)),
        };
    }

    private static string DurationBucketLabel(decimal? modifiedDuration)
    {
        if (modifiedDuration is null) return UnknownLabel;

        foreach (var (upperBound, label) in DurationBuckets)
        {
            if (modifiedDuration.Value < upperBound) return label;
        }

        return DurationBuckets[^1].Label;
    }

    private static IReadOnlyList<CompositionShare> GroupByShare(
        IReadOnlyList<PortfolioHolding> holdings,
        decimal totalMarketValue,
        Func<PortfolioHolding, string> keySelector)
    {
        var groups = holdings
            .GroupBy(keySelector)
            .Select(g => new { Key = g.Key, Value = g.Sum(h => h.MarketValueRub) })
            .OrderByDescending(g => g.Value)
            .ToList();

        if (groups.Count == 0 || totalMarketValue == 0m)
        {
            return groups
                .Select(g => new CompositionShare { Key = g.Key, MarketValueRub = g.Value, SharePercent = 0m })
                .ToList();
        }

        return NormalizeShares(groups.Select(g => (g.Key, g.Value)).ToList(), totalMarketValue);
    }

    /// <summary>
    /// Считает доли в процентах так, чтобы сумма по разрезу была равна ровно 100.00 (spec §9
    /// "сумма долей = 100%"): округляет все доли, кроме последней, до 2 знаков, а последней
    /// присваивает остаток (100 − сумма предыдущих) — устраняет дрейф округления, типичный при
    /// независимом округлении каждой доли по отдельности.
    /// </summary>
    private static List<CompositionShare> NormalizeShares(List<(string Key, decimal Value)> groups, decimal totalMarketValue)
    {
        var result = new List<CompositionShare>(groups.Count);
        decimal accumulatedPercent = 0m;

        for (var i = 0; i < groups.Count; i++)
        {
            var (key, value) = groups[i];
            decimal sharePercent;

            if (i == groups.Count - 1)
            {
                sharePercent = Math.Round(100m - accumulatedPercent, 2, MidpointRounding.AwayFromZero);
            }
            else
            {
                sharePercent = Math.Round(value / totalMarketValue * 100m, 2, MidpointRounding.AwayFromZero);
                accumulatedPercent += sharePercent;
            }

            result.Add(new CompositionShare { Key = key, MarketValueRub = value, SharePercent = sharePercent });
        }

        return result;
    }
}

/// <summary>Результат расчёта композиции по всем разрезам (spec §9).</summary>
public sealed record PortfolioComposition
{
    public required decimal TotalMarketValueRub { get; init; }
    public required IReadOnlyList<CompositionShare> ByIssuer { get; init; }
    public required IReadOnlyList<CompositionShare> BySector { get; init; }
    public required IReadOnlyList<CompositionShare> ByCouponType { get; init; }
    public required IReadOnlyList<CompositionShare> ByDurationBucket { get; init; }
}

/// <summary>Доля одной группы (эмитент/сектор/тип купона/корзина дюрации) в портфеле.</summary>
public sealed record CompositionShare
{
    public required string Key { get; init; }
    public required decimal MarketValueRub { get; init; }
    public required decimal SharePercent { get; init; }
}

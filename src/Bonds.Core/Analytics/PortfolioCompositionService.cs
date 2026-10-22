using Bonds.Core.Models;

namespace Bonds.Core.Analytics;

/// <summary>
/// Композиция портфеля (spec §9 «Композиция портфеля») — доли рыночной стоимости
/// по эмитенту, сектору, типу купона и корзинам дюрации. Сумма долей в каждом разрезе = 100%
/// (с округлением последнего элемента, чтобы сумма не "уплывала" из-за округления каждой доли
/// по отдельности — см. <see cref="NormalizeShares"/>). Чистый сервис, без I/O.
/// </summary>
public static class PortfolioCompositionService
{
    private const string UnknownLabel = "Не определено";

    /// <summary>Ключ эмитента в <see cref="PortfolioComposition.ByIssuer"/> для позиции без эмитента.</summary>
    public const string UnknownIssuerLabel = UnknownLabel;

    public static PortfolioComposition Calculate(IReadOnlyList<PortfolioHolding> holdings)
    {
        var totalMarketValue = holdings.Sum(h => h.MarketValueRub);

        return new PortfolioComposition
        {
            TotalMarketValueRub = totalMarketValue,
            ByIssuer = GroupByShare(holdings, totalMarketValue, h => h.Issuer ?? UnknownLabel),
            BySector = GroupByShare(holdings, totalMarketValue, h => h.Sector ?? UnknownLabel),
            ByCouponType = GroupByShare(holdings, totalMarketValue, h => h.CouponType.ToString()),
            // Границы корзин дюрации вынесены в общий DurationBucketClassifier (переиспользуется
            // RelativeValueService) — консистентность с разрезом композиции портфеля, не копипаста границ.
            ByDurationBucket = GroupByShare(holdings, totalMarketValue, h => DurationBucketClassifier.Label(h.ModifiedDuration)),
        };
    }

    /// <summary>Ключ класса актива в <see cref="PortfolioComposition.ByAssetClass"/>: облигации.</summary>
    public const string BondsAssetClass = "Облигации";

    /// <summary>Ключ класса актива: паи фондов (БПИФ).</summary>
    public const string FundsAssetClass = "Фонды";

    /// <summary>Ключ сектора для фондов облигаций.</summary>
    public const string FundSectorLabel = "Фонды облигаций";

    /// <summary>Ключ «типа купона» для фонда (у пая купона нет).</summary>
    public const string FundCouponLabel = "Фонд";

    /// <summary>Префикс ключа эмитента для фонда: <c>"Фонд: &lt;название&gt;"</c>.</summary>
    public const string FundIssuerPrefix = "Фонд: ";

    /// <summary>
    /// Композиция «облигации + фонды» (задача 57). Тот же алгоритм и та же нормализация долей, что у <see cref="Calculate"/> (сумма = 100.00 в
    /// каждом разрезе; <c>SharePercent</c> — ПРОЦЕНТЫ, задокументированное исключение), плюс разрез <see cref="PortfolioComposition.ByAssetClass"/>.
    /// Фонд в разрезах: эмитент — «Фонд: &lt;название&gt;»; сектор — «Фонды облигаций»; тип купона — «Фонд»; корзина дюрации —
    /// <see cref="DurationBucketClassifier.Label"/> от модифицированной дюрации ГОДЫ (null → «Не определено»). Фонды с нулевой/отрицательной
    /// стоимостью пропускаются. <see cref="Calculate"/> для существующих потребителей (SignalsEngine, вердикты) не менялся: фонды туда не попадают.
    /// </summary>
    public static PortfolioComposition CalculateWithFunds(
        IReadOnlyList<PortfolioHolding> holdings, IReadOnlyList<FundExposure> funds)
    {
        var liveFunds = funds.Where(f => f.MarketValueRub > 0m).ToList();
        var items = holdings
            .Select(h => new Item(
                h.MarketValueRub, h.Issuer ?? UnknownLabel, h.Sector ?? UnknownLabel, h.CouponType.ToString(),
                DurationBucketClassifier.Label(h.ModifiedDuration), BondsAssetClass))
            .Concat(liveFunds.Select(f => new Item(
                f.MarketValueRub, FundIssuerPrefix + (f.Name ?? f.Ticker ?? UnknownLabel), FundSectorLabel, FundCouponLabel,
                DurationBucketClassifier.Label(f.ModifiedDurationYears), FundsAssetClass)))
            .ToList();
        var total = items.Sum(i => i.Value);

        return new PortfolioComposition
        {
            TotalMarketValueRub = total,
            ByIssuer = GroupByShare(items, total, i => i.Issuer, i => i.Value),
            BySector = GroupByShare(items, total, i => i.Sector, i => i.Value),
            ByCouponType = GroupByShare(items, total, i => i.Coupon, i => i.Value),
            ByDurationBucket = GroupByShare(items, total, i => i.Bucket, i => i.Value),
            ByAssetClass = GroupByShare(items, total, i => i.AssetClass, i => i.Value),
        };
    }

    private sealed record Item(decimal Value, string Issuer, string Sector, string Coupon, string Bucket, string AssetClass);

    private static IReadOnlyList<CompositionShare> GroupByShare(
        IReadOnlyList<PortfolioHolding> holdings,
        decimal totalMarketValue,
        Func<PortfolioHolding, string> keySelector) =>
        GroupByShare(holdings, totalMarketValue, keySelector, h => h.MarketValueRub);

    private static IReadOnlyList<CompositionShare> GroupByShare<T>(
        IEnumerable<T> items,
        decimal totalMarketValue,
        Func<T, string> keySelector,
        Func<T, decimal> valueSelector)
    {
        var groups = items
            .GroupBy(keySelector)
            .Select(g => new { Key = g.Key, Value = g.Sum(valueSelector) })
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

    /// <summary>Разрез «Облигации / Фонды» (задача 57); заполняет только <see cref="PortfolioCompositionService.CalculateWithFunds"/>, у <c>Calculate</c> пуст.</summary>
    public IReadOnlyList<CompositionShare> ByAssetClass { get; init; } = [];
}

/// <summary>
/// Позиция в фонде для композиции и сценариев ставок (задача 57). <see cref="MarketValueRub"/> — РУБЛИ (единая цена пая × паи),
/// <see cref="ModifiedDurationYears"/> — МОДИФИЦИРОВАННАЯ дюрация в ГОДАХ (оценка, <c>FundDurationEstimator</c>); null — чувствительность неизвестна.
/// </summary>
public sealed record FundExposure(
    ulong FundId,
    string? Ticker,
    string? Name,
    decimal MarketValueRub,
    decimal? ModifiedDurationYears);

/// <summary>Доля одной группы (эмитент/сектор/тип купона/корзина дюрации) в портфеле.</summary>
public sealed record CompositionShare
{
    public required string Key { get; init; }
    public required decimal MarketValueRub { get; init; }

    /// <summary>
    /// Audit(portfolio) P-1: В ПРОЦЕНТАХ (0-100), НЕ в долях — осознанное исключение из
    /// конвенции репо «бэкенд отдаёт доли, ×100 делает фронт». Фронт (Dashboard pie,
    /// Analytics composition, useRecommendationsStore) читает это поле через выделенный
    /// <c>formatSharePercent</c> (БЕЗ дополнительного ×100), а не общий <c>formatPercent</c>.
    /// Сумма долей внутри одного разреза (ByIssuer/BySector/...) равна ровно 100m
    /// (см. <c>NormalizeShares</c>). Если меняете один конец этой пары (бэк или фронт) —
    /// обязательно меняйте оба.
    /// </summary>
    public required decimal SharePercent { get; init; }
}

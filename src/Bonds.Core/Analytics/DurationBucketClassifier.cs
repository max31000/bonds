namespace Bonds.Core.Analytics;

/// <summary>
/// Границы дюрационных корзин, вынесенные из <see cref="PortfolioCompositionService"/>
/// в общий хелпер — переиспользуются композицией портфеля (ByDurationBucket) И relative-value
/// корзинами (<see cref="RelativeValueService"/>), чтобы UI показывал бумагу в ТОЙ ЖЕ корзине, что
/// и разрез композиции портфеля (план явно требует консистентность, не копипасту границ).
/// </summary>
public static class DurationBucketClassifier
{
    /// <summary>
    /// Границы корзин дюрации в годах (spec §9 «корзины дюрации» — конкретные границы не заданы
    /// спекой, выбраны как разумные дефолты профильных калькуляторов; см. финальный отчёт этапа 06,
    /// помечено как требующее согласования с владельцем, если потребуется другая разбивка):
    /// 0–1, 1–3, 3–5, 5–7, 7+ лет.
    /// </summary>
    private static readonly (decimal UpperBoundYears, string Label)[] Buckets =
    [
        (1m, "0–1 года"),
        (3m, "1–3 года"),
        (5m, "3–5 лет"),
        (7m, "5–7 лет"),
        (decimal.MaxValue, "7+ лет"),
    ];

    /// <summary>Метка корзины "Не определено" — дюрация отсутствует (флоатер/неполные данные).</summary>
    public const string UnknownLabel = "Не определено";

    /// <summary>Возвращает метку корзины дюрации (например "1–3 года") либо <see cref="UnknownLabel"/>,
    /// если дюрация не посчиталась.</summary>
    public static string Label(decimal? durationYears)
    {
        if (durationYears is null) return UnknownLabel;

        foreach (var (upperBound, label) in Buckets)
        {
            if (durationYears.Value < upperBound) return label;
        }

        return Buckets[^1].Label;
    }

    /// <summary>
    /// Порядковый номер корзины (0 — «0–1 года» … 4 — «7+ лет»), по тем же границам, что <see cref="Label"/>;
    /// null — дюрации нет. Нужен для «соседних бакетов» (задача 50): сопоставимость кандидатов по сроку.
    /// </summary>
    public static int? Index(decimal? durationYears)
    {
        if (durationYears is null) return null;

        for (var i = 0; i < Buckets.Length; i++)
        {
            if (durationYears.Value < Buckets[i].UpperBoundYears) return i;
        }

        return Buckets.Length - 1;
    }

    /// <summary>Тот же бакет или соседний (±1); если дюрация любой из сторон неизвестна — false.</summary>
    public static bool AreSameOrAdjacent(decimal? a, decimal? b) =>
        Index(a) is { } ia && Index(b) is { } ib && Math.Abs(ia - ib) <= 1;
}

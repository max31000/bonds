namespace Bonds.Core.Analytics;

/// <summary>
/// Задача 30 часть A — «относительная дешевизна» (Bloomberg RV-подход): «где YTM больше» — плохой
/// вопрос (наверху рынка обычно преддефолтный мусор), правильный вопрос деск-трейдера — «что дёшево
/// ОТНОСИТЕЛЬНО СВОИХ» — сравнить G-спред бумаги с медианным G-спредом её КОРЗИНЫ (сектор ×
/// дюрационный бакет). Бумага сильно НАД медианой корзины — «дешёвая» (рынок закладывает бОльшую
/// премию за риск, либо недооценивает), сильно ПОД — «дорогая» (кандидат на продажу/пересмотр).
/// <para>
/// <b>Корзина</b> = (Sector, DurationBucketLabel) — дюрационные границы переиспользуют
/// <see cref="DurationBucketClassifier"/> (те же, что <c>PortfolioCompositionService.ByDurationBucket</c>
/// — план явно требует консистентность с уже существующим разрезом UI, не изобретать новые границы).
/// Сектор — как в <c>bond_universe.sector</c> (простая классификация гос/муни/корп, см.
/// <c>BondUniverseSectorMapper</c>).
/// </para>
/// <para>
/// <b>Смешение точного и приближённого G-спреда (осознанное решение плана).</b> Для позиций
/// портфеля используется НАШ ТОЧНЫЙ спред движка (<c>PortfolioHolding.GSpread</c>, посчитан через
/// <see cref="Bonds.Core.Calculation.BondMetricsCalculator"/> + <see cref="Bonds.Core.Calculation.GSpreadCalculator"/>),
/// а корзины (медиана/перцентили) строятся по ПРИБЛИЖЁННОМУ спреду всей рыночной вселенной
/// (<c>BondUniverseEntry.GspreadApproxFraction</c> — дешёвая биржевая статистика MOEX, см.
/// doc-comment <c>BondUniverseEntry</c>). Точный и приближённый спред считаются по одной и той же
/// формуле (G-спред = YTM/YIELD минус безрисковая кривая на срок дюрации), различие только в
/// источнике YTM/дюрации (движок vs биржевые YIELD/DURATION MOEX) — типовое расхождение единицы
/// б.п., меньше типового межквартильного размаха корзины (обычно десятки-сотни б.п.), поэтому
/// смешение не искажает вывод «дёшево/дорого относительно корзины», но не является тождеством.
/// </para>
/// </summary>
public static class RelativeValueService
{
    /// <summary>Минимальный размер корзины для надёжной медианы (план часть A.3) — меньше запускает
    /// fallback-цепочку корзина → сектор целиком → весь рынок, с понижением <see cref="RelativeValueConfidence"/>.</summary>
    public const int MinBasketSize = 5;

    /// <summary>Одна строка вселенной для построения статистики корзин — только НЕ скрытые
    /// гигиеническим фильтром бумаги (мусор в медиану не пускать, см. план часть A.2).</summary>
    public sealed record BasketMember
    {
        public required string Secid { get; init; }
        public required string? Sector { get; init; }
        public required decimal? DurationYears { get; init; }
        public required decimal? GSpreadFraction { get; init; }
    }

    /// <summary>Ключ корзины — сектор × дюрационный бакет.</summary>
    public sealed record BasketKey
    {
        public required string Sector { get; init; }
        public required string DurationBucket { get; init; }
    }

    /// <summary>Статистика одной корзины по G-спреду её членов.</summary>
    public sealed record BasketStats
    {
        public required decimal Median { get; init; }
        public required decimal P25 { get; init; }
        public required decimal P75 { get; init; }
        public required int Count { get; init; }
    }

    /// <summary>
    /// Насколько надёжна статистика корзины (план часть A.3): High — родная корзина
    /// (сектор × дюрация) насчитывает ≥ <see cref="MinBasketSize"/> бумаг; Medium — пришлось
    /// откатиться на "весь сектор" (дюрация проигнорирована); Low — откатились на "весь рынок"
    /// (даже сектора целиком не хватило).
    /// </summary>
    public enum RelativeValueConfidence
    {
        High,
        Medium,
        Low,
    }

    /// <summary>Итог для одной бумаги — какую корзину использовали (после fallback) + её статистика.</summary>
    public sealed record BasketResolution
    {
        public required BasketKey EffectiveBasket { get; init; }
        public required BasketStats Stats { get; init; }
        public required RelativeValueConfidence Confidence { get; init; }
    }

    /// <summary>Оценка относительной дешевизны/дороговизны одной бумаги против её корзины.</summary>
    public sealed record RelativeValueAssessment
    {
        public required BasketResolution Basket { get; init; }

        /// <summary>deviationFraction = G-спред бумаги − медиана корзины (ДОЛЯ). Положительное —
        /// «дешевле» (спред выше медианы, рынок недооценивает/закладывает риск), отрицательное —
        /// «дороже» (спред ниже медианы, кандидат на продажу).</summary>
        public required decimal DeviationFraction { get; init; }

        /// <summary>Перцентиль бумаги внутри корзины (0-100) — доля членов корзины со спредом
        /// строго ниже данной бумаги, округлённая линейной интерполяцией по рангу.</summary>
        public required decimal Percentile { get; init; }
    }

    /// <summary>
    /// Строит статистику ВСЕХ корзин (сектор × дюрация) по не скрытым членам вселенной. Пустые
    /// сектор/дюрация трактуются как отдельные ключи ("Не определено" для дюрации — см.
    /// <see cref="DurationBucketClassifier.UnknownLabel"/>; null-сектор — "Без сектора"), не
    /// отбрасываются молча.
    /// </summary>
    public static Dictionary<BasketKey, BasketStats> BuildBasketStats(IReadOnlyList<BasketMember> members)
    {
        var withSpread = members.Where(m => m.GSpreadFraction is not null).ToList();

        var groups = withSpread
            .GroupBy(m => new BasketKey
            {
                Sector = string.IsNullOrWhiteSpace(m.Sector) ? UnknownSector : m.Sector!,
                DurationBucket = DurationBucketClassifier.Label(m.DurationYears),
            });

        var result = new Dictionary<BasketKey, BasketStats>();
        foreach (var group in groups)
        {
            var spreads = group.Select(m => m.GSpreadFraction!.Value).ToList();
            result[group.Key] = ComputeStats(spreads);
        }

        return result;
    }

    /// <summary>Ключ сектора, используемый, когда у бумаги банка нет значения sector (не должно
    /// происходить в норме — MOEX всегда отдаёт SECTYPE, но не отбрасываем молча при пробеле в данных).</summary>
    public const string UnknownSector = "Без сектора";

    /// <summary>
    /// Резолвит статистику корзины для бумаги с fallback-цепочкой (план часть A.3): родная корзина
    /// (сектор × дюрация) → весь сектор целиком (дюрация игнорируется, статистика по всем членам
    /// сектора независимо от дюрационного бакета) → весь рынок (все члены вселенной). Понижает
    /// confidence на каждом шаге отката.
    /// </summary>
    public static BasketResolution ResolveBasket(
        BasketKey key,
        IReadOnlyList<BasketMember> allMembers,
        Dictionary<BasketKey, BasketStats> basketStats)
    {
        if (basketStats.TryGetValue(key, out var nativeStats) && nativeStats.Count >= MinBasketSize)
        {
            return new BasketResolution { EffectiveBasket = key, Stats = nativeStats, Confidence = RelativeValueConfidence.High };
        }

        // Fallback 1: весь сектор (все дюрационные бакеты вместе).
        var sectorSpreads = allMembers
            .Where(m => m.GSpreadFraction is not null)
            .Where(m => string.Equals(
                string.IsNullOrWhiteSpace(m.Sector) ? UnknownSector : m.Sector,
                key.Sector, StringComparison.OrdinalIgnoreCase))
            .Select(m => m.GSpreadFraction!.Value)
            .ToList();

        if (sectorSpreads.Count >= MinBasketSize)
        {
            var sectorKey = new BasketKey { Sector = key.Sector, DurationBucket = SectorWideBucketLabel };
            return new BasketResolution
            {
                EffectiveBasket = sectorKey,
                Stats = ComputeStats(sectorSpreads),
                Confidence = RelativeValueConfidence.Medium,
            };
        }

        // Fallback 2: весь рынок.
        var marketSpreads = allMembers.Where(m => m.GSpreadFraction is not null).Select(m => m.GSpreadFraction!.Value).ToList();
        var marketKey = new BasketKey { Sector = MarketWideLabel, DurationBucket = MarketWideLabel };
        return new BasketResolution
        {
            EffectiveBasket = marketKey,
            Stats = ComputeStats(marketSpreads),
            Confidence = RelativeValueConfidence.Low,
        };
    }

    /// <summary>Метка дюрационного поля резолюции, когда откатились на "весь сектор" (дюрация не учитывается).</summary>
    public const string SectorWideBucketLabel = "Весь сектор";

    /// <summary>Метка и сектора, и дюрации, когда откатились на "весь рынок".</summary>
    public const string MarketWideLabel = "Весь рынок";

    /// <summary>
    /// Оценивает одну бумагу: резолвит корзину (с fallback) и считает deviation/перцентиль против
    /// её статистики. <paramref name="bondSpread"/> — точный спред для позиций портфеля,
    /// приближённый — для рыночных кандидатов (см. doc-comment класса про смешение единиц).
    /// </summary>
    public static RelativeValueAssessment Assess(
        string? sector,
        decimal? durationYears,
        decimal bondSpread,
        IReadOnlyList<BasketMember> allMembers,
        Dictionary<BasketKey, BasketStats> basketStats)
    {
        var key = new BasketKey
        {
            Sector = string.IsNullOrWhiteSpace(sector) ? UnknownSector : sector!,
            DurationBucket = DurationBucketClassifier.Label(durationYears),
        };

        var resolution = ResolveBasket(key, allMembers, basketStats);
        var deviation = bondSpread - resolution.Stats.Median;
        var percentile = ComputePercentile(bondSpread, resolution.EffectiveBasket, allMembers, resolution.Confidence);

        return new RelativeValueAssessment
        {
            Basket = resolution,
            DeviationFraction = deviation,
            Percentile = percentile,
        };
    }

    private static decimal ComputePercentile(
        decimal bondSpread, BasketKey effectiveBasket, IReadOnlyList<BasketMember> allMembers, RelativeValueConfidence confidence)
    {
        List<decimal> spreads;
        if (confidence == RelativeValueConfidence.High)
        {
            spreads = allMembers
                .Where(m => m.GSpreadFraction is not null)
                .Where(m => string.Equals(string.IsNullOrWhiteSpace(m.Sector) ? UnknownSector : m.Sector, effectiveBasket.Sector, StringComparison.OrdinalIgnoreCase))
                .Where(m => DurationBucketClassifier.Label(m.DurationYears) == effectiveBasket.DurationBucket)
                .Select(m => m.GSpreadFraction!.Value)
                .ToList();
        }
        else if (confidence == RelativeValueConfidence.Medium)
        {
            spreads = allMembers
                .Where(m => m.GSpreadFraction is not null)
                .Where(m => string.Equals(string.IsNullOrWhiteSpace(m.Sector) ? UnknownSector : m.Sector, effectiveBasket.Sector, StringComparison.OrdinalIgnoreCase))
                .Select(m => m.GSpreadFraction!.Value)
                .ToList();
        }
        else
        {
            spreads = allMembers.Where(m => m.GSpreadFraction is not null).Select(m => m.GSpreadFraction!.Value).ToList();
        }

        if (spreads.Count == 0) return 50m;

        var below = spreads.Count(s => s < bondSpread);
        return Math.Round(below * 100m / spreads.Count, 1);
    }

    /// <summary>Медиана/p25/p75 по НЕ пустому списку спредов (доли) — линейная интерполяция между
    /// соседними по рангу значениями (тот же метод, что большинство статистических пакетов
    /// используют по умолчанию, "linear interpolation" / Excel PERCENTILE.INC).</summary>
    private static BasketStats ComputeStats(List<decimal> spreads)
    {
        var sorted = spreads.OrderBy(s => s).ToList();
        return new BasketStats
        {
            Median = Percentile(sorted, 0.5m),
            P25 = Percentile(sorted, 0.25m),
            P75 = Percentile(sorted, 0.75m),
            Count = sorted.Count,
        };
    }

    private static decimal Percentile(List<decimal> sorted, decimal fraction)
    {
        if (sorted.Count == 0) return 0m;
        if (sorted.Count == 1) return sorted[0];

        var rank = fraction * (sorted.Count - 1);
        var lowerIndex = (int)Math.Floor(rank);
        var upperIndex = (int)Math.Ceiling(rank);
        if (lowerIndex == upperIndex) return sorted[lowerIndex];

        var weight = rank - lowerIndex;
        return sorted[lowerIndex] + (sorted[upperIndex] - sorted[lowerIndex]) * weight;
    }
}

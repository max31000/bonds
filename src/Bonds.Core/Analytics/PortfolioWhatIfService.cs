namespace Bonds.Core.Analytics;

/// <summary>
/// What-if превью портфеля «до/после покупки корзины» (plan/29 §B) — недостающее звено перед
/// будущей кнопкой «Купить»: как изменится ВЕСЬ портфель (не только сама корзина), если купить
/// строки, собранные <see cref="BasketBuilderService"/>. Вход — текущие holdings (стоимость,
/// доходность, дюрация, эмитент) + строки корзины; выход — суммарная стоимость, средневзвешенные
/// доходность/дюрация и топ-концентрации по эмитентам ДО и ПОСЛЕ, плюс предупреждения.
/// <para>
/// <b>Средневзвешенные метрики</b> — тот же принцип, что <see cref="BasketBuilderService"/>/
/// positionsAggregation.ts (задача 21): вес — рыночная стоимость, флоатеры/индексируемые исключены
/// из доходности (сноска <see cref="WhatIfSnapshot.HasExcludedFloaters"/>), но не из дюрации.
/// </para>
/// <para>
/// <b>Предупреждения</b> (plan/29 §B.1): (1) доля эмитента ПОСЛЕ покупки превышает
/// <c>maxConcentrationPercent</c> (дефолт — тот же, что у
/// <c>UserSettings.DefaultMaxConcentrationPercent</c>/аллокации/сигналов — 25%); (2) эмитент,
/// которого не было в портфеле (или была нулевая доля), после покупки превышает 25% — отдельная,
/// более узкая проверка "новый крупный эмитент" НЕЗАВИСИМО от общего лимита настройки (защита от
/// случая, когда пользователь намеренно поднял общий лимит выше 25%, но всё равно стоит предупредить
/// о разовом резком перекосе в бумагу, которой раньше не было).
/// </para>
/// Чистый сервис, без I/O.
/// </summary>
public static class PortfolioWhatIfService
{
    /// <summary>Дефолтный лимит доли эмитента — то же значение, что <see cref="CashAllocationService.DefaultMaxIssuerSharePercent"/>/<c>SignalEngineOptions.DefaultMaxConcentrationPercent</c>.</summary>
    public const decimal DefaultMaxConcentrationPercent = 25m;

    /// <summary>Порог "новый крупный эмитент" (plan/29 §B.1) — намеренно фиксированная константа 25%, не настраиваемая, независимая от <paramref name="maxConcentrationPercent"/> (см. doc-comment класса).</summary>
    private const decimal NewIssuerWarningThresholdPercent = 25m;

    public static PortfolioWhatIfResult Evaluate(
        IReadOnlyList<WhatIfHoldingInput> holdings,
        IReadOnlyList<BasketLine> basketLines,
        decimal maxConcentrationPercent = DefaultMaxConcentrationPercent)
    {
        var before = Snapshot(holdings
            .Select(h => (Issuer: h.Issuer, MarketValueRub: h.MarketValueRub, h.EffectiveYield, h.ModifiedDuration, h.IsFloater))
            .ToList());

        var bought = basketLines.Where(l => l.Quantity > 0m).ToList();
        var combined = holdings
            .Select(h => (Issuer: h.Issuer, MarketValueRub: h.MarketValueRub, h.EffectiveYield, h.ModifiedDuration, h.IsFloater))
            .Concat(bought.Select(l => (Issuer: l.Issuer, MarketValueRub: l.ActualCostRub, l.EffectiveYield, l.ModifiedDuration, l.IsFloater)))
            .ToList();
        var after = Snapshot(combined);

        var beforeByIssuer = GroupIssuerValue(holdings.Select(h => (Issuer: h.Issuer, MarketValueRub: h.MarketValueRub)).ToList());
        var afterByIssuer = GroupIssuerValue(combined.Select(c => (c.Issuer, c.MarketValueRub)).ToList());

        var allIssuers = beforeByIssuer.Keys.Union(afterByIssuer.Keys).ToList();
        var concentrations = allIssuers
            .Select(issuer =>
            {
                var beforeValue = beforeByIssuer.GetValueOrDefault(issuer, 0m);
                var afterValue = afterByIssuer.GetValueOrDefault(issuer, 0m);
                var beforeShare = before.TotalValueRub > 0m ? beforeValue / before.TotalValueRub * 100m : 0m;
                var afterShare = after.TotalValueRub > 0m ? afterValue / after.TotalValueRub * 100m : 0m;
                return new WhatIfConcentration
                {
                    Issuer = issuer,
                    SharePercentBefore = beforeShare,
                    SharePercentAfter = afterShare,
                };
            })
            .OrderByDescending(c => c.SharePercentAfter)
            .ToList();

        var warnings = new List<WhatIfWarning>();
        foreach (var c in concentrations)
        {
            if (c.SharePercentAfter > maxConcentrationPercent)
            {
                warnings.Add(new WhatIfWarning
                {
                    Kind = WhatIfWarningKind.ConcentrationLimitBreached,
                    Issuer = c.Issuer,
                    SharePercentAfter = c.SharePercentAfter,
                });
            }

            // "Новый крупный эмитент": не имел доли (или почти не имел) ДО, а ПОСЛЕ — выше фиксированного
            // порога 25% (независимо от maxConcentrationPercent, см. doc-comment).
            if (c.SharePercentBefore <= 0m && c.SharePercentAfter > NewIssuerWarningThresholdPercent)
            {
                warnings.Add(new WhatIfWarning
                {
                    Kind = WhatIfWarningKind.NewIssuerAboveThreshold,
                    Issuer = c.Issuer,
                    SharePercentAfter = c.SharePercentAfter,
                });
            }
        }

        return new PortfolioWhatIfResult
        {
            Before = before,
            After = after,
            Concentrations = concentrations,
            Warnings = warnings,
        };
    }

    private static Dictionary<string, decimal> GroupIssuerValue(IReadOnlyList<(string? Issuer, decimal MarketValueRub)> items)
    {
        return items
            .GroupBy(i => i.Issuer ?? "Не определено")
            .ToDictionary(g => g.Key, g => g.Sum(i => i.MarketValueRub));
    }

    private static WhatIfSnapshot Snapshot(
        IReadOnlyList<(string? Issuer, decimal MarketValueRub, decimal? EffectiveYield, decimal? ModifiedDuration, bool IsFloater)> items)
    {
        var totalValueRub = items.Sum(i => i.MarketValueRub);

        var yieldItems = items
            .Where(i => !i.IsFloater && i.EffectiveYield.HasValue)
            .Select(i => (Value: i.EffectiveYield!.Value, Weight: i.MarketValueRub))
            .ToList();

        var durationItems = items
            .Where(i => i.ModifiedDuration.HasValue)
            .Select(i => (Value: i.ModifiedDuration!.Value, Weight: i.MarketValueRub))
            .ToList();

        var hasExcludedFloaters = items.Any(i => i.IsFloater);

        return new WhatIfSnapshot
        {
            TotalValueRub = totalValueRub,
            WeightedYield = WeightedAverage(yieldItems),
            WeightedDuration = WeightedAverage(durationItems),
            HasExcludedFloaters = hasExcludedFloaters,
        };
    }

    private static decimal? WeightedAverage(IReadOnlyList<(decimal Value, decimal Weight)> items)
    {
        var totalWeight = items.Sum(i => i.Weight);
        if (totalWeight <= 0m) return null;
        var weightedSum = items.Sum(i => i.Value * i.Weight);
        return weightedSum / totalWeight;
    }
}

/// <summary>Один текущий holding портфеля — только поля, нужные для what-if (стоимость/доходность/дюрация/эмитент), сборка из <see cref="Bonds.Core.Models.PortfolioHolding"/> — ответственность вызывающего слоя.</summary>
public sealed record WhatIfHoldingInput
{
    public required ulong InstrumentId { get; init; }
    public string? Issuer { get; init; }
    public required decimal MarketValueRub { get; init; }
    public decimal? EffectiveYield { get; init; }
    public decimal? ModifiedDuration { get; init; }
    public bool IsFloater { get; init; }
}

/// <summary>Снимок метрик портфеля в один момент (до либо после покупки).</summary>
public sealed record WhatIfSnapshot
{
    public required decimal TotalValueRub { get; init; }
    public decimal? WeightedYield { get; init; }
    public decimal? WeightedDuration { get; init; }
    public required bool HasExcludedFloaters { get; init; }
}

/// <summary>Доля одного эмитента до и после покупки корзины.</summary>
public sealed record WhatIfConcentration
{
    public required string Issuer { get; init; }
    public required decimal SharePercentBefore { get; init; }
    public required decimal SharePercentAfter { get; init; }
}

public enum WhatIfWarningKind
{
    /// <summary>Доля эмитента после покупки превышает настроенный лимит концентрации.</summary>
    ConcentrationLimitBreached,

    /// <summary>Эмитента не было в портфеле (или доля была ~0), после покупки его доля превышает фиксированный порог 25%.</summary>
    NewIssuerAboveThreshold,
}

/// <summary>Одно предупреждение what-if — какой эмитент и какая доля после покупки.</summary>
public sealed record WhatIfWarning
{
    public required WhatIfWarningKind Kind { get; init; }
    public required string Issuer { get; init; }
    public required decimal SharePercentAfter { get; init; }
}

/// <summary>Результат what-if — снимки до/после, концентрации по эмитентам, предупреждения.</summary>
public sealed record PortfolioWhatIfResult
{
    public required WhatIfSnapshot Before { get; init; }
    public required WhatIfSnapshot After { get; init; }
    public required IReadOnlyList<WhatIfConcentration> Concentrations { get; init; }
    public required IReadOnlyList<WhatIfWarning> Warnings { get; init; }
}

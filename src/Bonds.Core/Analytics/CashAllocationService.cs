namespace Bonds.Core.Analytics;

/// <summary>
/// «Куда вложить сумму» (plan/17 §B) — жадное распределение свободных денег между текущими
/// позициями портфеля (НЕ скринер по всей вселенной бумаг — та же граница MVP, что у
/// <see cref="SwitchAnalysisService"/>, spec §3 «Вне скоупа»). Кандидаты сортируются по убыванию
/// эффективной доходности (YTM либо текущая купонная доходность для флоатера/индексируемой —
/// та же логика выбора, что в <see cref="PositionComparisonService"/>), и деньги идут в бумагу
/// с максимальной доходностью, пока не упрутся в лимит концентрации по эмитенту или в остаток
/// денег меньше цены одной бумаги. Чистый сервис, без I/O.
/// </summary>
public static class CashAllocationService
{
    /// <summary>Дефолтный лимит доли одного эмитента в портфеле после докупки (25% — тот же ориентир, что
    /// <c>SignalEngineOptions.DefaultMaxConcentrationPercent</c>; здесь отдельная константа, т.к. сервис
    /// не имеет доступа к Signals-слою, но значение намеренно совпадает).</summary>
    public const decimal DefaultMaxIssuerSharePercent = 25m;

    public const string Disclaimer =
        "Оценка распределения свободных средств по бумагам текущего портфеля (не скринер по всей " +
        "вселенной бумаг — сравниваются только позиции, которые уже есть в портфеле). Не учитывает " +
        "налоги и не является индивидуальной инвестиционной рекомендацией. Доходность — аналитическая " +
        "оценка (YTM либо текущая купонная доходность для флоатера/индексируемой бумаги), не гарантирована.";

    /// <summary>
    /// Распределяет <paramref name="amountRub"/> между <paramref name="candidates"/>.
    /// Алгоритм: сортировка кандидатов по убыванию <see cref="CashAllocationCandidate.EffectiveYield"/>
    /// (кандидаты без доходности — в конец, помечаются пропуском «нет доходности», не участвуют в
    /// распределении); для каждого по очереди докупаем максимум лотов, пока остаток эмитента в
    /// портфеле ПОСЛЕ покупки не превышает <paramref name="maxIssuerSharePercent"/> от суммы
    /// (текущая рыночная стоимость всего портфеля + вносимая сумма — база расширяется деньгами,
    /// которые физически входят в портфель); если лимит не позволяет купить ни одного лота —
    /// кандидат помечается пропуском «лимит концентрации» и распределение переходит к следующему.
    /// Остановка — остаток денег меньше цены самой дешёвой доступной бумаги среди кандидатов.
    /// </summary>
    public static CashAllocationResult Allocate(
        decimal amountRub,
        IReadOnlyList<CashAllocationCandidate> candidates,
        decimal currentPortfolioValueRub,
        decimal maxIssuerSharePercent = DefaultMaxIssuerSharePercent)
    {
        if (amountRub <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(amountRub), "Сумма должна быть положительной.");
        }

        var allocations = new List<CashAllocationLine>();
        var skipped = new List<CashAllocationSkip>();
        var leftover = amountRub;

        // Итоговая база для лимита концентрации растёт по мере того, как деньги физически входят
        // в портфель (иначе лимит считался бы от "старой" базы и позволял бы перекос сильнее заявленного).
        var portfolioValueAfterAllocation = currentPortfolioValueRub;
        var issuerValueAfterAllocation = candidates
            .Where(c => c.Issuer is not null)
            .GroupBy(c => c.Issuer)
            .ToDictionary(g => g.Key!, g => g.Sum(c => c.CurrentIssuerMarketValueRub));

        var ordered = candidates
            .OrderByDescending(c => c.EffectiveYield.HasValue)
            .ThenByDescending(c => c.EffectiveYield)
            .ToList();

        foreach (var candidate in ordered)
        {
            if (candidate.EffectiveYield is null)
            {
                skipped.Add(new CashAllocationSkip
                {
                    InstrumentId = candidate.InstrumentId,
                    Name = candidate.Name,
                    Issuer = candidate.Issuer,
                    Reason = CashAllocationSkipReason.NoYield,
                });
                continue;
            }

            if (candidate.PricePerLotRub <= 0m)
            {
                skipped.Add(new CashAllocationSkip
                {
                    InstrumentId = candidate.InstrumentId,
                    Name = candidate.Name,
                    Issuer = candidate.Issuer,
                    Reason = CashAllocationSkipReason.NoPrice,
                });
                continue;
            }

            var issuerKey = candidate.Issuer ?? $"__instrument_{candidate.InstrumentId}";
            var issuerValue = issuerValueAfterAllocation.TryGetValue(issuerKey, out var v) ? v : 0m;

            var quantity = 0m;
            var costRub = 0m;

            while (true)
            {
                var nextCostRub = costRub + candidate.PricePerLotRub;
                if (nextCostRub > leftover) break; // не хватает денег на ещё один лот

                var newPortfolioValue = portfolioValueAfterAllocation + candidate.PricePerLotRub;
                var newIssuerValue = issuerValue + costRub + candidate.PricePerLotRub;
                var newIssuerSharePercent = newPortfolioValue > 0m
                    ? newIssuerValue / newPortfolioValue * 100m
                    : 0m;

                if (newIssuerSharePercent > maxIssuerSharePercent) break; // лимит концентрации

                costRub = nextCostRub;
                quantity += candidate.LotSize;
                portfolioValueAfterAllocation = newPortfolioValue;
            }

            if (quantity > 0m)
            {
                leftover -= costRub;
                issuerValueAfterAllocation[issuerKey] = issuerValue + costRub;

                // Задача 24: разложение costRub (вся докупка) на компоненты — тот же множитель
                // "число купленных лотов", что и у costRub относительно PricePerLotRub.
                var lotsBought = candidate.LotSize > 0m ? quantity / candidate.LotSize : 0m;

                allocations.Add(new CashAllocationLine
                {
                    InstrumentId = candidate.InstrumentId,
                    Name = candidate.Name,
                    Issuer = candidate.Issuer,
                    Quantity = quantity,
                    EstimatedCostRub = costRub,
                    EffectiveYield = candidate.EffectiveYield.Value,
                    LotSizeAssumed = candidate.LotSizeIsAssumed,
                    CleanCostRub = candidate.CleanPriceRub * lotsBought,
                    AccruedCostRub = candidate.AccruedRub * lotsBought,
                    CommissionCostRub = candidate.CommissionRub * lotsBought,
                });
            }
            else
            {
                skipped.Add(new CashAllocationSkip
                {
                    InstrumentId = candidate.InstrumentId,
                    Name = candidate.Name,
                    Issuer = candidate.Issuer,
                    Reason = CashAllocationSkipReason.ConcentrationLimit,
                });
            }
        }

        return new CashAllocationResult
        {
            AmountRub = amountRub,
            Allocations = allocations,
            Skipped = skipped,
            LeftoverRub = leftover,
            Disclaimer = Disclaimer,
        };
    }
}

/// <summary>Один кандидат на докупку — текущая позиция портфеля (spec §3 — не вселенная бумаг).</summary>
public sealed record CashAllocationCandidate
{
    public required ulong InstrumentId { get; init; }
    public string? Name { get; init; }
    public string? Issuer { get; init; }

    /// <summary>YTM либо CurrentYield (для флоатера/индексируемой) — см. <see cref="PositionComparisonService"/>. Null — доходность не определена, кандидат пропускается.</summary>
    public decimal? EffectiveYield { get; init; }

    /// <summary>Грязная цена (цена + НКД) одного лота с учётом комиссии покупки (<see cref="SwitchAnalysisService.DefaultCommissionRate"/> либо переопределённая ставка) — сколько рублей стоит купить <see cref="LotSize"/> штук.</summary>
    public required decimal PricePerLotRub { get; init; }

    /// <summary>
    /// Задача 24: разложение <see cref="PricePerLotRub"/> на компоненты (чистая цена + НКД + комиссия)
    /// для одного лота — только для отображения в UI («купить N шт × цена (чистая + НКД + комиссия)»),
    /// не участвует в расчёте распределения (тот использует только <see cref="PricePerLotRub"/>).
    /// Сумма CleanPriceRub + AccruedRub + CommissionRub должна равняться PricePerLotRub.
    /// </summary>
    public decimal CleanPriceRub { get; init; }

    /// <summary>Задача 24: НКД в составе цены одного лота — см. <see cref="CleanPriceRub"/>.</summary>
    public decimal AccruedRub { get; init; }

    /// <summary>Задача 24: комиссия покупки в составе цены одного лота — см. <see cref="CleanPriceRub"/>.</summary>
    public decimal CommissionRub { get; init; }

    /// <summary>Размер лота в штуках. У большинства корп. облигаций 1 — если источник (Instrument/T-Invest) не отдаёт лот, берётся 1 и помечается <see cref="LotSizeIsAssumed"/>.</summary>
    public required decimal LotSize { get; init; }

    /// <summary>true — <see cref="LotSize"/> не взят из данных инструмента, а принят равным 1 по умолчанию (см. doc-comment сервиса/плана).</summary>
    public bool LotSizeIsAssumed { get; init; }

    /// <summary>Текущая рыночная стоимость позиций этого эмитента в портфеле (для базы лимита концентрации). 0, если эмитента ещё нет в портфеле.</summary>
    public required decimal CurrentIssuerMarketValueRub { get; init; }
}

/// <summary>Итог распределения суммы — что купить, остаток, и что пропущено (spec §9, обязательный дисклеймер).</summary>
public sealed record CashAllocationResult
{
    public required decimal AmountRub { get; init; }
    public required IReadOnlyList<CashAllocationLine> Allocations { get; init; }
    public required IReadOnlyList<CashAllocationSkip> Skipped { get; init; }
    public required decimal LeftoverRub { get; init; }
    public required string Disclaimer { get; init; }
}

/// <summary>Одна строка распределения — сколько купить конкретной бумаги.</summary>
public sealed record CashAllocationLine
{
    public required ulong InstrumentId { get; init; }
    public string? Name { get; init; }
    public string? Issuer { get; init; }
    public required decimal Quantity { get; init; }
    public required decimal EstimatedCostRub { get; init; }
    public required decimal EffectiveYield { get; init; }

    /// <summary>true — размер лота принят равным 1 по умолчанию (источник не предоставил лот).</summary>
    public required bool LotSizeAssumed { get; init; }

    /// <summary>
    /// Задача 24: разложение EstimatedCostRub на всю докупку (не на 1 лот) — чистая цена всех купленных
    /// бумаг. CleanCostRub + AccruedCostRub + CommissionCostRub = EstimatedCostRub (с точностью до
    /// округления). 0, если у кандидата не было разложения (см. <see cref="CashAllocationCandidate.CleanPriceRub"/>).
    /// </summary>
    public decimal CleanCostRub { get; init; }

    /// <summary>Задача 24: НКД в составе EstimatedCostRub — см. <see cref="CleanCostRub"/>.</summary>
    public decimal AccruedCostRub { get; init; }

    /// <summary>Задача 24: комиссия покупки в составе EstimatedCostRub — см. <see cref="CleanCostRub"/>.</summary>
    public decimal CommissionCostRub { get; init; }
}

/// <summary>Почему кандидат пропущен и не получил докупку.</summary>
public enum CashAllocationSkipReason
{
    /// <summary>Нет применимой доходности (YTM не сошёлся и не флоатер/индексируемая с CurrentYield).</summary>
    NoYield,

    /// <summary>Лимит концентрации по эмитенту не позволяет купить ни одного лота.</summary>
    ConcentrationLimit,

    /// <summary>Цена лота не определена (нет котировки).</summary>
    NoPrice,
}

/// <summary>Кандидат, не получивший докупку, и причина.</summary>
public sealed record CashAllocationSkip
{
    public required ulong InstrumentId { get; init; }
    public string? Name { get; init; }
    public string? Issuer { get; init; }
    public required CashAllocationSkipReason Reason { get; init; }
}

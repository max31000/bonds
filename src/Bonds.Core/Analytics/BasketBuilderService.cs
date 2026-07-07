namespace Bonds.Core.Analytics;

/// <summary>
/// Конструктор корзины (plan/29 §A) — «собрал корзину процентами → штуки/стоимость», в отличие от
/// <see cref="CashAllocationService"/> (жадный алгоритм без участия пользователя). Вход — сумма +
/// строки с целевым весом (доля 0..1, Σ ≤ 1 — недобор остаётся деньгами); на каждую строку —
/// готовая цена лота (грязная + комиссия, тот же паттерн, что <see cref="CashAllocationCandidate"/>
/// в CashAllocationService/эндпоинт GetAllocation).
/// <para>
/// <b>Алгоритм (намеренно простой, БЕЗ жадного перераспределения):</b> целевые рубли на строку =
/// amount × вес; штук = floor(целевые / цена лота). Недоиспользованные рубли КАЖДОЙ строки
/// (target − actual) и весь неучтённый вес (1 − Σ весов) складываются в один <see cref="LeftoverRub"/>
/// — деньги НЕ перетекают из одной строки корзины в другую. Это осознанный выбор (plan/29 §A):
/// пользователь сам расставил проценты — предсказуемость важнее оптимальности упаковки лотов;
/// если бы остаток одной бумаги "доливался" в следующую по списку, результат перестал бы быть
/// прозрачным (порядок строк начал бы влиять на исход), и повторный расчёт с той же корзиной после
/// небольшого изменения цены дал бы неожиданно другое распределение. Кто хочет максимизировать
/// использование суммы — может сам подвинуть проценты и пересчитать.
/// </para>
/// <para>
/// <b>Метрики корзины:</b> средневзвешенные (вес — фактическая стоимость строки, ActualCostRub)
/// доходность и дюрация. Флоатеры/индексируемые исключаются из средневзвешенной доходности —
/// тот же принцип, что "Итого" в задаче 21 (positionsAggregation.ts: currentYield несравним с YTM),
/// сноска <see cref="BasketMetrics.HasExcludedFloaters"/> должна отображаться в UI. Дюрация не имеет
/// этой проблемы (определена и для флоатеров) — учитывается по всем строкам, где известна.
/// Строки с нулевым количеством (не хватило денег на долю) не участвуют в весах вовсе.
/// </para>
/// Чистый сервис, без I/O.
/// </summary>
public static class BasketBuilderService
{
    public const string Disclaimer =
        "Расчёт корзины по заданным вами процентам — сколько штук купить на введённую сумму с учётом " +
        "лотов, НКД и комиссии покупки. Остатки по каждой бумаге (когда доля не кратна цене лота) не " +
        "перераспределяются между другими строками корзины — это отражается в общем остатке деньгами. " +
        "Средневзвешенная доходность считается без флоатеров/индексируемых бумаг (их текущая купонная " +
        "доходность несравнима с YTM). Не является индивидуальной инвестиционной рекомендацией.";

    /// <summary>Максимально допустимая сумма долей строк (небольшой допуск на погрешность округления при передаче с фронта, как и в валидации эндпоинта).</summary>
    private const decimal MaxTotalWeightFraction = 1.0001m;

    public static BasketBuildResult Build(decimal amountRub, IReadOnlyList<BasketLineInput> lines)
    {
        if (amountRub <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(amountRub), "Сумма должна быть положительной.");
        }

        var totalWeight = lines.Sum(l => l.TargetWeightFraction);
        if (totalWeight > MaxTotalWeightFraction)
        {
            throw new ArgumentException($"Сумма весов строк ({totalWeight:0.####}) не может превышать 1.", nameof(lines));
        }

        var resultLines = new List<BasketLine>(lines.Count);
        var leftoverRub = amountRub;

        foreach (var line in lines)
        {
            var targetRub = amountRub * line.TargetWeightFraction;
            var quantity = 0m;
            var actualCostRub = 0m;

            if (line.PricePerLotRub > 0m)
            {
                var lots = Math.Floor(targetRub / line.PricePerLotRub);
                if (lots > 0m)
                {
                    quantity = lots * line.LotSize;
                    actualCostRub = lots * line.PricePerLotRub;
                }
            }

            leftoverRub -= actualCostRub;

            resultLines.Add(new BasketLine
            {
                InstrumentId = line.InstrumentId,
                Name = line.Name,
                Issuer = line.Issuer,
                TargetWeightFraction = line.TargetWeightFraction,
                ActualWeightFraction = amountRub > 0m ? actualCostRub / amountRub : 0m,
                Quantity = quantity,
                ActualCostRub = actualCostRub,
                EffectiveYield = line.EffectiveYield,
                ModifiedDuration = line.ModifiedDuration,
                IsFloater = line.IsFloater,
                LotSizeAssumed = line.LotSizeIsAssumed,
                CleanCostRub = quantity > 0m ? line.CleanPriceRub * (quantity / (line.LotSize == 0m ? 1m : line.LotSize)) : 0m,
                AccruedCostRub = quantity > 0m ? line.AccruedRub * (quantity / (line.LotSize == 0m ? 1m : line.LotSize)) : 0m,
                CommissionCostRub = quantity > 0m ? line.CommissionRub * (quantity / (line.LotSize == 0m ? 1m : line.LotSize)) : 0m,
            });
        }

        var metrics = ComputeMetrics(resultLines);

        return new BasketBuildResult
        {
            AmountRub = amountRub,
            Lines = resultLines,
            LeftoverRub = leftoverRub,
            Metrics = metrics,
            Disclaimer = Disclaimer,
        };
    }

    private static BasketMetrics ComputeMetrics(IReadOnlyList<BasketLine> lines)
    {
        var bought = lines.Where(l => l.Quantity > 0m).ToList();
        var totalCostRub = bought.Sum(l => l.ActualCostRub);

        var yieldItems = bought
            .Where(l => !l.IsFloater && l.EffectiveYield.HasValue)
            .Select(l => (Value: l.EffectiveYield!.Value, Weight: l.ActualCostRub))
            .ToList();

        var durationItems = bought
            .Where(l => l.ModifiedDuration.HasValue)
            .Select(l => (Value: l.ModifiedDuration!.Value, Weight: l.ActualCostRub))
            .ToList();

        var hasExcludedFloaters = bought.Any(l => l.IsFloater);

        return new BasketMetrics
        {
            TotalCostRub = totalCostRub,
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

/// <summary>Один вход конструктора корзины — бумага + целевой вес (доля 0..1) + готовая цена лота/метрики (собираются вызывающим слоем из holdings, тот же паттерн, что CashAllocationCandidate).</summary>
public sealed record BasketLineInput
{
    public required ulong InstrumentId { get; init; }
    public string? Name { get; init; }
    public string? Issuer { get; init; }

    /// <summary>Целевая доля суммы корзины (0..1) — НЕ проценты (конвенция репо: бэкенд — доли).</summary>
    public required decimal TargetWeightFraction { get; init; }

    /// <summary>Грязная цена (цена + НКД) одного лота с учётом комиссии покупки — сколько рублей стоит купить <see cref="LotSize"/> штук.</summary>
    public required decimal PricePerLotRub { get; init; }

    /// <summary>Разложение <see cref="PricePerLotRub"/> на компоненты (чистая цена + НКД + комиссия) одного лота — только для UI (задача 24 паттерн), сумма равна PricePerLotRub.</summary>
    public decimal CleanPriceRub { get; init; }
    public decimal AccruedRub { get; init; }
    public decimal CommissionRub { get; init; }

    public required decimal LotSize { get; init; }
    public bool LotSizeIsAssumed { get; init; }

    /// <summary>YTM либо CurrentYield для флоатера/индексируемой — null, если доходность не определена (строка просто не попадёт в средневзвешенную доходность корзины).</summary>
    public decimal? EffectiveYield { get; init; }

    public decimal? ModifiedDuration { get; init; }

    /// <summary>true — исключить эту строку из средневзвешенной доходности корзины (см. doc-comment сервиса).</summary>
    public bool IsFloater { get; init; }
}

/// <summary>Результат сборки корзины — строки со штуками/стоимостью, остаток деньгами, метрики корзины.</summary>
public sealed record BasketBuildResult
{
    public required decimal AmountRub { get; init; }
    public required IReadOnlyList<BasketLine> Lines { get; init; }

    /// <summary>Сумма НЕ распределённая по строкам — недобор весов (Σ &lt; 1) + округление лотов каждой строки (см. doc-comment сервиса про отсутствие жадного перераспределения).</summary>
    public required decimal LeftoverRub { get; init; }

    public required BasketMetrics Metrics { get; init; }
    public required string Disclaimer { get; init; }
}

/// <summary>Одна строка собранной корзины.</summary>
public sealed record BasketLine
{
    public required ulong InstrumentId { get; init; }
    public string? Name { get; init; }
    public string? Issuer { get; init; }

    public required decimal TargetWeightFraction { get; init; }

    /// <summary>Фактический вес после округления до целых лотов = ActualCostRub / amountRub (может быть меньше целевого — см. doc-comment сервиса).</summary>
    public required decimal ActualWeightFraction { get; init; }

    public required decimal Quantity { get; init; }
    public required decimal ActualCostRub { get; init; }

    public decimal? EffectiveYield { get; init; }
    public decimal? ModifiedDuration { get; init; }
    public bool IsFloater { get; init; }
    public required bool LotSizeAssumed { get; init; }

    /// <summary>Разложение ActualCostRub (задача 24 паттерн) — CleanCostRub + AccruedCostRub + CommissionCostRub = ActualCostRub.</summary>
    public decimal CleanCostRub { get; init; }
    public decimal AccruedCostRub { get; init; }
    public decimal CommissionCostRub { get; init; }
}

/// <summary>Метрики корзины — средневзвешенные по фактической стоимости строк.</summary>
public sealed record BasketMetrics
{
    public required decimal TotalCostRub { get; init; }

    /// <summary>Средневзвешенная доходность (флоатеры/индексируемые исключены) — null, если ни одна купленная строка не даёт сравнимую доходность.</summary>
    public decimal? WeightedYield { get; init; }

    /// <summary>Средневзвешенная модифицированная дюрация — null, если дюрация нигде не известна.</summary>
    public decimal? WeightedDuration { get; init; }

    /// <summary>true — хотя бы одна купленная строка-флоатер/индексируемая была исключена из WeightedYield (сноска в UI).</summary>
    public required bool HasExcludedFloaters { get; init; }
}

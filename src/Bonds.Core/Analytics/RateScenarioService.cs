namespace Bonds.Core.Analytics;

public static class RateScenarioService
{
    // Default shift grid in basis points
    public static readonly int[] DefaultShiftsBp = [-200, -100, -50, 0, 50, 100, 200];

    /// <summary>
    /// Computes portfolio value under parallel rate shifts.
    /// Δy in fractions = shiftBp / 10000.
    /// ΔP/P ≈ −ModDur·Δy + 0.5·Convexity·Δy² (omit 2nd term if Convexity is null).
    /// </summary>
    public static IReadOnlyList<RateScenarioPoint> Compute(
        IEnumerable<PortfolioHolding> holdings,
        IReadOnlyList<int> shiftsBp)
    {
        var holdingsList = holdings.ToList();

        // H-1/M-1: база — ВЕСЬ портфель (как и CurrentValueRub в эндпоинте), чтобы выполнялся
        // инвариант NewValue == CurrentValue + Δ. Чувствительность к ставке — только у позиций
        // с дюрацией (у флоатеров/бумаг без дюрации вклад в Δ = 0, цена при сдвиге не меняется).
        var currentValue = holdingsList.Sum(h => h.MarketValueRub);
        var sensitive = holdingsList
            .Where(h => h.ModifiedDuration.HasValue && h.MarketValueRub > 0)
            .ToList();

        return shiftsBp.Select(shiftBp =>
        {
            var deltaY = shiftBp / 10_000m;
            var deltaRub = sensitive.Sum(h =>
            {
                var durationEffect = -(h.ModifiedDuration!.Value) * deltaY;
                var convexityEffect = h.Convexity.HasValue
                    ? 0.5m * h.Convexity.Value * deltaY * deltaY
                    : 0m;
                return (durationEffect + convexityEffect) * h.MarketValueRub;
            });
            var newValue = currentValue + deltaRub;
            var deltaPercent = currentValue != 0 ? deltaRub / currentValue * 100m : 0m;

            return new RateScenarioPoint
            {
                ShiftBp = shiftBp,
                NewValueRub = newValue,
                DeltaRub = deltaRub,
                DeltaPercent = deltaPercent,
            };
        }).ToList();
    }

    /// <summary>
    /// Процентно-чувствительная часть портфеля — сумма рыночной стоимости позиций, у которых есть
    /// модифицированная дюрация (только они дают вклад в Δ при сдвиге кривой). Эндпоинт отдаёт это
    /// как <c>RateSensitiveValueRub</c>, чтобы фронт честно подписал «X из Y подвержено сдвигу».
    /// </summary>
    public static decimal RateSensitiveValue(IEnumerable<PortfolioHolding> holdings) =>
        holdings.Where(h => h.ModifiedDuration.HasValue && h.MarketValueRub > 0)
            .Sum(h => h.MarketValueRub);
}

public sealed record RateScenarioPoint
{
    public required int ShiftBp { get; init; }
    public required decimal NewValueRub { get; init; }
    public required decimal DeltaRub { get; init; }

    /// <summary>
    /// Audit(portfolio) P-1: В ПРОЦЕНТАХ (0-100), НЕ в долях — осознанное исключение из
    /// конвенции репо «бэкенд отдаёт доли, ×100 делает фронт». Фронт (Analytics.tsx) читает это
    /// поле напрямую через <c>.toFixed(2)</c>, БЕЗ <c>formatPercent</c> (который бы ×100'ил
    /// повторно). Регрессия закреплена тестом <c>DeltaPercent_IsAlreadyInPercentUnits_NotFraction</c>.
    /// Если когда-нибудь меняете один конец этой пары (бэк или фронт) — обязательно меняйте оба.
    /// </summary>
    public required decimal DeltaPercent { get; init; }
}

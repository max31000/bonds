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
        var positions = holdings
            .Where(h => h.ModifiedDuration.HasValue && h.MarketValueRub > 0)
            .ToList();

        var currentValue = positions.Sum(h => h.MarketValueRub);

        return shiftsBp.Select(shiftBp =>
        {
            var deltaY = shiftBp / 10_000m;
            var newValue = currentValue + positions.Sum(h =>
            {
                var durationEffect = -(h.ModifiedDuration!.Value) * deltaY;
                var convexityEffect = h.Convexity.HasValue
                    ? 0.5m * h.Convexity.Value * deltaY * deltaY
                    : 0m;
                return (durationEffect + convexityEffect) * h.MarketValueRub;
            });
            var deltaRub = newValue - currentValue;
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
}

public sealed record RateScenarioPoint
{
    public required int ShiftBp { get; init; }
    public required decimal NewValueRub { get; init; }
    public required decimal DeltaRub { get; init; }
    public required decimal DeltaPercent { get; init; }
}

namespace Bonds.Core.Analytics;

/// <summary>
/// Единая конвенция «доходность после налога» (задачи 48 и 50): годовая доходность к worst
/// умножается на <c>(1 − НДФЛ 13%)</c>. Упрощение — налог применяется ко ВСЕЙ доходности (купон + дисконт),
/// без льгот и прогрессии; отрицательная доходность налога не создаёт. Используется анализом замены
/// (<see cref="SwitchAnalysisService"/>) и ранжированием кандидатов (<see cref="CandidateYieldRanking"/>) —
/// ставка берётся из одного источника, <see cref="SaleTaxEstimator.NdflRate"/>.
/// </summary>
public static class AfterTaxYield
{
    /// <summary>Ставка НДФЛ, ДОЛЯ (0.13 = 13%) — тот же источник, что налог продажи.</summary>
    public const decimal TaxRate = SaleTaxEstimator.NdflRate;

    /// <summary>ДОЛЯ → ДОЛЯ: положительная доходность ×(1 − <see cref="TaxRate"/>), отрицательная без изменений.</summary>
    public static decimal Apply(decimal yieldFraction) =>
        yieldFraction > 0m ? yieldFraction * (1m - TaxRate) : yieldFraction;
}

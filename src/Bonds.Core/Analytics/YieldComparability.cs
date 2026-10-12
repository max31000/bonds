namespace Bonds.Core.Analytics;

/// <summary>
/// Единое правило «сравнимости по доходности»: плавающий купон, индексируемый номинал или неполные
/// данные делают эффективную доходность несопоставимой с YTM фиксированного купона.
/// Используется сравнением позиций, замены, relative-value и фронтовым isOutOfComparison.
/// </summary>
public static class YieldComparability
{
    /// <summary>true — бумагу можно сравнивать по доходности с фикс-купонными аналогами.</summary>
    public static bool IsComparable(bool isFloater, bool isIndexed, bool dataIncomplete) =>
        !isFloater && !isIndexed && !dataIncomplete;
}

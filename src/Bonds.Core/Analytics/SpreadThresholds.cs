namespace Bonds.Core.Analytics;

/// <summary>Общие пороги по спреду для relative-value вердикта и риск-сигналов кандидата.</summary>
public static class SpreadThresholds
{
    /// <summary>
    /// Порог |отклонение G-спреда от медианы корзины| — ДОЛЯ (0.0020 = 20 б.п.). Меньше — «в рынке»
    /// (Fair в RV-вердикте), больше — Cheap/Rich (RV) и Caution/Good (риск-сигнал спреда кандидата).
    /// Единственный источник значения: RV-вердикт и сигналы кандидата обязаны быть согласованы.
    /// </summary>
    public const decimal DeviationFraction = 0.0020m;
}

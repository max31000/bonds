namespace Bonds.Core.Analytics;

/// <summary>Общие пороги по спреду для relative-value вердикта и риск-сигналов кандидата.</summary>
public static class SpreadThresholds
{
    /// <summary>
    /// НИЖНЯЯ планка порога |отклонение G-спреда от медианы корзины| — ДОЛЯ (0.0020 = 20 б.п.).
    /// Фактический порог корзины — <see cref="ForBasket"/> = max(эта планка, <see cref="IqrMultiplier"/> × IQR
    /// корзины): меньше — «в рынке» (Fair в RV-вердикте), больше — Cheap/Rich (RV) и Caution/Good
    /// (риск-сигнал спреда кандидата). Единственный источник значения планки: RV-вердикт и сигналы
    /// кандидата обязаны быть согласованы.
    /// </summary>
    public const decimal DeviationFraction = 0.0020m;

    /// <summary>
    /// Доля межквартильного размаха корзины (IQR = P75 − P25, ДОЛЯ), которую считаем «шумом»:
    /// порог вердикта = max(<see cref="DeviationFraction"/>, 0.25 × IQR). Эвристика до калибровки на
    /// истории (BACKLOG О2) — не откалибрована.
    /// </summary>
    public const decimal IqrMultiplier = 0.25m;

    /// <summary>
    /// Порог вердикта/сигнала спреда для корзины со статистикой <paramref name="p25"/>/<paramref name="p75"/>
    /// (ДОЛИ G-спреда): max(<see cref="DeviationFraction"/>, <see cref="IqrMultiplier"/> × (P75 − P25)).
    /// Эталон: IQR = 10 б.п. → 0.25 × 10 = 2.5 б.п. &lt; 20 б.п. → порог 20 б.п.; IQR = 200 б.п. → 50 б.п.
    /// Отрицательный IQR (P75 &lt; P25 — невозможно при корректной статистике) трактуется как 0.
    /// </summary>
    public static decimal ForBasket(decimal p25, decimal p75) =>
        Math.Max(DeviationFraction, IqrMultiplier * Math.Max(0m, p75 - p25));
}

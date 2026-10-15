namespace Bonds.Infrastructure.Universe;

/// <summary>
/// Задача 43: условие «суточного» шага обогащения банка (эмитенты, карточки бумаг) — выполняется
/// не в каждом часовом цикле, а один раз в московские сутки на первом тике после начала торгового окна
/// (<see cref="BondUniverseRefreshOptions.TradingWindowStartMsk"/>, 09:50 МСК).
/// </summary>
public static class DailyEnrichmentGate
{
    /// <summary>
    /// True, если в московскую дату <paramref name="nowMsk"/> шаг ещё не выполнялся
    /// (<paramref name="lastRunDateMsk"/> ≠ сегодня) и время уже не раньше <paramref name="startMsk"/>.
    /// </summary>
    public static bool IsDue(DateTime nowMsk, DateOnly? lastRunDateMsk, TimeOnly startMsk) =>
        lastRunDateMsk != DateOnly.FromDateTime(nowMsk) && TimeOnly.FromDateTime(nowMsk) >= startMsk;
}

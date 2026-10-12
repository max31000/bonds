namespace Bonds.Core.Signals;

/// <summary>
/// Настраиваемые параметры правил событий. Это ДЕФОЛТЫ: пороги из
/// <see cref="Bonds.Core.Models.UserSettings"/> (страница «Настройки») имеют приоритет и накладываются
/// поверх (см. <see cref="WithOverrides"/>). Секция конфига "Signals".
/// </summary>
public sealed class SignalEngineOptions
{
    /// <summary>
    /// За сколько дней до даты события (оферта/погашение/амортизация/пересчёт ставки флоатера)
    /// показывать его как «приближается». Один порог для всех типов событий.
    /// </summary>
    public int UpcomingEventDaysThreshold { get; set; } = 14;

    /// <summary>
    /// Порог свободных денег на счёте (<c>accounts.cash_rub</c>, РУБЛИ), выше которого появляется
    /// напоминание о реинвесте (Info, состояние).
    /// </summary>
    public decimal UninvestedCashThresholdRub { get; set; } = 10_000m;

    /// <summary>
    /// Дефолтный лимит концентрации по эмитенту в ПРОЦЕНТАХ (единственное «процентное» поле опций —
    /// сравнивается с <see cref="Bonds.Core.Analytics.PortfolioCompositionService"/> SharePercent, тоже
    /// в процентах). Применяется, если для эмитента нет записи
    /// <see cref="Bonds.Core.Models.TargetAllocation.MaxConcentrationPercent"/>.
    /// </summary>
    public decimal DefaultMaxConcentrationPercent { get; set; } = 25m;

    /// <summary>
    /// Накладывает пользовательские пороги (null = «не задан, оставить дефолт») на дефолты этого
    /// экземпляра. Возвращает НОВЫЙ объект — singleton-опции не мутируются.
    /// </summary>
    public SignalEngineOptions WithOverrides(int? upcomingEventDays, decimal? uninvestedCashRub, decimal? maxConcentrationPercent) =>
        new()
        {
            UpcomingEventDaysThreshold = upcomingEventDays ?? UpcomingEventDaysThreshold,
            UninvestedCashThresholdRub = uninvestedCashRub ?? UninvestedCashThresholdRub,
            DefaultMaxConcentrationPercent = maxConcentrationPercent ?? DefaultMaxConcentrationPercent,
        };
}

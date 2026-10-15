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
    /// Задача 49: порог события <see cref="Bonds.Core.Models.SignalType.SpreadWidening"/> — расширение эффективного
    /// G-спреда бумаги сегодня к МЕДИАНЕ её же спреда за 20 торговых дней. ДОЛЯ (0.0030 = 30 б.п.), условие строгое «&gt;».
    /// <para>
    /// Ступенчатость порогов: 30 б.п. — раннее ПРЕДУПРЕЖДЕНИЕ (Warning-событие в «Событиях», блокирует «докупить», но не меняет
    /// светофор), 50 б.п. (<c>ReliabilityScoreOptions.SpreadMomentumShockFraction</c>) — критический флаг <c>SpreadShock</c>
    /// скоркарты, который краснит светофор и ведёт к вердикту «рассмотреть выход». Если бы событие стояло на 50 б.п., оно
    /// срабатывало бы одновременно с критическим флагом и не давало времени заметить расширение заранее.
    /// </para>
    /// </summary>
    public decimal SpreadWideningThresholdFraction { get; set; } = 0.0030m;

    /// <summary>
    /// Задача 49: порог события <see cref="Bonds.Core.Models.SignalType.PriceDrawdown"/> — просадка чистой цены
    /// от максимума за 20 торговых дней. ДОЛЯ (0.05 = 5%), условие строгое «&gt;».
    /// </summary>
    public decimal PriceDrawdownThresholdFraction { get; set; } = 0.05m;

    /// <summary>
    /// Минимум торговых дней истории для рыночных событий: меньше — не генерировать (шум на короткой выборке).
    /// Совпадает с порогом скоркарты (<see cref="Bonds.Core.Analytics.ReliabilityScoreOptions.MinHistoryDays"/>).
    /// </summary>
    public int MarketHistoryMinDays { get; set; } = Bonds.Core.Analytics.ReliabilityScoreOptions.MinHistoryDays;

    /// <summary>
    /// Накладывает пользовательские пороги (null = «не задан, оставить дефолт») на дефолты этого
    /// экземпляра. Возвращает НОВЫЙ объект — singleton-опции не мутируются.
    /// </summary>
    public SignalEngineOptions WithOverrides(
        int? upcomingEventDays,
        decimal? uninvestedCashRub,
        decimal? maxConcentrationPercent,
        decimal? spreadWideningFraction = null,
        decimal? priceDrawdownFraction = null) =>
        new()
        {
            UpcomingEventDaysThreshold = upcomingEventDays ?? UpcomingEventDaysThreshold,
            UninvestedCashThresholdRub = uninvestedCashRub ?? UninvestedCashThresholdRub,
            DefaultMaxConcentrationPercent = maxConcentrationPercent ?? DefaultMaxConcentrationPercent,
            SpreadWideningThresholdFraction = spreadWideningFraction ?? SpreadWideningThresholdFraction,
            PriceDrawdownThresholdFraction = priceDrawdownFraction ?? PriceDrawdownThresholdFraction,
            MarketHistoryMinDays = MarketHistoryMinDays,
        };
}

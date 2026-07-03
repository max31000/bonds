namespace Bonds.Infrastructure.Quotes;

/// <summary>
/// Параметры лёгкого контура котировок (plan/16 часть A) — намеренно поллинг, не gRPC-стриминг
/// MarketDataStream и не SignalR (задание прямо это запрещает: single-user сервис, поллинг раз в
/// 30-60 сек полностью закрывает потребность и на порядок проще в эксплуатации). Дефолты — та же
/// эвристика происхождения, что <see cref="Scheduling.SchedulerOptions"/>: MOEX основная секция
/// торгов открывается ~10:00 MSK, окно 09:50-19:00 даёт запас на предторговый аукцион и вечернюю
/// сессию с небольшим отступом на завершение основных торгов.
/// </summary>
public sealed class LiveQuotesOptions
{
    /// <summary>Как часто опрашивать котировки открытых позиций (T-Invest GetQuotesAsync). Дефолт — 60 сек (plan/16).</summary>
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Начало окна торговых часов MOEX по московскому времени, за пределами — поллинг пропускается.</summary>
    public TimeOnly TradingWindowStartMsk { get; set; } = new TimeOnly(9, 50);

    /// <summary>Конец окна торговых часов MOEX по московскому времени.</summary>
    public TimeOnly TradingWindowEndMsk { get; set; } = new TimeOnly(19, 0);

    /// <summary>Retention тиков — при каждой записи удаляются строки старше этого срока (plan/16 часть A).</summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromDays(8);
}

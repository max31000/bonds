using Bonds.Core.Universe;

namespace Bonds.Infrastructure.Universe;

/// <summary>
/// Параметры хостед-сервиса банка облигаций (задача 26 часть C.1). Дефолты — та же эвристика
/// происхождения, что <see cref="Bonds.Infrastructure.Quotes.LiveQuotesOptions"/>: MOEX закрывает
/// основную сессию ~19:00 МСК, дневной срез истории пишется первым тиком после этого времени.
/// </summary>
public sealed class BondUniverseRefreshOptions
{
    /// <summary>Как часто обновлять снимок всей вселенной в торговые часы. Дефолт — раз в час
    /// (план: "в торговые часы — раз в час обновляет снимок") — банк не требует поминутной свежести,
    /// это не лёгкий контур котировок позиций.</summary>
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Как часто BackgroundService проверяет условия (наступил ли час обновления/momент
    /// закрытия торгов) — тот же паттерн, что <see cref="Bonds.Infrastructure.Scheduling.SchedulerOptions.PollingInterval"/>:
    /// не реальный интервал обновления, а частота опроса условий (не realtime, достаточно нескольких раз в час).</summary>
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Начало/конец торговых часов MOEX по московскому времени — та же эвристика, что
    /// <see cref="Bonds.Infrastructure.Quotes.LiveQuotesOptions"/> (09:50-19:00).</summary>
    public TimeOnly TradingWindowStartMsk { get; set; } = new TimeOnly(9, 50);
    public TimeOnly TradingWindowEndMsk { get; set; } = new TimeOnly(19, 0);

    /// <summary>Если снимок пуст ИЛИ старше этого порога — обновить сразу при старте сервиса,
    /// не дожидаясь первого интервала (план часть C.1).</summary>
    public TimeSpan StaleThreshold { get; set; } = TimeSpan.FromHours(6);

    /// <summary>Retention дневной истории — план: ~400 дней.</summary>
    public int HistoryRetentionDays { get; set; } = 400;

    /// <summary>Пороги гигиенического фильтра (часть C.4) — вынесены в общий Options-объект,
    /// т.к. дефолты одинаковы что для refresh-сервиса (не используются им напрямую), что для API.</summary>
    public UniverseHygieneOptions Hygiene { get; set; } = new();

    /// <summary>
    /// Задача 30 часть B.3 — как долго кэшировать сглаженный снимок relative value в памяти
    /// (<c>RelativeValueSnapshotBuilder</c>). Дефолт ~1 час (план); вынесено в конфиг (не константа
    /// в коде), чтобы интеграционные тесты могли поставить 0 — иначе singleton-кэш пережил бы
    /// сидирование новых данных следующим тестом в той же коллекции (TestWebApplicationFactory
    /// общий на всю коллекцию "Integration", см. IntegrationCollectionFixture).
    /// </summary>
    public TimeSpan RelativeValueCacheDuration { get; set; } = TimeSpan.FromHours(1);
}

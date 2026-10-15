using Bonds.Core.Universe;

namespace Bonds.Infrastructure.Universe;

/// <summary>
/// Параметры хостед-сервиса банка облигаций. Дефолты — та же эвристика
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

    /// <summary>Задача 43: максимум карточек бумаг (security_details), обрабатываемых за один суточный шаг.
    /// ~3400 бумаг банка прогреваются примерно за 17 суток, портфель и watchlist — в первый же шаг.</summary>
    public int SecurityDetailsBatchSize { get; set; } = 200;

    /// <summary>Задача 43: карточка старше этого срока считается устаревшей и обновляется (приоритет 3).</summary>
    public TimeSpan SecurityDetailsStaleAfter { get; set; } = TimeSpan.FromDays(30);

    /// <summary>Задача 43: пауза между запросами карточек к ISS (не напрягаем публичный API). В тестах 0.</summary>
    public TimeSpan SecurityDetailsRequestDelay { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Задача 45: максимум ИНН эмитентов, проверяемых в ГИР БО за один суточный шаг (бережём сервис).
    /// Очередь — портфель/watchlist, затем ВЕСЬ банк (ревью В2): при паузе <see cref="IssuerFinancialsRequestDelay"/>
    /// 1 с и 1–3 запросах на ИНН один тик занимает ~50–150 с; банк в несколько тысяч эмитентов прогревается
    /// за ~3–4 недели (дефолт 20 → 50).</summary>
    public int IssuerFinancialsBatchSize { get; set; } = 50;

    /// <summary>Задача 45: статус проверки ИНН (кроме 'error') старше этого срока считается устаревшим
    /// (отчётность годовая — чаще раз в месяц смысла нет).</summary>
    public TimeSpan IssuerFinancialsRefreshAfter { get; set; } = TimeSpan.FromDays(30);

    /// <summary>Задача 45: изолированный сбой по ИНН (статус 'error') перепроверяется на следующие сутки,
    /// а не через 30 дней. Чуть меньше 24 ч, чтобы не промахнуться мимо суточного окна.</summary>
    public TimeSpan IssuerFinancialsErrorRetryAfter { get; set; } = TimeSpan.FromHours(20);

    /// <summary>Задача 45: пауза между запросами к ГИР БО (не менее 1 с по плану). В тестах 0.</summary>
    public TimeSpan IssuerFinancialsRequestDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Retention дневной истории — план: ~400 дней.</summary>
    public int HistoryRetentionDays { get; set; } = 400;

    /// <summary>Пороги гигиенического фильтра (часть C.4) — вынесены в общий Options-объект,
    /// т.к. дефолты одинаковы что для refresh-сервиса (не используются им напрямую), что для API.</summary>
    public UniverseHygieneOptions Hygiene { get; set; } = new();

    /// <summary>
    /// Как долго кэшировать сглаженный снимок relative value в памяти
    /// (<c>RelativeValueSnapshotBuilder</c>). Дефолт ~1 час (план); вынесено в конфиг (не константа
    /// в коде), чтобы интеграционные тесты могли поставить 0 — иначе singleton-кэш пережил бы
    /// сидирование новых данных следующим тестом в той же коллекции (TestWebApplicationFactory
    /// общий на всю коллекцию "Integration", см. IntegrationCollectionFixture).
    /// </summary>
    public TimeSpan RelativeValueCacheDuration { get; set; } = TimeSpan.FromHours(1);
}

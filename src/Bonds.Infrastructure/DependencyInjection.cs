using Microsoft.Extensions.DependencyInjection.Extensions;
using Bonds.Core.Interfaces;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Signals;
using Bonds.Infrastructure.Analytics;
using Bonds.Infrastructure.CashFlow;
using Bonds.Infrastructure.Connectors.GirBo;
using Bonds.Infrastructure.Connectors.Moex;
using Bonds.Core.Credentials;
using Bonds.Infrastructure.Connectors.TInvest;
using Bonds.Infrastructure.Quotes;
using Bonds.Infrastructure.Repositories;
using Bonds.Infrastructure.Scheduling;
using Bonds.Infrastructure.Services;
using Bonds.Infrastructure.Sync;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bonds.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Регистрация кастомных Dapper TypeHandler'ов (DateOnly, enum-as-string) — один раз,
        // до первого использования Dapper-репозиториев (порт из cashpulse).
        DapperTypeHandlers.Register();

        // Вспомогательная функция — читает connectionString лениво при первом resolve,
        // чтобы WebApplicationFactory.ConfigureWebHost успела подменить конфигурацию
        // до того как репозитории впервые создадутся (паттерн из cashpulse).
        static string GetConnStr(IServiceProvider sp) =>
            sp.GetRequiredService<IConfiguration>()
              .GetConnectionString("DefaultConnection")
              ?? throw new InvalidOperationException("DefaultConnection string is not configured");

        // Repositories/Connectors/доменные сервисы регистрируются здесь по мере появления
        // (этапы 02-08): IInstrumentRepository, ITInvestClient, IMoexClient, ...

        // Auth
        services.AddScoped<IUserRepository>(sp => new UserRepository(GetConnStr(sp)));
        services.AddScoped<UserProvisioning>();

        // Storage — справочник, котировки, позиции/операции, сигналы.
        services.AddScoped<IInstrumentRepository>(sp => new InstrumentRepository(GetConnStr(sp)));
        services.AddScoped<ICouponScheduleRepository>(sp => new CouponScheduleRepository(GetConnStr(sp)));
        services.AddScoped<IAmortizationScheduleRepository>(sp => new AmortizationScheduleRepository(GetConnStr(sp)));
        services.AddScoped<IOfferScheduleRepository>(sp => new OfferScheduleRepository(GetConnStr(sp)));
        services.AddScoped<IMarketQuoteRepository>(sp => new MarketQuoteRepository(GetConnStr(sp)));
        services.AddScoped<IYieldCurveRepository>(sp => new YieldCurveRepository(GetConnStr(sp)));
        services.AddScoped<IAccountRepository>(sp => new AccountRepository(GetConnStr(sp)));
        services.AddScoped<IPositionRepository>(sp => new PositionRepository(GetConnStr(sp)));
        services.AddScoped<IOperationRepository>(sp => new OperationRepository(GetConnStr(sp)));
        services.AddScoped<IProjectedCashFlowRepository>(sp => new ProjectedCashFlowRepository(GetConnStr(sp)));
        services.AddScoped<IPortfolioValueSnapshotRepository>(sp => new PortfolioValueSnapshotRepository(GetConnStr(sp)));
        services.AddScoped<ISignalRepository>(sp => new SignalRepository(GetConnStr(sp)));
        services.AddScoped<ITargetAllocationRepository>(sp => new TargetAllocationRepository(GetConnStr(sp)));

        // Тики лёгкого контура котировок (intraday_quotes).
        services.AddScoped<IIntradayQuoteRepository>(sp => new IntradayQuoteRepository(GetConnStr(sp)));

        // Кэш дневной истории цены инструмента (instrument_price_history) для
        // графика цены карточки позиции.
        services.AddScoped<IInstrumentPriceHistoryRepository>(sp => new InstrumentPriceHistoryRepository(GetConnStr(sp)));

        // Настройки пользователя (пороги Signals Engine + токен T-Invest зашифрованный).
        services.AddScoped<IUserSettingsRepository>(sp => new UserSettingsRepository(GetConnStr(sp)));

        // Ручной watchlist (бумаги вне текущих позиций, отслеживаемые по ISIN).
        services.AddScoped<IWatchlistItemRepository>(sp => new WatchlistItemRepository(GetConnStr(sp)));

        // Банк облигаций MOEX — снимок всей рыночной вселенной + дневная история.
        services.AddScoped<IBondUniverseRepository>(sp => new BondUniverseRepository(GetConnStr(sp)));

        // Задача 43: персистентный кэш карточек бумаг MOEX (флаги дефолтов и т.п.).
        services.AddScoped<ISecurityDetailsRepository>(sp => new SecurityDetailsRepository(GetConnStr(sp)));

        // Задача 45: отчётность эмитентов (ГИР БО) и статусы проверки по ИНН.
        services.AddScoped<IIssuerFinancialsRepository>(sp => new IssuerFinancialsRepository(GetConnStr(sp)));

        // Задача 46: суточный кэш risk_level облигаций T-Invest и ручные рейтинги эмитентов.
        services.AddScoped<IInstrumentRiskLevelRepository>(sp => new InstrumentRiskLevelRepository(GetConnStr(sp)));
        services.AddScoped<IIssuerRatingRepository>(sp => new IssuerRatingRepository(GetConnStr(sp)));

        // Задача 51: фонды (БПИФ) — отдельный домен: справочник, позиции, журнал, рыночные данные.
        services.AddScoped<IFundRepository>(sp => new FundRepository(GetConnStr(sp)));
        services.AddScoped<IFundPositionRepository>(sp => new FundPositionRepository(GetConnStr(sp)));
        services.AddScoped<IFundOperationRepository>(sp => new FundOperationRepository(GetConnStr(sp)));
        services.AddScoped<IFundMarketDataRepository>(sp => new FundMarketDataRepository(GetConnStr(sp)));

        // Задача 55: валюта как актив (юани и т.п.) — отдельный домен по образцу фондов.
        services.AddScoped<ICurrencyRepository>(sp => new CurrencyRepository(GetConnStr(sp)));
        services.AddScoped<ICurrencyPositionRepository>(sp => new CurrencyPositionRepository(GetConnStr(sp)));
        services.AddScoped<ICurrencyOperationRepository>(sp => new CurrencyOperationRepository(GetConnStr(sp)));
        services.AddScoped<ICurrencyMarketDataRepository>(sp => new CurrencyMarketDataRepository(GetConnStr(sp)));

        // Задача 60: позиции, деньги и журнал не-T-Invest счетов (Freedom) - отдельный домен broker_*, цены и суммы в валюте строки.
        services.AddScoped<IBrokerPositionRepository>(sp => new BrokerPositionRepository(GetConnStr(sp)));
        services.AddScoped<IBrokerCashRepository>(sp => new BrokerCashRepository(GetConnStr(sp)));
        services.AddScoped<IBrokerOperationRepository>(sp => new BrokerOperationRepository(GetConnStr(sp)));

        // Migration runner
        services.AddSingleton(sp => new MigrationRunner(
            GetConnStr(sp),
            sp.GetRequiredService<ILogger<MigrationRunner>>()));

        // Connectors — MOEX ISS (справочно-историческое) и T-Invest (истина про счёт,
        // plan/00 §4 "Разделение источников данных").

        // MOEX ISS: бесплатный публичный API без авторизации — именованный HttpClient с базовым
        // адресом, без секретов в конфиге.
        services.AddHttpClient(MoexIssClient.HttpClientName, client =>
        {
            client.BaseAddress = new Uri("https://iss.moex.com");
            client.Timeout = TimeSpan.FromSeconds(15);
        });
        services.AddScoped<IMoexIssClient, MoexIssClient>();

        // ЦБ РФ (cbr.ru): официальные курсы USD/EUR/GBP для оценки брокерских позиций в рублях (задача 60) - публичный API без авторизации.
        services.AddHttpClient(Bonds.Infrastructure.Connectors.Cbr.CbrRatesClient.HttpClientName, client =>
        {
            client.BaseAddress = new Uri("https://www.cbr.ru");
            client.Timeout = TimeSpan.FromSeconds(15);
        });
        services.AddScoped<Bonds.Infrastructure.Connectors.Cbr.ICbrRatesClient, Bonds.Infrastructure.Connectors.Cbr.CbrRatesClient>();
        services.Configure<CurrencyRateOptions>(configuration.GetSection(CurrencyRateOptions.SectionName));

        // ГИР БО ФНС (bo.nalog.gov.ru): открытая отчётность организаций по ИНН, без авторизации и секретов,
        // но с обязательным User-Agent. Таймаут 30 с — сервис медленный.
        services.AddHttpClient(GirBoClient.HttpClientName, client =>
        {
            client.BaseAddress = new Uri("https://bo.nalog.gov.ru");
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("BondAnalytics/1.0 (personal portfolio tool)");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        });
        services.AddScoped<IGirBoClient, GirBoClient>();

        // Freedom (Tradernet API v3, только чтение): ключи пользователя приходят параметром в каждый вызов
        // клиента (реестр секретов, задача 58), в конфиге только адрес хоста.
        services.Configure<Bonds.Infrastructure.Connectors.Freedom.FreedomOptions>(
            configuration.GetSection(Bonds.Infrastructure.Connectors.Freedom.FreedomOptions.SectionName));
        services.AddHttpClient(Bonds.Infrastructure.Connectors.Freedom.FreedomApiClient.HttpClientName, (sp, client) =>
        {
            var o = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Bonds.Infrastructure.Connectors.Freedom.FreedomOptions>>().Value;
            client.BaseAddress = new Uri(o.BaseUrl);
            client.Timeout = TimeSpan.FromSeconds(o.TimeoutSeconds);
        });
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<Bonds.Infrastructure.Connectors.Freedom.IFreedomApiClient, Bonds.Infrastructure.Connectors.Freedom.FreedomApiClient>();

        // Telegram Bot API — алерт владельцу при падении автосинка. Тот же бот,
        // что и CI-алерты (Telegram:BotToken), base address без секрета в URL самого
        // клиента (токен подставляется в путь запроса на каждый вызов, см. TelegramAlertSender).
        services.AddHttpClient(TelegramAlertSender.HttpClientName, client =>
        {
            client.BaseAddress = new Uri("https://api.telegram.org");
            client.Timeout = TimeSpan.FromSeconds(10);
        });
        services.AddSingleton<ITelegramAlertSender, TelegramAlertSender>();
        services.AddSingleton<SyncAlertThrottle>();

        // T-Invest: gRPC-клиент НЕ регистрируется здесь готовым (нет статического токена в DI) —
        // TInvestPortfolioClient сам резолвит токен через ITInvestTokenProvider (БД на пользователя,
        // без ENV-фолбэка) и лениво строит InvestApiClient внутри своего scoped-экземпляра
        // (см. doc-comment TInvestPortfolioClient, BACKEND_DECISIONS.md решение 12).
        services.AddScoped<ITInvestPortfolioClient, TInvestPortfolioClient>();

        // Валидация токена перед сохранением — не завязана на
        // ITInvestTokenProvider/БД, токен — явный аргумент вызова из PUT /api/settings/tinvest-token.
        services.AddScoped<ITInvestTokenValidator, TInvestTokenValidator>();

        // Оркестрация синка — без HTTP-эндпоинта/шедулера на этом этапе,
        // вызывается программно/из тестов.
        services.AddScoped<BondSyncService>();

        // Задача 46: суточное обновление risk_level облигаций T-Invest (один bulk-вызов Bonds(), шаг цикла синка).
        services.AddScoped<InstrumentRiskLevelRefresher>();

        // Задача 51: синк фондов (вызывается из BondSyncService на уже полученных данных) и суточные
        // рыночные данные фондов (шаг SyncCycleService).
        services.AddScoped<IFundSyncService, FundSyncService>();
        services.AddScoped<FundMarketDataRefresher>();

        // Задача 55: синк валюты (из BondSyncService), курс платежей не в рублях и суточная история курса (шаг SyncCycleService).
        services.AddScoped<ICurrencySyncService, CurrencySyncService>();
        services.AddScoped<ICurrencyRateLookup, CurrencyRateLookup>();
        services.AddScoped<CurrencyRateRefresher>();

        // Задача 60: синк счёта Freedom (по всем пользователям с ключами) - отдельный шаг SyncCycleService после T-Invest - и
        // оценка брокерских позиций в рублях (читается эндпоинтами и сводкой портфеля).
        services.AddScoped<FreedomSyncService>();
        services.AddScoped<Bonds.Infrastructure.Analytics.BrokerHoldingsBuilder>();

        // Единый путь "обогатить одну бумагу по ISIN" (резолв инструмента +
        // MOEX-котировка), вынесенный из WatchlistSyncService — переиспользуется watchlist
        // и материализацией из банка облигаций (POST /api/universe/{secid}/materialize).
        services.AddScoped<InstrumentEnrichmentService>();

        // Синк watchlist-бумаг (ISIN без позиции) — переиспользует
        // InstrumentEnrichmentService, отдельный шаг в SyncCycleService.
        services.AddScoped<WatchlistSyncService>();

        // Cash-Flow Projection + Portfolio Analytics — координирующие сервисы поверх
        // чистого Bonds.Core.CashFlow/Bonds.Core.Analytics; без HTTP-эндпоинта и без
        // планирования по расписанию — вызываются программно/из тестов на этом этапе.
        services.AddScoped<CashFlowProjectionOrchestrator>();
        services.AddScoped<PortfolioSnapshotService>();

        // Ретроспективный бэкфилл истории XIRR из журнала операций + дневных цен MOEX ISS.
        services.AddScoped<PortfolioHistoryBackfillService>();

        // График цены карточки позиции — та же дозагрузка хвоста, что и у
        // бэкфилла XIRR, но per-instrument и с персистентным кэшем (instrument_price_history).
        services.AddScoped<InstrumentPriceHistoryService>();

        // Сборщик holdings (между репозиториями и аналитическими сервисами) — общий
        // вход для positions/composition/scatter/comparison/replacement эндпоинтов.
        services.AddScoped<PortfolioHoldingsBuilder>();

        // Единый журнал доходов (задача 52): входы для PositionIncomeCalculator; задача 53 берёт доходы по фондам отсюда.
        services.AddScoped<IncomeLedgerBuilder>();
        services.AddScoped<FundOverviewBuilder>();
        services.AddScoped<CurrencyOverviewBuilder>();

        // Резолвер эффективной ставки комиссии (override → оценка из журнала →
        // дефолт) — потребляется replacement/allocation/ifSoldNow эндпоинтами.
        services.AddScoped<ICommissionRateProvider, CommissionRateProvider>();

        // DataProtection — шифрование токена T-Invest, вводимого через UI (PUT /api/settings/tinvest-token).
        // Ключи ЯВНО персистируются на volume вне UnionFS-слоёв контейнера (DataProtection:KeysPath,
        // дефолт /app/dataprotection-keys) — по умолчанию ASP.NET Core пишет ключи в domain-профиль
        // ("/root/.aspnet/DataProtection-Keys"), который живёт только внутри слоя контейнера и
        // теряется при каждом "docker stop && rm && run" на деплое (plan/13 корневая причина: токен
        // из БД молча перестаёт расшифровываться после каждого передеплоя). SetApplicationName
        // фиксирован явной строкой (а не выводится из имени сборки/пути) — стабильность ключей не
        // должна зависеть от имени бинарника/пути публикации.
        //
        // Путь читается ЛЕНИВО через IConfigureOptions<KeyManagementOptions> (а не сразу из
        // параметра configuration выше — тот же паттерн, что GetConnStr),
        // потому что WebApplicationFactory.ConfigureWebHost в тестах подставляет DataProtection:KeysPath
        // ПОСЛЕ этого вызова AddInfrastructure — eager-чтение здесь всегда попадало бы на дефолт
        // "/app/dataprotection-keys" (в тестовом контейнере read-only FS) вместо тестового temp dir.
        services.AddDataProtection().SetApplicationName("bonds-api");
        services.AddSingleton<IConfigureOptions<KeyManagementOptions>>(sp =>
            new ConfigureOptions<KeyManagementOptions>(options =>
            {
                var keysPath = sp.GetRequiredService<IConfiguration>()["DataProtection:KeysPath"]
                    ?? "/app/dataprotection-keys";
                options.XmlRepository = new FileSystemXmlRepository(new DirectoryInfo(keysPath), sp.GetRequiredService<ILoggerFactory>());
            }));
        services.AddScoped<ITInvestTokenProvider, TInvestTokenProvider>();

        // Задача 58: «Подключения» — секреты пользователя (токены брокеров/ИИ) в user_credentials.
        // Новый провайдер = запись в CredentialProviders + ICredentialValidator ниже, без миграций и эндпоинтов.
        services.AddScoped<IUserCredentialRepository>(sp => new UserCredentialRepository(GetConnStr(sp)));
        services.AddScoped<IUserCredentialStore, Bonds.Infrastructure.Credentials.UserCredentialStore>();
        services.AddScoped<ICredentialValidator, Bonds.Infrastructure.Credentials.TInvestReadCredentialValidator>();
        services.AddScoped<ICredentialValidator, Bonds.Infrastructure.Credentials.TInvestTradeCredentialValidator>();
        services.AddScoped<ICredentialValidator, Bonds.Infrastructure.Credentials.ClaudeCodeCredentialValidator>();
        services.AddScoped<ICredentialValidator, Bonds.Infrastructure.Credentials.FreedomCredentialValidator>();

        // Signals Engine + Scheduler — options читаются из конфига с дефолтами
        // самого класса опций, если секция не задана (см. doc-comment SignalEngineOptions/SchedulerOptions).
        services.Configure<SignalEngineOptions>(configuration.GetSection("Signals"));
        services.Configure<SchedulerOptions>(configuration.GetSection("Scheduler"));

        // ISyncCycleRunner — Singleton (держит статус последнего синка и семафор защиты от
        // параллельного запуска между тиками хостед-сервиса и HTTP force-refresh,
        // сам резолвит Scoped-зависимости через IServiceScopeFactory внутри RunCycleAsync
        // (см. doc-comment SyncCycleService).
        services.AddSingleton<ISyncCycleRunner, SyncCycleService>();
        services.AddHostedService<SyncSchedulerHostedService>();

        // Лёгкий контур котировок — поллинг, НЕ gRPC-стриминг/SignalR (осознанное
        // ограничение плана). Options вынесены в конфиг с дефолтами самого класса опций.
        services.Configure<LiveQuotesOptions>(configuration.GetSection("LiveQuotes"));
        services.AddHostedService<LiveQuotesPollingService>();

        // Банк облигаций MOEX — снимок всей рыночной вселенной (не только
        // позиций/watchlist), раз в час в торговые часы + дневная история для трендов.
        services.Configure<Bonds.Infrastructure.Universe.BondUniverseRefreshOptions>(configuration.GetSection("BondUniverse"));
        services.AddHostedService<Bonds.Infrastructure.Universe.BondUniverseRefreshService>();
        // Задача 43: суточные шаги обогащения банка (резолвятся из scope тика BondUniverseRefreshService).
        services.AddScoped<Bonds.Infrastructure.Universe.UniverseEmitentEnricher>();
        services.AddScoped<Bonds.Infrastructure.Universe.SecurityDetailsEnricher>();
        // Задача 45: суточное наполнение отчётности эмитентов портфеля/watchlist из ГИР БО (тот же тик).
        services.AddScoped<Bonds.Infrastructure.Universe.IssuerFinancialsEnricher>();

        // Задача 49: батчевая сборка входов рыночных/кредитных событий (Scoped: репозитории Scoped).
        services.AddScoped<Bonds.Infrastructure.Universe.PositionMarketContextBuilder>();

        // Сглаженная (медиана дневных медиан за 5 торговых дней) статистика
        // корзин relative value + кэш ~1 час в памяти — Singleton (кэш переживает HTTP-запросы),
        // резолвит Scoped IBondUniverseRepository через IServiceScopeFactory (см. doc-comment).
        services.AddSingleton<Bonds.Infrastructure.Universe.RelativeValueSnapshotBuilder>();

        // Задача 46: скоркарта надёжности всего банка (батчевая сборка входов + кэш; Invalidate при ручном вводе).
        services.AddSingleton<Bonds.Infrastructure.Universe.ReliabilityScoreProvider>();

        return services;
    }
}

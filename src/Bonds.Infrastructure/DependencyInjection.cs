using Bonds.Core.Interfaces;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Services;
using Bonds.Core.Signals;
using Bonds.Infrastructure.Analytics;
using Bonds.Infrastructure.CashFlow;
using Bonds.Infrastructure.Connectors.Moex;
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

        // Auth (этап 02)
        services.AddScoped<IUserRepository>(sp => new UserRepository(GetConnStr(sp)));
        services.AddScoped<ITelegramAuthService, TelegramAuthService>();

        // Storage (этап 03) — справочник, котировки, позиции/операции, сигналы.
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

        // Plan/16 часть A: тики лёгкого контура котировок (intraday_quotes).
        services.AddScoped<IIntradayQuoteRepository>(sp => new IntradayQuoteRepository(GetConnStr(sp)));

        // Plan/19 часть A: кэш дневной истории цены инструмента (instrument_price_history) для
        // графика цены карточки позиции.
        services.AddScoped<IInstrumentPriceHistoryRepository>(sp => new InstrumentPriceHistoryRepository(GetConnStr(sp)));

        // Этап 08: настройки пользователя (пороги Signals Engine + токен T-Invest зашифрованный).
        services.AddScoped<IUserSettingsRepository>(sp => new UserSettingsRepository(GetConnStr(sp)));

        // Задача 20 часть A: ручной watchlist (бумаги вне текущих позиций, отслеживаемые по ISIN).
        services.AddScoped<IWatchlistItemRepository>(sp => new WatchlistItemRepository(GetConnStr(sp)));

        // Migration runner
        services.AddSingleton(sp => new MigrationRunner(
            GetConnStr(sp),
            sp.GetRequiredService<ILogger<MigrationRunner>>()));

        // Connectors (этап 04) — MOEX ISS (справочно-историческое) и T-Invest (истина про счёт,
        // plan/00 §4 "Разделение источников данных").

        // MOEX ISS: бесплатный публичный API без авторизации — именованный HttpClient с базовым
        // адресом, без секретов в конфиге.
        services.AddHttpClient(MoexIssClient.HttpClientName, client =>
        {
            client.BaseAddress = new Uri("https://iss.moex.com");
            client.Timeout = TimeSpan.FromSeconds(15);
        });
        services.AddScoped<IMoexIssClient, MoexIssClient>();

        // Telegram Bot API — алерт владельцу при падении автосинка (plan/13 часть D). Тот же бот,
        // что и авторизация/CI-алерты (Telegram:BotToken), base address без секрета в URL самого
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

        // Валидация токена перед сохранением (plan/13 часть C) — не завязана на
        // ITInvestTokenProvider/БД, токен — явный аргумент вызова из PUT /api/settings/tinvest-token.
        services.AddScoped<ITInvestTokenValidator, TInvestTokenValidator>();

        // Оркестрация синка (этап 04 Часть C) — без HTTP-эндпоинта/шедулера на этом этапе,
        // вызывается программно/из тестов (см. plan/04).
        services.AddScoped<BondSyncService>();

        // Задача 20 часть A: синк watchlist-бумаг (ISIN без позиции) — переиспользует BondSyncService,
        // отдельный шаг в SyncCycleService.
        services.AddScoped<WatchlistSyncService>();

        // Cash-Flow Projection + Portfolio Analytics (этап 06) — координирующие сервисы поверх
        // чистого Bonds.Core.CashFlow/Bonds.Core.Analytics; без HTTP-эндпоинта (этап 08) и без
        // планирования по расписанию (этап 07) — вызываются программно/из тестов на этом этапе.
        services.AddScoped<CashFlowProjectionOrchestrator>();
        services.AddScoped<PortfolioSnapshotService>();

        // Plan/15: ретроспективный бэкфилл истории XIRR из журнала операций + дневных цен MOEX ISS.
        services.AddScoped<PortfolioHistoryBackfillService>();

        // Plan/19 часть A: график цены карточки позиции — та же дозагрузка хвоста, что и у
        // бэкфилла XIRR, но per-instrument и с персистентным кэшем (instrument_price_history).
        services.AddScoped<InstrumentPriceHistoryService>();

        // Этап 08: сборщик holdings (между репозиториями и аналитическими сервисами) — общий
        // вход для positions/composition/scatter/comparison/replacement эндпоинтов.
        services.AddScoped<PortfolioHoldingsBuilder>();

        // Plan/22 часть C: резолвер эффективной ставки комиссии (override → оценка из журнала →
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
        // параметра configuration выше — тот же паттерн, что GetConnStr/Jwt:Secret в Program.cs),
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

        // Signals Engine + Scheduler (этап 07, plan/07) — options читаются из конфига с дефолтами
        // самого класса опций, если секция не задана (см. doc-comment SignalEngineOptions/SchedulerOptions).
        services.Configure<SignalEngineOptions>(configuration.GetSection("Signals"));
        services.Configure<SchedulerOptions>(configuration.GetSection("Scheduler"));

        // ISyncCycleRunner — Singleton (держит статус последнего синка и семафор защиты от
        // параллельного запуска между тиками хостед-сервиса и будущим HTTP force-refresh,
        // этап 08), сам резолвит Scoped-зависимости через IServiceScopeFactory внутри RunCycleAsync
        // (см. doc-comment SyncCycleService).
        services.AddSingleton<ISyncCycleRunner, SyncCycleService>();
        services.AddHostedService<SyncSchedulerHostedService>();

        // Plan/16 часть A: лёгкий контур котировок — поллинг, НЕ gRPC-стриминг/SignalR (осознанное
        // ограничение плана). Options вынесены в конфиг с дефолтами самого класса опций.
        services.Configure<LiveQuotesOptions>(configuration.GetSection("LiveQuotes"));
        services.AddHostedService<LiveQuotesPollingService>();

        return services;
    }
}

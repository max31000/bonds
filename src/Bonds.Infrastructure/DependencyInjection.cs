using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Services;
using Bonds.Infrastructure.Repositories;
using Bonds.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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

        // Migration runner
        services.AddSingleton(sp => new MigrationRunner(
            GetConnStr(sp),
            sp.GetRequiredService<ILogger<MigrationRunner>>()));

        return services;
    }
}

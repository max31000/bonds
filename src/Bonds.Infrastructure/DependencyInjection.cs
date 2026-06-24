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

        // Migration runner
        services.AddSingleton(sp => new MigrationRunner(
            GetConnStr(sp),
            sp.GetRequiredService<ILogger<MigrationRunner>>()));

        return services;
    }
}

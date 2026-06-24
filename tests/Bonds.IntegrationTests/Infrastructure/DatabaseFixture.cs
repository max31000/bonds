using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.MySql;
using Bonds.Infrastructure;
using Xunit;

namespace Bonds.IntegrationTests.Infrastructure;

/// <summary>
/// Поднимает контейнер MySQL 8.0 и прогоняет все embedded-миграции через MigrationRunner
/// (включая доменную схему этапа 03 — instruments/positions/operations/... см.
/// 003_domain_schema.sql/004_add_user_base_currency.sql). Тесты репозиториев сидируют
/// свои собственные данные через сами репозитории (см. *RepositoryTests.cs) — здесь
/// фикстура только поднимает чистую мигрированную БД.
/// </summary>
public class DatabaseFixture : IAsyncLifetime
{
    private readonly MySqlContainer _container = new MySqlBuilder()
        .WithImage("mysql:8.0")
        .WithDatabase("bonds_test")
        .WithUsername("testuser")
        .WithPassword("testpassword")
        .Build();

    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();

        var logger = NullLogger<MigrationRunner>.Instance;
        var runner = new MigrationRunner(ConnectionString, logger);
        await runner.RunAsync();
    }

    public async Task DisposeAsync()
    {
        await _container.StopAsync();
        await _container.DisposeAsync();
    }
}

using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.MySql;
using Bonds.Infrastructure;
using Xunit;

namespace Bonds.IntegrationTests.Infrastructure;

/// <summary>
/// Поднимает контейнер MySQL 8.0 и прогоняет миграции через MigrationRunner.
/// На этапе 01 доменных таблиц нет — миграция только создаёт schema_version.
/// Сидинг тестовых данных добавится вместе с доменными миграциями (этап 03).
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

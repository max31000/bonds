using Bonds.Infrastructure;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Testcontainers.MySql;
using Xunit;

namespace Bonds.IntegrationTests;

/// <summary>
/// Тест миграций (plan/03 §D, критерии приёмки): применение всех embedded-миграций с нуля
/// проходит без ошибок, и повторный запуск MigrationRunner на той же базе ничего не ломает.
/// Идемпотентность обеспечивается реестром _migrations (каждый файл применяется максимум
/// один раз) — MySQL 8.0 не поддерживает "ADD COLUMN IF NOT EXISTS" (MariaDB-расширение),
/// поэтому для ALTER-миграций (004) идемпотентность держится исключительно на реестре,
/// как и в cashpulse. CREATE TABLE-миграции (003) дополнительно используют IF NOT EXISTS
/// как defense-in-depth.
/// Использует собственный контейнер (не общую DatabaseFixture), чтобы проверить путь
/// "с нуля" изолированно от прочих тестов класса.
/// </summary>
public class MigrationIdempotencyTests : IAsyncLifetime
{
    private readonly MySqlContainer _container = new MySqlBuilder()
        .WithImage("mysql:8.0")
        .WithDatabase("bonds_migration_test")
        .WithUsername("testuser")
        .WithPassword("testpassword")
        .Build();

    public async Task InitializeAsync() => await _container.StartAsync();

    public async Task DisposeAsync()
    {
        await _container.StopAsync();
        await _container.DisposeAsync();
    }

    [Fact]
    public async Task RunAsync_FromScratch_AppliesAllMigrations_AndCreatesExpectedTables()
    {
        var connectionString = _container.GetConnectionString();
        var runner = new MigrationRunner(connectionString, NullLogger<MigrationRunner>.Instance);

        await runner.RunAsync();

        await using var conn = new MySqlConnection(connectionString);
        await conn.OpenAsync();

        var appliedFiles = (await conn.QueryAsync<string>("SELECT FileName FROM _migrations ORDER BY FileName")).ToList();
        appliedFiles.Should().Contain(new[]
        {
            "001_initial_schema.sql",
            "002_add_users.sql",
            "003_domain_schema.sql",
            "004_add_user_base_currency.sql",
            "005_add_user_settings.sql",
            "006_add_instrument_name.sql",
            "007_add_intraday_quotes.sql",
            "008_add_instrument_price_history.sql",
            "009_add_watchlist_items.sql",
            "010_purge_intraday_quotes.sql",
            "011_add_amortization_is_known.sql",
            "012_add_commission_rate_override.sql",
            "013_add_bond_universe.sql",
        });

        var expectedTables = new[]
        {
            "users", "instruments", "coupon_schedules", "amortization_schedules", "offer_schedules",
            "market_quotes", "yield_curve_snapshots", "accounts", "positions", "operations",
            "projected_cash_flows", "portfolio_value_snapshots", "signals", "target_allocations",
            "user_settings", "intraday_quotes", "instrument_price_history", "watchlist_items",
            "bond_universe", "bond_universe_history",
        };

        foreach (var table in expectedTables)
        {
            var exists = await conn.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = @Table",
                new { Table = table });
            exists.Should().Be(1, $"таблица {table} должна быть создана миграциями");
        }
    }

    [Fact]
    public async Task RunAsync_CalledTwice_IsIdempotent_NoErrorsNoDuplicateRows()
    {
        var connectionString = _container.GetConnectionString();
        var runner = new MigrationRunner(connectionString, NullLogger<MigrationRunner>.Instance);

        await runner.RunAsync();
        // Повторный запуск на той же базе — критерий приёмки этапа 03 ("идемпотентность миграций").
        await runner.RunAsync();

        await using var conn = new MySqlConnection(connectionString);
        await conn.OpenAsync();

        var migrationCount = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM _migrations");
        migrationCount.Should().Be(13, "повторный запуск не должен заново применять уже применённые миграции");

        // users.base_currency существует ровно один раз (ALTER ... ADD COLUMN IF NOT EXISTS не падает повторно).
        var columnCount = await conn.ExecuteScalarAsync<long>(
            @"SELECT COUNT(*) FROM information_schema.columns
              WHERE table_schema = DATABASE() AND table_name = 'users' AND column_name = 'base_currency'");
        columnCount.Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_NewRunnerInstance_OnAlreadyMigratedDb_DoesNotThrow()
    {
        var connectionString = _container.GetConnectionString();
        await new MigrationRunner(connectionString, NullLogger<MigrationRunner>.Instance).RunAsync();

        // Новый процесс/инстанс приложения стартует на уже мигрированной базе (типичный сценарий
        // повторного деплоя контейнера bonds-api) — не должно быть исключений.
        var secondRunner = new MigrationRunner(connectionString, NullLogger<MigrationRunner>.Instance);
        var act = async () => await secondRunner.RunAsync();

        await act.Should().NotThrowAsync();
    }
}

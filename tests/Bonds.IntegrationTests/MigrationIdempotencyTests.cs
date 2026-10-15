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
            "014_add_account_balance.sql",
            "015_redemption_correctness_repair.sql",
            "016_signals_as_events.sql",
            "017_universe_official_yields.sql",
            "018_universe_emitents_and_security_details.sql",
        });

        var expectedTables = new[]
        {
            "users", "instruments", "coupon_schedules", "amortization_schedules", "offer_schedules",
            "market_quotes", "yield_curve_snapshots", "accounts", "positions", "operations",
            "projected_cash_flows", "portfolio_value_snapshots", "signals", "target_allocations",
            "user_settings", "intraday_quotes", "instrument_price_history", "watchlist_items",
            "bond_universe", "bond_universe_history", "security_details",
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
        migrationCount.Should().Be(18, "повторный запуск не должен заново применять уже применённые миграции");

        // users.base_currency существует ровно один раз (ALTER ... ADD COLUMN IF NOT EXISTS не падает повторно).
        var columnCount = await conn.ExecuteScalarAsync<long>(
            @"SELECT COUNT(*) FROM information_schema.columns
              WHERE table_schema = DATABASE() AND table_name = 'users' AND column_name = 'base_currency'");
        columnCount.Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_Migration015_OperationsTypeEnumAcceptsSecurityTransfers()
    {
        var connectionString = _container.GetConnectionString();
        await new MigrationRunner(connectionString, NullLogger<MigrationRunner>.Instance).RunAsync();

        await using var conn = new MySqlConnection(connectionString);
        await conn.OpenAsync();

        var columnType = await conn.ExecuteScalarAsync<string>(
            @"SELECT COLUMN_TYPE FROM information_schema.columns
              WHERE table_schema = DATABASE() AND table_name = 'operations' AND column_name = 'type'");
        columnType.Should().Contain("'SecurityIn'").And.Contain("'SecurityOut'");
        columnType.Should().Contain("'Redemption'", "прежние значения enum не потеряны");
    }

    [Fact]
    public async Task RunAsync_Migration016_SignalsBecomeEvents_AndDeadSettingsColumnsDropped()
    {
        var connectionString = _container.GetConnectionString();
        await new MigrationRunner(connectionString, NullLogger<MigrationRunner>.Instance).RunAsync();

        await using var conn = new MySqlConnection(connectionString);
        await conn.OpenAsync();

        async Task<long> ColumnCount(string table, string column) => await conn.ExecuteScalarAsync<long>(
            @"SELECT COUNT(*) FROM information_schema.columns
              WHERE table_schema = DATABASE() AND table_name = @Table AND column_name = @Column",
            new { Table = table, Column = column });

        (await ColumnCount("signals", "event_key")).Should().Be(1);
        (await ColumnCount("signals", "resolved_at")).Should().Be(1);
        (await ColumnCount("signals", "acknowledged_at")).Should().Be(1);
        (await ColumnCount("signals", "is_read")).Should().Be(0, "старый append-only лог заменён");
        (await ColumnCount("user_settings", "yield_below_alternative_bps")).Should().Be(0);
        (await ColumnCount("user_settings", "duration_drift_tolerance_years")).Should().Be(0);
        (await ColumnCount("user_settings", "default_max_concentration_pct")).Should().Be(1, "рабочие пороги остались");

        (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM signals")).Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_Migration017_UniverseOfficialYieldColumnsAndHistoryExtensionExist()
    {
        var connectionString = _container.GetConnectionString();
        await new MigrationRunner(connectionString, NullLogger<MigrationRunner>.Instance).RunAsync();

        await using var conn = new MySqlConnection(connectionString);
        await conn.OpenAsync();

        async Task<long> ColumnCount(string table, string column) => await conn.ExecuteScalarAsync<long>(
            @"SELECT COUNT(*) FROM information_schema.columns
              WHERE table_schema = DATABASE() AND table_name = @Table AND column_name = @Column",
            new { Table = table, Column = column });

        foreach (var column in new[]
                 {
                     "accrued_rub", "issue_size", "effective_yield_fraction", "yield_to_offer_fraction",
                     "yield_date_type", "gspread_official_fraction", "zspread_official_fraction",
                 })
        {
            (await ColumnCount("bond_universe", column)).Should().Be(1, $"bond_universe.{column} добавлена миграцией 017");
        }

        foreach (var column in new[] { "bid", "offer", "list_level", "gspread_effective_fraction" })
        {
            (await ColumnCount("bond_universe_history", column)).Should().Be(1, $"bond_universe_history.{column} добавлена миграцией 017");
        }
    }

    [Fact]
    public async Task RunAsync_Migration018_UniverseEmitentColumnsAndSecurityDetailsTableExist()
    {
        var connectionString = _container.GetConnectionString();
        await new MigrationRunner(connectionString, NullLogger<MigrationRunner>.Instance).RunAsync();

        await using var conn = new MySqlConnection(connectionString);
        await conn.OpenAsync();

        async Task<long> ColumnCount(string table, string column) => await conn.ExecuteScalarAsync<long>(
            @"SELECT COUNT(*) FROM information_schema.columns
              WHERE table_schema = DATABASE() AND table_name = @Table AND column_name = @Column",
            new { Table = table, Column = column });

        foreach (var column in new[] { "emitent_id", "emitent_inn", "emitent_title" })
        {
            (await ColumnCount("bond_universe", column)).Should().Be(1, $"bond_universe.{column} добавлена миграцией 018");
        }

        foreach (var column in new[]
                 {
                     "secid", "isin", "emitent_id", "has_default", "has_technical_default", "is_qualified_investors",
                     "coupon_benchmark", "coupon_benchmark_spread_fraction", "early_repayment", "fetched_at",
                 })
        {
            (await ColumnCount("security_details", column)).Should().Be(1, $"security_details.{column} создана миграцией 018");
        }
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

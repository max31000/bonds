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
            "019_issuer_financials.sql",
            "020_instrument_risk_levels_and_issuer_ratings.sql",
            "021_replacement_horizon_setting.sql",
            "022_market_credit_events.sql",
            "023_funds.sql",
            "024_operations_accrued.sql",
            "025_currency_assets.sql",
            "026_fund_duration_override.sql",
            "027_history_rebuild_after_quantity_fix.sql",
            "028_history_rebuild_accrued_settlement_currency.sql",
            "029_user_credentials.sql",
            "030_broker_accounts.sql",
            "031_cash_movements_trading_meta.sql",
            "032_baskets.sql",
            "033_trade_plans.sql",
            "034_telegram_bot.sql",
            "035_trade_orders.sql",
            "036_trade_orders_attention.sql",
            "037_trade_orders_broker.sql",
            "038_trade_orders_commission_currency.sql",
            "039_trade_plans_cash_hold.sql",
        });

        var expectedTables = new[]
        {
            "users", "instruments", "coupon_schedules", "amortization_schedules", "offer_schedules",
            "market_quotes", "yield_curve_snapshots", "accounts", "positions", "operations",
            "projected_cash_flows", "portfolio_value_snapshots", "signals",
            "user_settings", "intraday_quotes", "instrument_price_history", "watchlist_items",
            "bond_universe", "bond_universe_history", "security_details", "issuer_financials", "issuer_financials_status",
            "instrument_risk_levels", "issuer_ratings",
            "funds", "fund_positions", "fund_operations", "fund_price_history", "fund_distributions", "market_index_history",
            "currencies", "currency_positions", "currency_operations", "currency_rate_history",
            "user_credentials",
            "broker_positions", "broker_cash", "broker_operations",
            "account_cash_movements", "instrument_trading_meta",
            "baskets", "basket_members",
            "trade_plans", "trade_plan_lines",
            "bot_state",
            "trade_orders",
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
        migrationCount.Should().Be(39, "повторный запуск не должен заново применять уже применённые миграции");

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
    public async Task RunAsync_Migration019_IssuerFinancialsTablesAndUniqueKeyExist()
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
                     "inn", "period", "source", "fetched_at", "revenue", "operating_profit", "interest_paid", "net_profit",
                     "equity", "total_assets", "current_assets", "current_liabilities", "cash", "long_debt", "short_debt",
                     "retained_earnings", "has_audit",
                 })
        {
            (await ColumnCount("issuer_financials", column)).Should().Be(1, $"issuer_financials.{column} создана миграцией 019");
        }

        foreach (var column in new[] { "inn", "status", "checked_at" })
        {
            (await ColumnCount("issuer_financials_status", column)).Should().Be(1, $"issuer_financials_status.{column} создана миграцией 019");
        }

        var uniqueCols = (await conn.QueryAsync<string>(
            @"SELECT column_name FROM information_schema.statistics
              WHERE table_schema = DATABASE() AND table_name = 'issuer_financials' AND index_name = 'uq_issuer_financials'
              ORDER BY seq_in_index")).ToList();
        uniqueCols.Should().Equal("inn", "period", "source");
    }

    [Fact]
    public async Task RunAsync_Migration020_RiskLevelsAndIssuerRatingsTablesExist()
    {
        var connectionString = _container.GetConnectionString();
        await new MigrationRunner(connectionString, NullLogger<MigrationRunner>.Instance).RunAsync();

        await using var conn = new MySqlConnection(connectionString);
        await conn.OpenAsync();

        async Task<long> ColumnCount(string table, string column) => await conn.ExecuteScalarAsync<long>(
            @"SELECT COUNT(*) FROM information_schema.columns
              WHERE table_schema = DATABASE() AND table_name = @Table AND column_name = @Column",
            new { Table = table, Column = column });

        foreach (var column in new[] { "isin", "risk_level", "fetched_at" })
        {
            (await ColumnCount("instrument_risk_levels", column)).Should().Be(1, $"instrument_risk_levels.{column} создана миграцией 020");
        }

        foreach (var column in new[] { "inn", "agency", "rating", "rating_date", "note" })
        {
            (await ColumnCount("issuer_ratings", column)).Should().Be(1, $"issuer_ratings.{column} создана миграцией 020");
        }

        var uniqueCols = (await conn.QueryAsync<string>(
            @"SELECT column_name FROM information_schema.statistics
              WHERE table_schema = DATABASE() AND table_name = 'issuer_ratings' AND index_name = 'uq_issuer_ratings'
              ORDER BY seq_in_index")).ToList();
        uniqueCols.Should().Equal("inn", "agency");
    }

    [Fact]
    public async Task RunAsync_Migration021_ReplacementHorizonColumnExistsWithDefaultTwoYears()
    {
        var connectionString = _container.GetConnectionString();
        await new MigrationRunner(connectionString, NullLogger<MigrationRunner>.Instance).RunAsync();

        await using var conn = new MySqlConnection(connectionString);
        await conn.OpenAsync();

        var column = await conn.QuerySingleAsync<(string IsNullable, string? Default)>(
            @"SELECT is_nullable AS IsNullable, column_default AS `Default` FROM information_schema.columns
              WHERE table_schema = DATABASE() AND table_name = 'user_settings' AND column_name = 'replacement_horizon_years'");
        column.IsNullable.Should().Be("NO", "горизонт замены NOT NULL (миграция 021)");
        decimal.Parse(column.Default!, System.Globalization.CultureInfo.InvariantCulture).Should().Be(2.00m, "дефолт = прежняя фронтовая константа");
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

    [Fact]
    public async Task RunAsync_Migration022_SignalsTypeEnumAcceptsMarketAndCreditEvents_AndThresholdColumnsExist()
    {
        var connectionString = _container.GetConnectionString();
        await new MigrationRunner(connectionString, NullLogger<MigrationRunner>.Instance).RunAsync();

        await using var conn = new MySqlConnection(connectionString);
        await conn.OpenAsync();

        var columnType = await conn.ExecuteScalarAsync<string>(
            @"SELECT column_type FROM information_schema.columns
              WHERE table_schema = DATABASE() AND table_name = 'signals' AND column_name = 'type'");
        foreach (var type in new[] { "SpreadWidening", "PriceDrawdown", "IssuerDefaultEvent", "UpcomingOffer" })
        {
            columnType.Should().Contain($"'{type}'", $"ENUM signals.type должен содержать {type} (миграция 022)");
        }

        foreach (var column in new[] { "spread_widening_threshold_fraction", "price_drawdown_threshold_fraction" })
        {
            var nullable = await conn.ExecuteScalarAsync<string>(
                @"SELECT is_nullable FROM information_schema.columns
                  WHERE table_schema = DATABASE() AND table_name = 'user_settings' AND column_name = @Column",
                new { Column = column });
            nullable.Should().Be("YES", $"user_settings.{column} nullable: NULL = дефолт движка событий");
        }
    }
    [Fact]
    public async Task RunAsync_Migration024_OperationsHaveNullableAccruedColumn()
    {
        var connectionString = _container.GetConnectionString();
        await new MigrationRunner(connectionString, NullLogger<MigrationRunner>.Instance).RunAsync();

        await using var conn = new MySqlConnection(connectionString);
        await conn.OpenAsync();

        var nullable = await conn.ExecuteScalarAsync<string>(
            @"SELECT is_nullable FROM information_schema.columns
              WHERE table_schema = DATABASE() AND table_name = 'operations' AND column_name = 'accrued_rub'");
        nullable.Should().Be("YES", "operations.accrued_rub nullable: NULL = брокер НКД не отдал (миграция 024)");
    }

    [Fact]
    public async Task RunAsync_Migration025_CurrencyTablesHaveExpectedShape()
    {
        var connectionString = _container.GetConnectionString();
        await new MigrationRunner(connectionString, NullLogger<MigrationRunner>.Instance).RunAsync();

        await using var conn = new MySqlConnection(connectionString);
        await conn.OpenAsync();

        async Task<(string Type, string Nullable)> Column(string table, string column)
        {
            var row = await conn.QuerySingleAsync<(string ColumnType, string IsNullable)>(
                @"SELECT COLUMN_TYPE AS ColumnType, IS_NULLABLE AS IsNullable FROM information_schema.columns
                  WHERE table_schema = DATABASE() AND table_name = @Table AND column_name = @Column",
                new { Table = table, Column = column });
            return (row.ColumnType, row.IsNullable);
        }

        (await Column("currency_operations", "type")).Type.Should().Be("enum('Buy','Sell','Tax','Fee','Income')");
        (await Column("currency_operations", "currency_id")).Nullable.Should().Be("YES", "валюту можно не резолвить - деньги не теряем");
        (await Column("currency_positions", "avg_purchase_price_rub")).Nullable.Should().Be("YES", "брокер может не знать цену входа");
        (await Column("currency_positions", "quantity")).Type.Should().StartWith("decimal(20,6)");
        (await Column("currencies", "face_value")).Type.Should().StartWith("decimal(20,6)");
        (await Column("currency_rate_history", "close_rub")).Nullable.Should().Be("NO");
        (await Column("currencies", "iso_code")).Type.Should().Be("varchar(8)");
    }

    [Fact]
    public async Task RunAsync_Migration026_FundsHaveNullableDurationOverrideYears()
    {
        var connectionString = _container.GetConnectionString();
        await new MigrationRunner(connectionString, NullLogger<MigrationRunner>.Instance).RunAsync();

        await using var conn = new MySqlConnection(connectionString);
        await conn.OpenAsync();

        var row = await conn.QuerySingleAsync<(string ColumnType, string IsNullable)>(
            @"SELECT COLUMN_TYPE AS ColumnType, IS_NULLABLE AS IsNullable FROM information_schema.columns
              WHERE table_schema = DATABASE() AND table_name = 'funds' AND column_name = 'duration_override_years'");
        row.ColumnType.Should().StartWith("decimal(9,4)", "годы, 4 знака");
        row.IsNullable.Should().Be("YES", "NULL = ручная дюрация не задана (миграция 026)");
    }

    [Fact]
    public async Task RunAsync_Migration029_CopiesExistingTInvestToken_AsIs_AndIsSafeToReapply()
    {
        var connectionString = _container.GetConnectionString();
        var runner = new MigrationRunner(connectionString, NullLogger<MigrationRunner>.Instance);
        await runner.RunAsync();

        await using var conn = new MySqlConnection(connectionString);
        await conn.OpenAsync();

        // Прод-состояние до выката 029: у двух пользователей токен в user_settings, у третьего токена нет.
        var withToken = await conn.ExecuteScalarAsync<ulong>("INSERT INTO users (telegram_id) VALUES (9001001) ON DUPLICATE KEY UPDATE telegram_id = telegram_id; SELECT id FROM users WHERE telegram_id = 9001001");
        var withToken2 = await conn.ExecuteScalarAsync<ulong>("INSERT INTO users (telegram_id) VALUES (9001002) ON DUPLICATE KEY UPDATE telegram_id = telegram_id; SELECT id FROM users WHERE telegram_id = 9001002");
        var noToken = await conn.ExecuteScalarAsync<ulong>("INSERT INTO users (telegram_id) VALUES (9001003) ON DUPLICATE KEY UPDATE telegram_id = telegram_id; SELECT id FROM users WHERE telegram_id = 9001003");
        await conn.ExecuteAsync(
            @"INSERT INTO user_settings (user_id, tinvest_token_encrypted, tinvest_token_last4) VALUES (@A, 'CIPHERTEXT-A', 'AAAA'), (@B, 'CIPHERTEXT-B', 'BBBB');
              INSERT INTO user_settings (user_id) VALUES (@C)",
            new { A = withToken, B = withToken2, C = noToken });

        async Task ReapplyMigration029()
        {
            await conn.ExecuteAsync("DELETE FROM _migrations WHERE FileName = '029_user_credentials.sql'");
            await runner.RunAsync();
        }

        await ReapplyMigration029();

        var rows = (await conn.QueryAsync<(ulong UserId, string Provider, string Secret, string Last4, string Status)>(
            "SELECT user_id, provider, secret_encrypted, secret_last4, status FROM user_credentials ORDER BY user_id")).ToList();
        rows.Should().HaveCount(2, "пользователь без токена не получает запись");
        rows.Should().Contain((withToken, "tinvest_read", "CIPHERTEXT-A", "AAAA", "ok"), "шифротекст переносится КАК ЕСТЬ");
        rows.Should().Contain((withToken2, "tinvest_read", "CIPHERTEXT-B", "BBBB", "ok"));

        // Пользователь уже заменил токен через новый UI — повторное применение миграции его не откатывает и не дублирует.
        await conn.ExecuteAsync("UPDATE user_credentials SET secret_encrypted = 'CIPHERTEXT-A2' WHERE user_id = @A", new { A = withToken });
        await ReapplyMigration029();

        (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM user_credentials")).Should().Be(2);
        (await conn.ExecuteScalarAsync<string>("SELECT secret_encrypted FROM user_credentials WHERE user_id = @A", new { A = withToken }))
            .Should().Be("CIPHERTEXT-A2");

        // Легаси-колонки не тронуты (страховочный fallback до отдельной миграции удаления).
        (await conn.ExecuteScalarAsync<string>("SELECT tinvest_token_encrypted FROM user_settings WHERE user_id = @A", new { A = withToken }))
            .Should().Be("CIPHERTEXT-A");
    }
}

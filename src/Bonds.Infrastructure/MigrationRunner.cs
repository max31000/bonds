using System.Reflection;
using Dapper;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace Bonds.Infrastructure;

public class MigrationRunner
{
    private readonly string _connectionString;
    private readonly ILogger<MigrationRunner> _logger;

    public MigrationRunner(string connectionString, ILogger<MigrationRunner> logger)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

    public async Task RunAsync()
    {
        await using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync();

        // Таблица учёта применённых миграций
        await conn.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS _migrations (
                Id          INT           NOT NULL AUTO_INCREMENT,
                FileName    VARCHAR(255)  NOT NULL,
                AppliedAt   DATETIME      NOT NULL DEFAULT CURRENT_TIMESTAMP,
                PRIMARY KEY (Id),
                UNIQUE KEY uq_migrations_filename (FileName)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci");

        var applied = (await conn.QueryAsync<string>("SELECT FileName FROM _migrations"))
            .ToHashSet();

        // Файлы миграций из EmbeddedResource (Bonds.Infrastructure/Migrations/*.sql)
        var assembly = Assembly.GetExecutingAssembly();
        var migrationFiles = assembly.GetManifestResourceNames()
            .Where(n => n.Contains(".Migrations.") && n.EndsWith(".sql"))
            .OrderBy(n => n)
            .ToList();

        foreach (var resourceName in migrationFiles)
        {
            // Имя ресурса вида Bonds.Infrastructure.Migrations.001_initial_schema.sql
            var parts = resourceName.Split('.');
            var sqlFileName = string.Join(".", parts.TakeLast(2)); // "001_initial_schema.sql"

            if (applied.Contains(sqlFileName))
            {
                _logger.LogDebug("Migration {FileName} already applied, skipping", sqlFileName);
                continue;
            }

            _logger.LogInformation("Applying migration: {FileName}", sqlFileName);

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                _logger.LogWarning("Could not load migration resource: {ResourceName}", resourceName);
                continue;
            }

            using var reader = new StreamReader(stream);
            var sql = await reader.ReadToEndAsync();

            await using var tx = await conn.BeginTransactionAsync();
            try
            {
                var statements = SplitSqlStatements(sql);
                foreach (var stmt in statements)
                {
                    if (!string.IsNullOrWhiteSpace(stmt))
                        await conn.ExecuteAsync(stmt, transaction: tx);
                }

                await conn.ExecuteAsync(
                    "INSERT INTO _migrations (FileName) VALUES (@FileName)",
                    new { FileName = sqlFileName },
                    tx);

                await tx.CommitAsync();
                _logger.LogInformation("Migration {FileName} applied successfully", sqlFileName);
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync();
                _logger.LogError(ex, "Failed to apply migration {FileName}", sqlFileName);
                throw;
            }
        }
    }

    private static List<string> SplitSqlStatements(string sql)
    {
        // Простой сплиттер по «;» — для DDL-миграций этого достаточно (как в cashpulse)
        return sql.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();
    }
}

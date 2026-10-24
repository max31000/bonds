using Bonds.Core.Agents;
using Bonds.Core.Interfaces.Repositories;
using Dapper;
using MySqlConnector;

namespace Bonds.Infrastructure.Repositories;

/// <summary>Хранилище <c>agent_settings</c> (задача 68). Нет строки — <see cref="AgentSettings.Default"/> (агент выключен).</summary>
public class AgentSettingsRepository : IAgentSettingsRepository
{
    private readonly string _connectionString;

    public AgentSettingsRepository(string connectionString) => _connectionString = connectionString;

    private sealed record Row(bool Enabled, string Model, string MonthlyModel, int DailyRunLimit);

    public async Task<AgentSettings> GetAsync(ulong userId)
    {
        using var conn = new MySqlConnection(_connectionString);
        var row = await conn.QuerySingleOrDefaultAsync<Row>(
            "SELECT enabled AS Enabled, model AS Model, monthly_model AS MonthlyModel, daily_run_limit AS DailyRunLimit FROM agent_settings WHERE user_id = @UserId",
            new { UserId = userId });
        return row is null ? AgentSettings.Default : new AgentSettings(row.Enabled, row.Model, row.MonthlyModel, row.DailyRunLimit);
    }

    public async Task UpsertAsync(ulong userId, AgentSettings settings)
    {
        using var conn = new MySqlConnection(_connectionString);
        await conn.ExecuteAsync(
            @"INSERT INTO agent_settings (user_id, enabled, model, monthly_model, daily_run_limit)
              VALUES (@UserId, @Enabled, @Model, @MonthlyModel, @DailyRunLimit)
              ON DUPLICATE KEY UPDATE enabled = VALUES(enabled), model = VALUES(model), monthly_model = VALUES(monthly_model), daily_run_limit = VALUES(daily_run_limit)",
            new { UserId = userId, settings.Enabled, settings.Model, settings.MonthlyModel, settings.DailyRunLimit });
    }

    public async Task<IReadOnlyList<ulong>> ListEnabledUserIdsAsync()
    {
        using var conn = new MySqlConnection(_connectionString);
        return (await conn.QueryAsync<ulong>("SELECT user_id FROM agent_settings WHERE enabled = 1 ORDER BY user_id")).ToList();
    }
}

/// <summary>Хранилище <c>agent_runs</c> (задача 68). Вид задачи — строкой (<see cref="AgentTaskKind"/>.ToString()).</summary>
public class AgentRunRepository : IAgentRunRepository
{
    private readonly string _connectionString;

    public AgentRunRepository(string connectionString) => _connectionString = connectionString;

    private const string Columns = @"id AS Id, user_id AS UserId, kind AS Kind, model AS Model, question AS Question, started AS Started, finished AS Finished,
        exit_code AS ExitCode, cost_usd AS CostUsd, tokens AS Tokens, result_text AS ResultText, error AS Error";

    public async Task<ulong> InsertStartedAsync(AgentRun run)
    {
        using var conn = new MySqlConnection(_connectionString);
        return await conn.ExecuteScalarAsync<ulong>(
            @"INSERT INTO agent_runs (user_id, kind, model, question, started) VALUES (@UserId, @Kind, @Model, @Question, @Started);
              SELECT LAST_INSERT_ID();",
            new { run.UserId, run.Kind, run.Model, Question = Truncate(run.Question, 1000), run.Started });
    }

    public async Task FinishAsync(ulong id, DateTime finishedUtc, int? exitCode, decimal? costUsd, long? tokens, string? resultText, string? error)
    {
        using var conn = new MySqlConnection(_connectionString);
        await conn.ExecuteAsync(
            @"UPDATE agent_runs SET finished = @Finished, exit_code = @ExitCode, cost_usd = @CostUsd, tokens = @Tokens, result_text = @ResultText, error = @Error
              WHERE id = @Id",
            new { Id = id, Finished = finishedUtc, ExitCode = exitCode, CostUsd = costUsd, Tokens = tokens, ResultText = resultText, Error = Truncate(error, 1000) });
    }

    public async Task<int> CountStartedSinceAsync(ulong userId, DateTime sinceUtc, AgentTaskKind? kind = null)
    {
        using var conn = new MySqlConnection(_connectionString);
        return await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM agent_runs WHERE user_id = @UserId AND started >= @Since AND (@Kind IS NULL OR kind = @Kind)",
            new { UserId = userId, Since = sinceUtc, Kind = kind?.ToString() });
    }

    public async Task<IReadOnlyList<AgentRun>> ListAsync(ulong userId, int limit)
    {
        using var conn = new MySqlConnection(_connectionString);
        return (await conn.QueryAsync<AgentRun>(
            $"SELECT {Columns} FROM agent_runs WHERE user_id = @UserId ORDER BY started DESC, id DESC LIMIT @Limit",
            new { UserId = userId, Limit = Math.Clamp(limit, 1, 200) })).ToList();
    }

    private static string? Truncate(string? value, int max) => value is null || value.Length <= max ? value : value[..max];
}

using Bonds.Core.Agents;

namespace Bonds.Core.Interfaces.Repositories;

/// <summary>Настройки агента (<c>agent_settings</c>, задача 68). Нет строки — <see cref="AgentSettings.Default"/> (выключен).</summary>
public interface IAgentSettingsRepository
{
    Task<AgentSettings> GetAsync(ulong userId);

    Task UpsertAsync(ulong userId, AgentSettings settings);

    /// <summary>Пользователи с включённым агентом (для расписания).</summary>
    Task<IReadOnlyList<ulong>> ListEnabledUserIdsAsync();
}

/// <summary>Журнал запусков агента (<c>agent_runs</c>, задача 68).</summary>
public interface IAgentRunRepository
{
    /// <summary>Записывает начатый запуск (до старта процесса — так он входит в суточный лимит даже при падении).</summary>
    Task<ulong> InsertStartedAsync(AgentRun run);

    Task FinishAsync(ulong id, DateTime finishedUtc, int? exitCode, decimal? costUsd, long? tokens, string? resultText, string? error);

    /// <summary>Сколько запусков пользователь начал с <paramref name="sinceUtc"/> (опционально — только вида <paramref name="kind"/>).</summary>
    Task<int> CountStartedSinceAsync(ulong userId, DateTime sinceUtc, AgentTaskKind? kind = null);

    /// <summary>Последние запуски пользователя, новые первыми.</summary>
    Task<IReadOnlyList<AgentRun>> ListAsync(ulong userId, int limit);
}

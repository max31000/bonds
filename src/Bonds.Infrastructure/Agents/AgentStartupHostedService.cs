using Bonds.Core.Agents;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bonds.Infrastructure.Agents;

/// <summary>
/// Подготовка агентов при старте API (задача 68, ревью M1/MINOR 5): корень каталогов запусков (root, 0711), удаление каталогов прошлых запусков
/// (<c>run-*</c> — остаются после падения/рестарта посреди запуска) и предупреждение, если включён <see cref="AgentOptions.AllowWebResearch"/>.
/// Сбой — только <c>LogWarning</c>: старт API не блокируется (запуск агента сам откажет, если каталог не подготовить).
/// </summary>
public sealed class AgentStartupHostedService : IHostedService
{
    private readonly IOptionsMonitor<AgentOptions> _options;
    private readonly ILogger<AgentStartupHostedService> _logger;

    public AgentStartupHostedService(IOptionsMonitor<AgentOptions> options, ILogger<AgentStartupHostedService> logger)
    {
        _options = options;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Prepare(_options.CurrentValue, _logger);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Возвращает число удалённых каталогов прошлых запусков (−1 — подготовка не удалась).</summary>
    public static int Prepare(AgentOptions options, ILogger logger)
    {
        if (options.AllowWebResearch)
        {
            logger.LogWarning(
                "Agent:AllowWebResearch is enabled — NOT recommended: WebSearch/WebFetch run in the same session as portfolio data (exfiltration channel). " +
                "Keep it off until the agent runs in a separate worker container.");
        }

        if (!options.Enabled) return 0;
        try
        {
            var root = options.ResolvedRunRoot;
            AgentFileSystem.EnsureRunRoot(root);
            var removed = AgentFileSystem.CleanupStaleRunDirectories(root);
            if (removed > 0) logger.LogInformation("Agent: removed {Count} stale run directories", removed);
            return removed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Agent: run root preparation failed ({ErrorType})", ex.GetType().Name);
            return -1;
        }
    }
}

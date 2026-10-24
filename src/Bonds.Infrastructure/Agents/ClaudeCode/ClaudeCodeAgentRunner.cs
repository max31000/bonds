using System.ComponentModel;
using Bonds.Core.Agents;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bonds.Infrastructure.Agents.ClaudeCode;

/// <summary>
/// <see cref="IAgentRunner"/> через Claude Code CLI (задача 68). На каждый запуск:
/// <list type="number">
/// <item>каталог запуска в <see cref="AgentOptions.ResolvedRunRoot"/> (0700, владелец — uid агента) = <c>HOME</c> и рабочий каталог процесса; в нём MCP-конфиг
/// (0600) с bearer-токеном запуска (<see cref="AgentFileSystem"/>);</item>
/// <item>на Linux CLI запускается через <c>setpriv</c> под непривилегированным <c>agent</c> (<see cref="ClaudeCodeCli.SetprivLaunch"/>): не читает env API
/// (<c>/proc/1/environ</c>) и ключи DataProtection;</item>
/// <item>процесс с окружением С НУЛЯ (<see cref="ClaudeCodeCli.BuildEnvironment"/>: токен подписки только в env, без <c>ANTHROPIC_API_KEY</c> и переменных хоста,
/// кроме <c>PATH</c>), задание — через stdin;</item>
/// <item>таймаут — kill дерева процессов (<see cref="IAgentProcessExecutor"/>);</item>
/// <item>каталог удаляется в <c>finally</c> (конфиг с токеном не переживает запуск).</item>
/// </list>
/// Токен и bearer вычищаются из любого текста, который уходит наружу (ответ, ошибка). В логи пишутся только вид задачи, код выхода и длительность.
/// </summary>
public sealed class ClaudeCodeAgentRunner : IAgentRunner
{
    private readonly IAgentProcessExecutor _executor;
    private readonly IOptionsMonitor<AgentOptions> _options;
    private readonly ILogger<ClaudeCodeAgentRunner> _logger;

    public ClaudeCodeAgentRunner(IAgentProcessExecutor executor, IOptionsMonitor<AgentOptions> options, ILogger<ClaudeCodeAgentRunner> logger)
    {
        _executor = executor;
        _options = options;
        _logger = logger;
    }

    /// <summary>Каталог последнего запуска (для тестов: проверка, что он удалён).</summary>
    internal string? LastHomeDir { get; private set; }

    public async Task<AgentResult> RunAsync(AgentTask task, AgentCredentials credentials, CancellationToken ct = default)
    {
        var options = _options.CurrentValue;
        var setpriv = options.ShouldUseSetpriv ? new ClaudeCodeCli.SetprivLaunch(options.SetprivPath, options.RunAsUid, options.RunAsGid) : null;
        string? home = null;
        LastHomeDir = null;
        string Redact(string text) => Redactor(text, credentials.OAuthToken, task.McpBearer);
        var started = DateTime.UtcNow;
        try
        {
            // Каталог запуска (HOME и cwd CLI): 0700, владелец — uid агента; MCP-конфиг с bearer — 0600 того же владельца (ревью M1).
            var root = options.ResolvedRunRoot;
            if (!Directory.Exists(root)) AgentFileSystem.EnsureRunRoot(root);
            home = AgentFileSystem.CreateRunDirectory(root, setpriv?.Uid, setpriv?.Gid);
            LastHomeDir = home;
            var mcpConfigPath = Path.Combine(home, "mcp.json");
            await AgentFileSystem.WritePrivateFileAsync(mcpConfigPath, ClaudeCodeCli.BuildMcpConfig(task.McpUrl, task.McpBearer), setpriv?.Uid, setpriv?.Gid, ct);

            var psi = ClaudeCodeCli.CreateStartInfo(
                options.CliPath, task, credentials.OAuthToken, home, mcpConfigPath, Environment.GetEnvironmentVariable("PATH"), setpriv);
            var outcome = await _executor.RunAsync(psi, task.Prompt, task.Timeout, options.MaxOutputBytes, ct);

            AgentResult result;
            if (outcome.TimedOut)
            {
                result = new AgentResult(string.Empty, null, -1, IsError: true, TimedOut: true,
                    Error: $"Превышено время запуска ({(int)task.Timeout.TotalSeconds} с) — процесс остановлен");
            }
            else
            {
                result = ClaudeCodeCli.ParseOutput(outcome.Stdout, outcome.ExitCode, Redact);
                if (!result.Succeeded && string.IsNullOrWhiteSpace(result.Error) && outcome.Stderr.Length > 0)
                    result = result with { Error = Shorten(Redact(outcome.Stderr)) };
            }

            _logger.LogInformation(
                "Agent run {Kind} for user {UserId}: exit {ExitCode}, timed out {TimedOut}, {Elapsed} ms",
                task.Kind, task.UserId, result.ExitCode, result.TimedOut, (long)(DateTime.UtcNow - started).TotalMilliseconds);
            return result;
        }
        catch (Win32Exception ex)
        {
            // Нет исполняемого файла CLI (образ без Node/CLI, неверный Agent:CliPath).
            _logger.LogWarning("Agent run {Kind}: CLI start failed ({ErrorType})", task.Kind, ex.GetType().Name);
            return new AgentResult(string.Empty, null, -1, IsError: true, Error: "Claude Code CLI не запустился — проверьте установку на сервере");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Не удалось подготовить изолированный каталог (chown/права) — без него не запускаем.
            _logger.LogWarning("Agent run {Kind}: run directory setup failed ({ErrorType})", task.Kind, ex.GetType().Name);
            return new AgentResult(string.Empty, null, -1, IsError: true, Error: "Не удалось подготовить изолированный каталог запуска агента");
        }
        finally
        {
            if (home is not null) TryDelete(home);
        }
    }

    /// <summary>Убирает секреты запуска из текста процесса.</summary>
    internal static string Redactor(string text, params string?[] secrets)
    {
        foreach (var secret in secrets)
        {
            if (!string.IsNullOrEmpty(secret) && secret.Length >= 8) text = text.Replace(secret, "***", StringComparison.Ordinal);
        }

        return text;
    }

    private static string Shorten(string s) => s.Length <= 500 ? s : s[..500] + "…";

    private void TryDelete(string home)
    {
        try
        {
            if (Directory.Exists(home)) Directory.Delete(home, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Agent run: temp directory cleanup failed ({ErrorType})", ex.GetType().Name);
        }
    }
}

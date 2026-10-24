using System.ComponentModel;
using Bonds.Core.Agents;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bonds.Infrastructure.Agents.ClaudeCode;

/// <summary>
/// <see cref="IAgentRunner"/> через Claude Code CLI (задача 68). На каждый запуск:
/// <list type="number">
/// <item>временный каталог (0700) = <c>HOME</c> и рабочий каталог процесса; в нём MCP-конфиг (0600) с bearer-токеном запуска;</item>
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
        var home = Path.Combine(Path.GetTempPath(), "bonds-agent-" + Guid.NewGuid().ToString("N"));
        LastHomeDir = home;
        string Redact(string text) => Redactor(text, credentials.OAuthToken, task.McpBearer);
        var started = DateTime.UtcNow;
        try
        {
            CreatePrivateDirectory(home);
            var mcpConfigPath = Path.Combine(home, "mcp.json");
            await WritePrivateFileAsync(mcpConfigPath, ClaudeCodeCli.BuildMcpConfig(task.McpUrl, task.McpBearer), ct);

            var psi = ClaudeCodeCli.CreateStartInfo(options.CliPath, task, credentials.OAuthToken, home, mcpConfigPath, Environment.GetEnvironmentVariable("PATH"));
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
        finally
        {
            TryDelete(home);
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

    private static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
        else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static async Task WritePrivateFileAsync(string path, string content, CancellationToken ct)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using var stream = new FileStream(path, options);
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(content.AsMemory(), ct);
    }

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

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Bonds.Core.Agents;

namespace Bonds.Infrastructure.Agents.ClaudeCode;

/// <summary>
/// Чистые функции запуска Claude Code CLI (задача 68; флаги сверены с <c>claude --help</c> версии из Dockerfile, 2.1.296):
/// <c>claude -p --output-format json --strict-mcp-config --mcp-config &lt;файл&gt; --tools "" --allowedTools mcp__bonds__* --permission-prompts none --max-turns N</c>.
/// <list type="bullet">
/// <item><b>Задание — через stdin</b>, не аргументом: <c>--tools</c>/<c>--allowedTools</c>/<c>--mcp-config</c> вариадические (съели бы текст), а argv виден в <c>ps</c>.</item>
/// <item><c>--tools ""</c> выключает ВСЕ встроенные инструменты (Bash, файлы, веб); исследовательским задачам — только <c>WebSearch,WebFetch</c>.</item>
/// <item><c>--strict-mcp-config</c> — только наш MCP-сервер bonds; <c>--permission-prompts none</c> — всё, что потребовало бы разрешения, отклоняется.</item>
/// <item><c>--bare</c> НЕ используется (в нём не работает OAuth-токен подписки).</item>
/// </list>
/// </summary>
public static class ClaudeCodeCli
{
    /// <summary>Имя MCP-сервера в конфиге — от него зависят имена инструментов (<c>mcp__bonds__portfolio</c>).</summary>
    public const string McpServerName = "bonds";

    public const string OAuthTokenVariable = "CLAUDE_CODE_OAUTH_TOKEN";

    /// <summary>Переменные, которые НИКОГДА не попадают процессу агента (иначе CLI выбрал бы API-ключ вместо подписки пользователя).</summary>
    public static readonly IReadOnlyList<string> ForbiddenVariables = ["ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_BASE_URL"];

    public static IReadOnlyList<string> BuildArguments(AgentTask task, string mcpConfigPath)
    {
        var builtIn = task.AllowWebResearch ? "WebSearch,WebFetch" : string.Empty;
        var allowed = task.AllowWebResearch ? $"mcp__{McpServerName}__*,WebSearch,WebFetch" : $"mcp__{McpServerName}__*";
        return
        [
            "-p",
            "--output-format", "json",
            "--strict-mcp-config",
            "--mcp-config", mcpConfigPath,
            "--tools", builtIn,
            "--allowedTools", allowed,
            "--permission-prompts", "none",
            "--max-turns", task.MaxTurns.ToString(CultureInfo.InvariantCulture),
            "--model", task.Model,
            "--no-session-persistence",
        ];
    }

    /// <summary>
    /// Окружение процесса агента — собирается С НУЛЯ (переменные хоста не наследуются): токен подписки, <c>HOME</c> = временный каталог запуска, <c>PATH</c>
    /// хоста и постоянные флаги CLI (без автообновления и телеметрии). На Windows (только локальный запуск разработчика) добавляются <c>USERPROFILE</c> = тот же
    /// каталог и системные <c>SystemRoot</c>/<c>windir</c> (без них не стартует ни один процесс). <see cref="ForbiddenVariables"/> не попадают никогда.
    /// </summary>
    public static IReadOnlyDictionary<string, string> BuildEnvironment(string oauthToken, string homeDir, string? hostPath)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [OAuthTokenVariable] = oauthToken,
            ["HOME"] = homeDir,
            ["PATH"] = hostPath ?? string.Empty,
            ["TMPDIR"] = homeDir,
            ["DISABLE_AUTOUPDATER"] = "1",
            ["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1",
        };
        if (OperatingSystem.IsWindows())
        {
            env["USERPROFILE"] = homeDir;
            env["TEMP"] = homeDir;
            env["TMP"] = homeDir;
            foreach (var name in new[] { "SystemRoot", "windir" })
            {
                if (Environment.GetEnvironmentVariable(name) is { } value) env[name] = value;
            }
        }

        foreach (var forbidden in ForbiddenVariables) env.Remove(forbidden);
        return env;
    }

    /// <summary>
    /// <see cref="ProcessStartInfo"/> запуска: аргументы списком (без оболочки), окружение очищено и заполнено <see cref="BuildEnvironment"/>, рабочий каталог —
    /// временный каталог запуска (нет проектных настроек/CLAUDE.md), stdin/stdout/stderr перенаправлены.
    /// </summary>
    public static ProcessStartInfo CreateStartInfo(
        string cliPath, AgentTask task, string oauthToken, string homeDir, string mcpConfigPath, string? hostPath, SetprivLaunch? setpriv = null)
    {
        var psi = new ProcessStartInfo(setpriv?.SetprivPath ?? cliPath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = homeDir,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        if (setpriv is not null)
        {
            foreach (var arg in setpriv.PrefixArguments()) psi.ArgumentList.Add(arg);
            psi.ArgumentList.Add(cliPath);
        }

        foreach (var arg in BuildArguments(task, mcpConfigPath)) psi.ArgumentList.Add(arg);

        // Environment изначально — копия окружения хоста: очищаем полностью.
        psi.Environment.Clear();
        foreach (var (key, value) in BuildEnvironment(oauthToken, homeDir, hostPath)) psi.Environment[key] = value;
        return psi;
    }

    /// <summary>
    /// Запуск CLI под непривилегированным пользователем (ревью M1): <c>setpriv --reuid=U --regid=G --clear-groups --no-new-privs --inh-caps=-all --bounding-set=-all --
    /// claude …</c>. setpriv делает <c>exec</c> (тот же pid — kill дерева по таймауту работает как прежде). Сброс bounding set — пока ещё root (CAP_SETPCAP), затем
    /// смена uid/gid, без дополнительных групп и с <c>no_new_privs</c> (setuid-бинари не поднимут права).
    /// </summary>
    public sealed record SetprivLaunch(string SetprivPath, int Uid, int Gid)
    {
        public IReadOnlyList<string> PrefixArguments() =>
        [
            "--reuid=" + Uid.ToString(CultureInfo.InvariantCulture),
            "--regid=" + Gid.ToString(CultureInfo.InvariantCulture),
            "--clear-groups",
            "--no-new-privs",
            "--inh-caps=-all",
            "--bounding-set=-all",
            "--",
        ];
    }

    /// <summary>JSON-конфиг MCP (<c>--mcp-config</c>): один HTTP-сервер bonds с bearer-токеном запуска в заголовке. Пишется в файл каталога запуска (0600).</summary>
    public static string BuildMcpConfig(string mcpUrl, string bearer) =>
        JsonSerializer.Serialize(new
        {
            mcpServers = new Dictionary<string, object>
            {
                [McpServerName] = new
                {
                    type = "http",
                    url = mcpUrl,
                    headers = new Dictionary<string, string> { ["Authorization"] = "Bearer " + bearer },
                },
            },
        });

    /// <summary>
    /// Разбор stdout <c>--output-format json</c>: <c>{"type":"result","is_error":…,"result":"…","total_cost_usd":…,"usage":{"input_tokens":…,"output_tokens":…}}</c>.
    /// Не JSON — ошибка «непонятный вывод». Секреты из текста вычищаются <paramref name="redact"/>.
    /// </summary>
    public static AgentResult ParseOutput(string stdout, int exitCode, Func<string, string> redact)
    {
        var trimmed = stdout.Trim();
        // CLI печатает один JSON-объект; на случай служебных строк до него берём последнюю строку, начинающуюся с «{».
        var start = trimmed.StartsWith('{') ? 0 : trimmed.LastIndexOf("\n{", StringComparison.Ordinal) + 1;
        if (trimmed.Length == 0 || start < 0 || start >= trimmed.Length || trimmed[start] != '{')
            return new AgentResult(string.Empty, null, exitCode == 0 ? -1 : exitCode, IsError: true, Error: "Claude Code не вернул результат (exit " + exitCode + ")");

        try
        {
            using var doc = JsonDocument.Parse(trimmed[start..]);
            var root = doc.RootElement;
            var text = root.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() ?? string.Empty : string.Empty;
            var isError = root.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True;
            decimal? cost = root.TryGetProperty("total_cost_usd", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetDecimal(out var cv) ? cv : null;
            long? tokens = null;
            if (root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
            {
                long sum = 0;
                foreach (var name in new[] { "input_tokens", "output_tokens", "cache_creation_input_tokens", "cache_read_input_tokens" })
                {
                    if (u.TryGetProperty(name, out var t) && t.ValueKind == JsonValueKind.Number && t.TryGetInt64(out var tv)) sum += tv;
                }

                tokens = sum;
            }

            text = redact(text);
            var failed = isError || exitCode != 0;
            return new AgentResult(failed ? string.Empty : text, cost, exitCode, tokens, isError, failed ? Shorten(text.Length > 0 ? text : "ошибка Claude Code (exit " + exitCode + ")") : null);
        }
        catch (JsonException)
        {
            return new AgentResult(string.Empty, null, exitCode == 0 ? -1 : exitCode, IsError: true, Error: "Claude Code вернул непонятный вывод (exit " + exitCode + ")");
        }
    }

    private static string Shorten(string s) => s.Length <= 500 ? s : s[..500] + "…";
}

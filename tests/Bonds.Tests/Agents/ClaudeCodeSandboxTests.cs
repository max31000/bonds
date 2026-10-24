using Bonds.Core.Agents;
using Bonds.Infrastructure.Agents;
using Bonds.Infrastructure.Agents.ClaudeCode;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Bonds.Tests.Agents;

/// <summary>
/// Необязательная проверка с НАСТОЯЩИМ Claude Code CLI (задача 68). В CI и pre-push исключена (<c>Category!=Sandbox</c>); без env ничего не делает.
/// Запуск: <c>CLAUDE_CODE_OAUTH_TOKEN_TEST=… [CLAUDE_CLI_PATH=…] dotnet test tests/Bonds.Tests --filter "FullyQualifiedName~ClaudeCodeSandboxTests"</c>.
/// MCP-сервер в этой проверке недоступен (адрес-заглушка) — проверяются флаги, авторизация подпиской через env и разбор JSON-ответа.
/// </summary>
[Trait("Category", "Sandbox")]
public class ClaudeCodeSandboxTests
{
    [Fact]
    public async Task RealCli_AnswersViaSubscriptionToken_WithRestrictedTools()
    {
        var token = Environment.GetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN_TEST");
        if (string.IsNullOrWhiteSpace(token)) return;

        var options = new AgentOptions { CliPath = Environment.GetEnvironmentVariable("CLAUDE_CLI_PATH") ?? "claude", Timeout = TimeSpan.FromMinutes(3) };
        var runner = new ClaudeCodeAgentRunner(
            new AgentProcessExecutor(NullLogger<AgentProcessExecutor>.Instance), new OptionsMonitorStub<AgentOptions>(options), NullLogger<ClaudeCodeAgentRunner>.Instance);

        var result = await runner.RunAsync(
            new AgentTask(AgentTaskKind.OnDemand, 0, "Ответь одним словом: готово. Инструменты не вызывай.", "haiku", 2, options.Timeout,
                "http://127.0.0.1:9/internal/mcp/0", "sandbox-bearer-not-used-000000"),
            new AgentCredentials(token));

        result.Succeeded.Should().BeTrue(result.Error);
        result.Text.Should().NotBeNullOrWhiteSpace();
        result.Text.Should().NotContain(token);
    }
}

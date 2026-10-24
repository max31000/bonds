using System.ComponentModel;
using System.Diagnostics;
using Bonds.Core.Agents;
using Bonds.Infrastructure.Agents;
using Bonds.Infrastructure.Agents.ClaudeCode;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bonds.Tests.Agents;

public class ClaudeCodeAgentRunnerTests
{
    private const string Token = "sk-ant-oat01-RUNNER-SECRET-0000000000";
    private const string Bearer = "bearer-RUNNER-SECRET-123456789";

    private sealed class CapturingExecutor : IAgentProcessExecutor
    {
        public ProcessStartInfo? Psi;
        public string? Stdin;
        public string? McpConfigSeen;
        public bool HomeExistedDuringRun;
        public AgentProcessOutcome Outcome = new(0, """{"type":"result","is_error":false,"result":"ok","total_cost_usd":0.01}""", string.Empty, false);
        public Exception? Throw;

        public Task<AgentProcessOutcome> RunAsync(ProcessStartInfo psi, string stdin, TimeSpan timeout, int maxOutputBytes, CancellationToken ct)
        {
            if (Throw is not null) throw Throw;
            Psi = psi;
            Stdin = stdin;
            HomeExistedDuringRun = Directory.Exists(psi.WorkingDirectory);
            var mcpIndex = psi.ArgumentList.IndexOf("--mcp-config");
            McpConfigSeen = File.ReadAllText(psi.ArgumentList[mcpIndex + 1]);
            return System.Threading.Tasks.Task.FromResult(Outcome);
        }
    }

    private static (ClaudeCodeAgentRunner Runner, CapturingExecutor Exec) Create()
    {
        var exec = new CapturingExecutor();
        var options = new OptionsMonitorStub<AgentOptions>(new AgentOptions { CliPath = "claude", UseSetpriv = false, RunRoot = Path.Combine(Path.GetTempPath(), "bonds-agent-tests") });
        return (new ClaudeCodeAgentRunner(exec, options, NullLogger<ClaudeCodeAgentRunner>.Instance), exec);
    }

    private static AgentTask NewTask() => new(AgentTaskKind.DailyDigest, 1, "дайджест", "sonnet", 10, TimeSpan.FromSeconds(30), "http://127.0.0.1:5001/internal/mcp/9", Bearer);

    [Fact]
    public async Task Run_PassesTokenOnlyViaEnv_PromptViaStdin_AndDeletesTempHome()
    {
        var (runner, exec) = Create();

        var result = await runner.RunAsync(NewTask(), new AgentCredentials(Token));

        result.Succeeded.Should().BeTrue();
        result.Text.Should().Be("ok");
        exec.Psi!.Environment[ClaudeCodeCli.OAuthTokenVariable].Should().Be(Token);
        exec.Psi.Environment.Keys.Should().NotContain("ANTHROPIC_API_KEY");
        exec.Psi.ArgumentList.Should().NotContain(a => a.Contains(Token, StringComparison.Ordinal) || a.Contains(Bearer, StringComparison.Ordinal));
        exec.Psi.Environment["HOME"].Should().Be(exec.Psi.WorkingDirectory);
        exec.Stdin.Should().Be("дайджест");
        exec.McpConfigSeen.Should().Contain("Bearer " + Bearer);
        exec.HomeExistedDuringRun.Should().BeTrue();
        Directory.Exists(runner.LastHomeDir).Should().BeFalse("временный HOME с MCP-конфигом удаляется после запуска");
    }

    [Fact]
    public async Task Run_Timeout_ReturnsTimedOut_AndDeletesHome()
    {
        var (runner, exec) = Create();
        exec.Outcome = new AgentProcessOutcome(-1, string.Empty, string.Empty, TimedOut: true);

        var result = await runner.RunAsync(NewTask(), new AgentCredentials(Token));

        result.TimedOut.Should().BeTrue();
        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("Превышено время");
        Directory.Exists(runner.LastHomeDir).Should().BeFalse();
    }

    [Fact]
    public async Task Run_StderrWithSecrets_IsRedacted()
    {
        var (runner, exec) = Create();
        exec.Outcome = new AgentProcessOutcome(2, string.Empty, $"auth failed for {Token} via {Bearer}", false);

        var result = await runner.RunAsync(NewTask(), new AgentCredentials(Token));

        result.Succeeded.Should().BeFalse();
        result.Error.Should().NotContain(Token).And.NotContain(Bearer);
    }

    [Fact]
    public async Task Run_CliMissing_ReturnsError_NoThrow()
    {
        var (runner, exec) = Create();
        exec.Throw = new Win32Exception(2, "No such file");

        var result = await runner.RunAsync(NewTask(), new AgentCredentials(Token));

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("CLI");
        Directory.Exists(runner.LastHomeDir).Should().BeFalse();
    }

    [Fact]
    public void Credentials_ToString_DoesNotLeakToken()
    {
        new AgentCredentials(Token).ToString().Should().NotContain(Token);
        NewTask().ToString().Should().NotContain(Bearer);
    }
}

internal sealed class OptionsMonitorStub<T> : IOptionsMonitor<T>
{
    public OptionsMonitorStub(T value) => CurrentValue = value;
    public T CurrentValue { get; set; }
    public T Get(string? name) => CurrentValue;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

internal sealed class ManualTimeProvider : TimeProvider
{
    public ManualTimeProvider(DateTime utcNow) => UtcNow = utcNow;
    public DateTime UtcNow { get; set; }
    public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(UtcNow, DateTimeKind.Utc));
}

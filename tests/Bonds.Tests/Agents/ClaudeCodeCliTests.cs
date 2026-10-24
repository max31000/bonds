using System.Text.Json;
using Bonds.Core.Agents;
using Bonds.Infrastructure.Agents.ClaudeCode;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Agents;

public class ClaudeCodeCliTests
{
    private const string Token = "sk-ant-oat01-SECRET-TOKEN-VALUE-1234567890";
    private const string Bearer = "mcp-bearer-SECRET-abcdefghijklmnop";

    private static AgentTask NewTask(bool web = false) => new(
        AgentTaskKind.OnDemand, 7, "вопрос пользователя", "sonnet", 12, TimeSpan.FromMinutes(5), "http://127.0.0.1:5001/internal/mcp/42", Bearer, web);

    [Fact]
    public void Arguments_HaveRequiredFlags_RestrictTools_AndNoBareNoPrompt()
    {
        var args = ClaudeCodeCli.BuildArguments(NewTask(), "/tmp/run/mcp.json");

        args.Should().ContainInOrder("-p", "--output-format", "json");
        args.Should().Contain("--strict-mcp-config");
        args.Should().ContainInOrder("--mcp-config", "/tmp/run/mcp.json");
        args.Should().ContainInOrder("--tools", string.Empty);
        args.Should().ContainInOrder("--allowedTools", "mcp__bonds__*");
        args.Should().ContainInOrder("--permission-prompts", "none");
        args.Should().ContainInOrder("--max-turns", "12");
        args.Should().ContainInOrder("--model", "sonnet");
        args.Should().NotContain("--bare");
        args.Should().NotContain(a => a.Contains("вопрос", StringComparison.Ordinal), "задание уходит через stdin, а не argv");
        args.Should().NotContain(a => a.Contains(Token, StringComparison.Ordinal) || a.Contains(Bearer, StringComparison.Ordinal));
    }

    [Fact]
    public void Arguments_WebResearch_AllowsOnlyWebSearchAndFetch()
    {
        var args = ClaudeCodeCli.BuildArguments(NewTask(web: true), "/tmp/mcp.json");
        args.Should().ContainInOrder("--tools", "WebSearch,WebFetch");
        args.Should().ContainInOrder("--allowedTools", "mcp__bonds__*,WebSearch,WebFetch");
    }

    [Fact]
    public void Environment_OnlyTokenHomePath_NoApiKey()
    {
        var env = ClaudeCodeCli.BuildEnvironment(Token, "/tmp/home", "/usr/bin");

        env[ClaudeCodeCli.OAuthTokenVariable].Should().Be(Token);
        env["HOME"].Should().Be("/tmp/home");
        env["PATH"].Should().Be("/usr/bin");
        env.Keys.Should().NotContain(ClaudeCodeCli.ForbiddenVariables);
        var allowed = new HashSet<string> { "CLAUDE_CODE_OAUTH_TOKEN", "HOME", "PATH", "TMPDIR", "DISABLE_AUTOUPDATER", "CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC" };
        if (OperatingSystem.IsWindows()) allowed.UnionWith(["USERPROFILE", "TEMP", "TMP", "SystemRoot", "windir"]);
        env.Keys.Should().BeSubsetOf(allowed);
    }

    [Fact]
    public void StartInfo_DoesNotInheritHostEnvironment_NorApiKey_AndTokenNotInArgv()
    {
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "sk-ant-api-HOST-KEY");
        Environment.SetEnvironmentVariable("BONDS_TEST_HOST_SECRET", "host-secret");
        try
        {
            var psi = ClaudeCodeCli.CreateStartInfo("claude", NewTask(), Token, "/tmp/home", "/tmp/home/mcp.json", "/usr/bin");

            psi.Environment.Keys.Should().NotContain("ANTHROPIC_API_KEY");
            psi.Environment.Keys.Should().NotContain("BONDS_TEST_HOST_SECRET");
            psi.Environment.Values.Should().NotContain("sk-ant-api-HOST-KEY");
            psi.Environment[ClaudeCodeCli.OAuthTokenVariable].Should().Be(Token);
            psi.ArgumentList.Should().NotContain(a => a.Contains(Token, StringComparison.Ordinal));
            psi.Arguments.Should().BeEmpty();
            psi.UseShellExecute.Should().BeFalse();
            psi.WorkingDirectory.Should().Be("/tmp/home");
            psi.RedirectStandardInput.Should().BeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
            Environment.SetEnvironmentVariable("BONDS_TEST_HOST_SECRET", null);
        }
    }

    [Fact]
    public void McpConfig_IsHttpServerWithBearerHeader()
    {
        using var doc = JsonDocument.Parse(ClaudeCodeCli.BuildMcpConfig("http://127.0.0.1:5001/internal/mcp/42", Bearer));
        var server = doc.RootElement.GetProperty("mcpServers").GetProperty("bonds");
        server.GetProperty("type").GetString().Should().Be("http");
        server.GetProperty("url").GetString().Should().Be("http://127.0.0.1:5001/internal/mcp/42");
        server.GetProperty("headers").GetProperty("Authorization").GetString().Should().Be("Bearer " + Bearer);
    }

    [Fact]
    public void ParseOutput_Success_ReadsTextCostTokens()
    {
        const string stdout = """
            {"type":"result","subtype":"success","is_error":false,"result":"Всё спокойно","total_cost_usd":0.0123,"num_turns":3,
             "usage":{"input_tokens":100,"output_tokens":50,"cache_creation_input_tokens":10,"cache_read_input_tokens":5}}
            """;
        var result = ClaudeCodeCli.ParseOutput(stdout, 0, s => s);

        result.Succeeded.Should().BeTrue();
        result.Text.Should().Be("Всё спокойно");
        result.CostUsd.Should().Be(0.0123m);
        result.Tokens.Should().Be(165);
    }

    [Fact]
    public void ParseOutput_IsError_ReturnsErrorText_Redacted()
    {
        var stdout = $$"""{"type":"result","is_error":true,"result":"Invalid token {{Token}}","total_cost_usd":0}""";
        var result = ClaudeCodeCli.ParseOutput(stdout, 1, s => ClaudeCodeAgentRunner.Redactor(s, Token));

        result.Succeeded.Should().BeFalse();
        result.Text.Should().BeEmpty();
        result.Error.Should().Contain("Invalid token ***").And.NotContain(Token);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{broken")]
    public void ParseOutput_Garbage_IsError(string stdout)
    {
        var result = ClaudeCodeCli.ParseOutput(stdout, 0, s => s);
        result.Succeeded.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
    }
}

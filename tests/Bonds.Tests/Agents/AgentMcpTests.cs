using System.Text.Json;
using Bonds.Core.Agents;
using Bonds.Infrastructure.Agents.Mcp;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Bonds.Tests.Agents;

public class AgentMcpTokensTests
{
    private static readonly DateTime Now = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Issue_Validate_BoundToRunAndUser()
    {
        var time = new ManualTimeProvider(Now);
        var tokens = new AgentMcpTokens(time);
        var a = tokens.Issue(userId: 1, runId: 10, TimeSpan.FromMinutes(5), AgentTaskKind.DailyDigest);
        var b = tokens.Issue(userId: 2, runId: 20, TimeSpan.FromMinutes(5), AgentTaskKind.DailyDigest);

        a.Should().NotBe(b);
        a.Length.Should().BeGreaterThanOrEqualTo(43, "32 случайных байта в base64url");
        tokens.Validate(a, 10).Should().Be(1UL);
        tokens.Validate(b, 20).Should().Be(2UL);
        tokens.Validate(b, 10).Should().BeNull("токен другого пользователя/запуска к этому запуску не подходит");
        tokens.Validate("wrong", 10).Should().BeNull();
        tokens.Validate(null, 10).Should().BeNull();
    }

    [Fact]
    public void Expired_And_Revoked_AreRejected()
    {
        var time = new ManualTimeProvider(Now);
        var tokens = new AgentMcpTokens(time);
        var t = tokens.Issue(1, 10, TimeSpan.FromMinutes(5), AgentTaskKind.DailyDigest);
        var r = tokens.Issue(1, 11, TimeSpan.FromMinutes(5), AgentTaskKind.DailyDigest);

        tokens.Revoke(r);
        tokens.Validate(r, 11).Should().BeNull();

        time.UtcNow = Now.AddMinutes(6);
        tokens.Validate(t, 10).Should().BeNull();
    }
}

public class BondsMcpServerTests
{
    private static BondsMcpServer Server()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        var tools = new BondsMcpTools(sp, new OptionsMonitorStub<AgentOptions>(new AgentOptions()), TimeProvider.System, NullLogger<BondsMcpTools>.Instance);
        return new BondsMcpServer(tools, NullLogger<BondsMcpServer>.Instance);
    }

    private static JsonElement Msg(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public async Task Initialize_EchoesSupportedVersion_AndAdvertisesTools()
    {
        var response = await Server().HandleAsync(Msg("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{}}}"""), 1);

        response!["result"]!["protocolVersion"]!.GetValue<string>().Should().Be("2025-03-26");
        response["result"]!["capabilities"]!["tools"].Should().NotBeNull();
        response["id"]!.GetValue<int>().Should().Be(1);
    }

    [Fact]
    public async Task ToolsList_ContainsReadOnlyToolsAndProposePlan_Only()
    {
        var response = await Server().HandleAsync(Msg("""{"jsonrpc":"2.0","id":"a","method":"tools/list"}"""), 1);
        var names = response!["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToList();

        names.Should().BeEquivalentTo(
            ["portfolio", "baskets", "plans", "signals", "cashflow_next_90d", "bond_card", "scorecard", "freedom_holdings", "cash", "propose_plan"]);
        names.Should().NotContain(n => n.Contains("approve") || n.Contains("order") || n.Contains("setting") || n.Contains("limit"));
    }

    [Fact]
    public async Task Notification_HasNoResponse_UnknownMethod_IsError()
    {
        var server = Server();
        (await server.HandleAsync(Msg("""{"jsonrpc":"2.0","method":"notifications/initialized"}"""), 1)).Should().BeNull();

        var unknown = await server.HandleAsync(Msg("""{"jsonrpc":"2.0","id":2,"method":"resources/list"}"""), 1);
        unknown!["error"]!["code"]!.GetValue<int>().Should().Be(-32601);

        var batch = await server.HandleAsync(Msg("""[{"jsonrpc":"2.0","id":3,"method":"ping"}]"""), 1);
        batch!["error"]!["code"]!.GetValue<int>().Should().Be(-32600);
    }

    [Fact]
    public async Task ToolsCall_UnknownTool_IsProtocolError_BadIsin_IsToolError()
    {
        var server = Server();
        var unknown = await server.HandleAsync(Msg("""{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"place_order","arguments":{}}}"""), 1);
        unknown!["error"]!["code"]!.GetValue<int>().Should().Be(-32602);

        var bad = await server.HandleAsync(Msg("""{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"bond_card","arguments":{"isin":"'; DROP"}}}"""), 1);
        bad!["result"]!["isError"]!.GetValue<bool>().Should().BeTrue();
        bad["result"]!["content"]![0]!["text"]!.GetValue<string>().Should().Contain("ISIN");
    }

    [Fact]
    public async Task ProposePlan_WithoutRationale_IsToolError()
    {
        var bad = await Server().HandleAsync(
            Msg("""{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"propose_plan","arguments":{"kind":"inflow","amount":1000}}}"""), 1);
        bad!["result"]!["isError"]!.GetValue<bool>().Should().BeTrue();
        bad["result"]!["content"]![0]!["text"]!.GetValue<string>().Should().Contain("rationale");
    }
}

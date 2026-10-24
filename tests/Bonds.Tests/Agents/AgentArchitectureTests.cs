using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Agents;

/// <summary>
/// Задача 68: код агента (раннер, MCP-сервер и инструменты, оркестратор, очередь, эндпоинт MCP) НЕ может торговать, подтверждать планы, менять лимиты, настройки,
/// режим исполнения или секреты. Сканируются исходники (как <c>NoOrderPlacementArchitectureTests</c>; комментарии тоже). Единственный пишущий путь — <c>propose_plan</c>
/// → <c>PolicyService.CreateManualAsync</c> (план всегда PendingApproval). Менять этот тест — осознанно.
/// </summary>
public class AgentArchitectureTests
{
    private static readonly string[] AgentPaths =
    [
        "Bonds.Core/Agents/",
        "Bonds.Infrastructure/Agents/",
        "Bonds.Api/Endpoints/McpEndpoints.cs",
    ];

    /// <summary>Заявки/исполнитель, решения по планам, режим/пауза/лимиты, запись секретов и настроек, правка корзин.</summary>
    private static readonly Regex Forbidden = new(
        @"OrderGateway|PlanExecution|ExecutionVenue|IExecutionGateResolver|PostLimitOrder|CancelOrder|PostOrder|putTradeOrder|delTradeOrder" +
        @"|ApproveAsync|RejectAsync|TransitionAsync|SetExecutionStatusAsync|RequestCancelAsync|RotateNonceAsync|ReleaseCashHoldsAsync|ExtendExpiryAsync" +
        @"|SetExecutionPausedAsync|SetExecutionModeAsync|ExecutionLimits|IUserSettingsRepository|ICapitalBasketRepository" +
        @"|\.SaveAsync\(|\.DeleteAsync\(|UpdateMetaAsync|MarkInvalidAsync|UpsertAsync|IUserCredentialRepository|PolicyRunner",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Bonds.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Bonds.sln не найден");
    }

    private static IEnumerable<(string Rel, int LineNo, string Line)> AgentSourceLines()
    {
        var src = Path.Combine(RepoRoot(), "src");
        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, file).Replace('\\', '/');
            if (rel.Contains("/obj/", StringComparison.Ordinal) || rel.Contains("/bin/", StringComparison.Ordinal)) continue;
            if (!AgentPaths.Any(p => rel.StartsWith(p, StringComparison.Ordinal))) continue;
            var n = 0;
            foreach (var line in File.ReadLines(file)) yield return (rel, ++n, line);
        }
    }

    [Fact]
    public void AgentCode_Exists()
    {
        AgentSourceLines().Select(x => x.Rel).Distinct().Should().Contain(
            ["Bonds.Infrastructure/Agents/Mcp/BondsMcpTools.cs", "Bonds.Infrastructure/Agents/ClaudeCode/ClaudeCodeAgentRunner.cs", "Bonds.Api/Endpoints/McpEndpoints.cs"]);
    }

    [Fact]
    public void AgentCode_HasNoTradingSettingsOrCredentialWrites()
    {
        var offenders = AgentSourceLines()
            .Where(x => Forbidden.IsMatch(x.Line))
            .Select(x => $"{x.Rel}:{x.LineNo}: {Forbidden.Match(x.Line).Value}")
            .ToList();

        offenders.Should().BeEmpty("агент не торгует, не подтверждает планы и не меняет лимиты, настройки и ключи (задача 68)");
    }

    [Fact]
    public void ProposePlan_UsesOnlyManualPlanCreation()
    {
        var tools = AgentSourceLines().Where(x => x.Rel == "Bonds.Infrastructure/Agents/Mcp/BondsMcpTools.cs").Select(x => x.Line).ToList();
        tools.Should().Contain(l => l.Contains("CreateManualAsync(", StringComparison.Ordinal));
        tools.Should().Contain(l => l.Contains("PlanSources.Agent", StringComparison.Ordinal));
    }

    [Fact]
    public void Runner_NeverUsesBareMode_AndClearsEnvironment()
    {
        var cli = AgentSourceLines().Where(x => x.Rel == "Bonds.Infrastructure/Agents/ClaudeCode/ClaudeCodeCli.cs").Select(x => x.Line).ToList();
        cli.Should().NotContain(l => l.Contains("\"--bare\"", StringComparison.Ordinal));
        cli.Should().Contain(l => l.Contains("psi.Environment.Clear()", StringComparison.Ordinal));
    }
}

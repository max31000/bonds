using System.Text.Json;
using Bonds.Core.Agents;
using Bonds.Infrastructure.Agents;
using Bonds.Infrastructure.Agents.ClaudeCode;
using Bonds.Infrastructure.Agents.Mcp;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Bonds.Tests.Agents;

/// <summary>Ревью задачи 68: изоляция процесса (setpriv, права каталогов), бронь propose_plan, виды задач, справедливость очереди.</summary>
public class AgentIsolationTests
{
    private static AgentTask NewTask() =>
        new(AgentTaskKind.OnDemand, 1, "q", "sonnet", 5, TimeSpan.FromSeconds(30), "http://127.0.0.1:5001/internal/mcp/1", "bearer-0000000000000000");

    // ─── setpriv ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void StartInfo_WithSetpriv_DropsPrivilegesBeforeCli()
    {
        var setpriv = new ClaudeCodeCli.SetprivLaunch("/usr/bin/setpriv", 10001, 10001);
        var psi = ClaudeCodeCli.CreateStartInfo("/usr/local/bin/claude", NewTask(), "tok-000000000", "/var/lib/bonds-agent/run-x", "/var/lib/bonds-agent/run-x/mcp.json", "/usr/bin", setpriv);

        psi.FileName.Should().Be("/usr/bin/setpriv");
        psi.ArgumentList.Take(9).Should().Equal(
            "--reuid=10001", "--regid=10001", "--clear-groups", "--no-new-privs", "--inh-caps=-all", "--bounding-set=-all", "--", "/usr/local/bin/claude", "-p");
        psi.ArgumentList.Should().NotContain(a => a.Contains("tok-000000000"));
        psi.Environment.Keys.Should().NotContain("ANTHROPIC_API_KEY");
    }

    [Fact]
    public void StartInfo_WithoutSetpriv_RunsCliDirectly()
    {
        var psi = ClaudeCodeCli.CreateStartInfo("claude", NewTask(), "tok", "/tmp/x", "/tmp/x/mcp.json", null);
        psi.FileName.Should().Be("claude");
        psi.ArgumentList[0].Should().Be("-p");
    }

    [Fact]
    public void Options_SetprivDefaultsToLinuxOnly_AndIsOverridable()
    {
        new AgentOptions().ShouldUseSetpriv.Should().Be(OperatingSystem.IsLinux());
        new AgentOptions { UseSetpriv = false }.ShouldUseSetpriv.Should().BeFalse();
        new AgentOptions().RunAsUid.Should().Be(10001);
        new AgentOptions { RunRoot = "/x" }.ResolvedRunRoot.Should().Be("/x");
    }

    // ─── права каталогов ──────────────────────────────────────────────────────────────────

    private static string TempRoot() => Path.Combine(Path.GetTempPath(), "bonds-agent-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void EnsureOwnerOnlyDirectory_Sets0700()
    {
        var dir = TempRoot();
        try
        {
            Directory.CreateDirectory(dir);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(dir, (UnixFileMode)0b111_101_101); // 0755, как создаёт FileSystemXmlRepository по умолчанию

            var mode = AgentFileSystem.EnsureOwnerOnlyDirectory(dir);

            if (OperatingSystem.IsWindows())
            {
                mode.Should().BeNull();
                return;
            }

            mode.Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.GetUnixFileMode(dir).Should().Be(AgentFileSystem.OwnerOnlyDirMode);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task RunRoot0711_RunDir0700_McpFile0600_AndStaleCleanup()
    {
        var root = TempRoot();
        try
        {
            AgentFileSystem.EnsureRunRoot(root);
            var run = AgentFileSystem.CreateRunDirectory(root, null, null);
            await AgentFileSystem.WritePrivateFileAsync(Path.Combine(run, "mcp.json"), "{}", null, null, CancellationToken.None);
            var keep = Directory.CreateDirectory(Path.Combine(root, "not-a-run")).FullName;

            Path.GetFileName(run).Should().StartWith(AgentFileSystem.RunDirPrefix);
            if (!OperatingSystem.IsWindows())
            {
                File.GetUnixFileMode(root).Should().Be(AgentFileSystem.RunRootMode);
                File.GetUnixFileMode(run).Should().Be(AgentFileSystem.OwnerOnlyDirMode);
                File.GetUnixFileMode(Path.Combine(run, "mcp.json")).Should().Be(AgentFileSystem.OwnerOnlyFileMode);
            }

            var removed = AgentStartupHostedService.Prepare(new AgentOptions { RunRoot = root }, NullLogger.Instance);
            removed.Should().Be(1);
            Directory.Exists(run).Should().BeFalse("каталоги прошлых запусков удаляются при старте");
            Directory.Exists(keep).Should().BeTrue("чужие каталоги не трогаем");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Startup_WarnsWhenWebResearchEnabled()
    {
        var root = TempRoot();
        try
        {
            var logger = new CapturingLogger();
            AgentStartupHostedService.Prepare(new AgentOptions { RunRoot = root, AllowWebResearch = true }, logger);
            logger.Warnings.Should().ContainSingle(w => w.Contains("AllowWebResearch") && w.Contains("NOT recommended"));

            var quiet = new CapturingLogger();
            AgentStartupHostedService.Prepare(new AgentOptions { RunRoot = root }, quiet);
            quiet.Warnings.Should().BeEmpty();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Runner_CreatesRunDirUnderRunRoot_AndRemovesIt()
    {
        var root = TempRoot();
        try
        {
            var exec = new DirProbeExecutor();
            var options = new OptionsMonitorStub<AgentOptions>(new AgentOptions { UseSetpriv = false, RunRoot = root });
            var runner = new ClaudeCodeAgentRunner(exec, options, NullLogger<ClaudeCodeAgentRunner>.Instance);

            var result = await runner.RunAsync(NewTask(), new AgentCredentials("sk-ant-oat01-0000000000000000"));

            result.Succeeded.Should().BeTrue();
            Path.GetDirectoryName(exec.Home).Should().Be(root);
            if (!OperatingSystem.IsWindows())
            {
                exec.HomeMode.Should().Be(AgentFileSystem.OwnerOnlyDirMode);
                exec.McpMode.Should().Be(AgentFileSystem.OwnerOnlyFileMode);
            }

            Directory.Exists(exec.Home).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private sealed class DirProbeExecutor : IAgentProcessExecutor
    {
        public string? Home;
        public UnixFileMode? HomeMode;
        public UnixFileMode? McpMode;

        public Task<AgentProcessOutcome> RunAsync(System.Diagnostics.ProcessStartInfo psi, string stdin, TimeSpan timeout, int maxOutputBytes, CancellationToken ct)
        {
            Home = psi.WorkingDirectory;
            if (!OperatingSystem.IsWindows())
            {
                HomeMode = File.GetUnixFileMode(Home);
                McpMode = File.GetUnixFileMode(Path.Combine(Home, "mcp.json"));
            }

            return Task.FromResult(new AgentProcessOutcome(0, """{"type":"result","is_error":false,"result":"ok"}""", string.Empty, false));
        }
    }

    // ─── propose_plan: бронь и виды задач ─────────────────────────────────────────────────

    [Fact]
    public async Task ReserveProposal_IsAtomic_UnderConcurrency_AndReleaseReturnsSlot()
    {
        var tokens = new AgentMcpTokens(TimeProvider.System);
        var bearer = tokens.Issue(1, 42, TimeSpan.FromMinutes(5), AgentTaskKind.OnDemand);

        var results = await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(() => tokens.TryReserveProposal(42, 1))));

        results.Count(r => r).Should().Be(1, "параллельные вызовы одного запуска не превышают лимит");
        tokens.ReservedProposals(42).Should().Be(1);

        tokens.ReleaseProposal(42);
        tokens.ReservedProposals(42).Should().Be(0);
        tokens.TryReserveProposal(42, 1).Should().BeTrue("возвращённый слот можно занять снова");

        tokens.Revoke(bearer);
        tokens.KindOf(42).Should().BeNull("после отзыва запуск забыт");
        tokens.ReservedProposals(42).Should().Be(0);
    }

    [Theory]
    [InlineData(AgentTaskKind.DailyDigest, false)]
    [InlineData(AgentTaskKind.MonthlyReview, false)]
    [InlineData(AgentTaskKind.EventReview, true)]
    [InlineData(AgentTaskKind.OnDemand, true)]
    public void KindStoredInGrant_DecidesProposeAccess(AgentTaskKind kind, bool mayPropose)
    {
        var tokens = new AgentMcpTokens(TimeProvider.System);
        tokens.Issue(1, 7, TimeSpan.FromMinutes(5), kind);
        tokens.KindOf(7).Should().Be(kind);
        AgentMcpTokens.KindMayPropose(tokens.KindOf(7)).Should().Be(mayPropose);
        AgentMcpTokens.KindMayPropose(tokens.KindOf(999)).Should().BeFalse("неизвестный запуск — нельзя");
    }

    private static (BondsMcpServer Server, AgentMcpTokens Tokens) ServerWithTokens()
    {
        var tokens = new AgentMcpTokens(TimeProvider.System);
        var sp = new ServiceCollection().AddSingleton(tokens).BuildServiceProvider();
        var tools = new BondsMcpTools(sp, new OptionsMonitorStub<AgentOptions>(new AgentOptions()), TimeProvider.System, NullLogger<BondsMcpTools>.Instance);
        return (new BondsMcpServer(tools, NullLogger<BondsMcpServer>.Instance), tokens);
    }

    private static JsonElement Msg(string json) => JsonDocument.Parse(json).RootElement;

    [Theory]
    [InlineData(AgentTaskKind.DailyDigest)]
    [InlineData(AgentTaskKind.MonthlyReview)]
    public async Task ProposePlan_FromReadOnlyKinds_IsToolError(AgentTaskKind kind)
    {
        var (server, tokens) = ServerWithTokens();
        tokens.Issue(1, 50, TimeSpan.FromMinutes(5), kind);

        var response = await server.HandleAsync(
            Msg("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"propose_plan","arguments":{"kind":"inflow","amount":1000,"rationale":"r"}}}"""), 1, default, 50);

        response!["result"]!["isError"]!.GetValue<bool>().Should().BeTrue();
        response["result"]!["content"]![0]!["text"]!.GetValue<string>().Should().Contain("недоступен");
        tokens.ReservedProposals(50).Should().Be(0);
    }

    [Fact]
    public async Task ProposePlan_InvalidArguments_ReleasesReservedSlot()
    {
        var (server, tokens) = ServerWithTokens();
        tokens.Issue(1, 51, TimeSpan.FromMinutes(5), AgentTaskKind.OnDemand);

        var response = await server.HandleAsync(
            Msg("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"propose_plan","arguments":{"kind":"bogus","rationale":"r"}}}"""), 1, default, 51);

        response!["result"]!["isError"]!.GetValue<bool>().Should().BeTrue();
        tokens.ReservedProposals(51).Should().Be(0, "ошибка валидации не тратит слот запуска");
    }

    // ─── очередь ──────────────────────────────────────────────────────────────────────────

    private static AgentWorkQueue Queue() => new(TimeProvider.System, new OptionsMonitorStub<AgentOptions>(new AgentOptions()));

    [Fact]
    public void Queue_AtMostOneQueuedOnDemandPerUser()
    {
        var queue = Queue();
        queue.TryEnqueue(new AgentWorkItem(1, AgentTaskKind.OnDemand, "a")).Should().BeTrue();
        queue.TryEnqueue(new AgentWorkItem(1, AgentTaskKind.OnDemand, "b")).Should().BeFalse("второй вопрос, пока первый ждёт, — отклоняется");
        queue.TryEnqueue(new AgentWorkItem(2, AgentTaskKind.OnDemand, "c")).Should().BeTrue("у другого пользователя своя очередь");

        queue.Reader.TryRead(out var first).Should().BeTrue();
        queue.Completed(queue.Begin(first!));
        queue.TryEnqueue(new AgentWorkItem(1, AgentTaskKind.OnDemand, "d")).Should().BeTrue();
    }

    [Fact]
    public void Queue_EventReview_MergesEventsWhileQueued()
    {
        var queue = Queue();
        queue.CriticalSignalsDetected(1, ["оферта A"]);
        queue.CriticalSignalsDetected(1, ["дефолт B", "оферта A"]);

        queue.Reader.TryRead(out var item).Should().BeTrue();
        queue.Reader.TryRead(out _).Should().BeFalse("одно задание разбора на пользователя");
        var work = queue.Begin(item!);
        work.Question.Should().Be("• оферта A\n• дефолт B");

        // Событие во время запуска — новый разбор (уже с ним одним).
        queue.CriticalSignalsDetected(1, ["просадка C"]);
        queue.Completed(work);
        queue.Reader.TryRead(out var next).Should().BeTrue();
        queue.Begin(next!).Question.Should().Be("• просадка C");
    }

    [Fact]
    public void Queue_Full_RejectsAndClearsPendingMark()
    {
        var queue = Queue();
        var accepted = 0;
        for (ulong u = 1; u <= 100; u++)
        {
            if (queue.TryEnqueue(new AgentWorkItem(u, AgentTaskKind.OnDemand, "x"))) accepted++;
        }

        accepted.Should().Be(32, "ёмкость канала — 32; переполнение отклоняется, а не выбрасывается молча");
        queue.IsPending(100, AgentTaskKind.OnDemand).Should().BeFalse("отклонённое задание не блокирует пользователя");
    }

    [Fact]
    public void Queue_ReleaseAsk_RestoresCooldown()
    {
        var queue = Queue();
        queue.TryMarkAsk(1, out var mark).Should().BeTrue();
        queue.TryMarkAsk(1, out _).Should().BeFalse();
        queue.ReleaseAsk(1, mark);
        queue.TryMarkAsk(1, out _).Should().BeTrue("снятая отметка не держит паузу");
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Warnings { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
    }
}

using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bonds.Core.Policy;
using Bonds.Infrastructure.Connectors.Freedom;
using Bonds.Infrastructure.Connectors.TInvest;
using Bonds.Infrastructure.Execution;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Policy;

/// <summary>
/// Жёсткое правило отправки заявок (задачи 63–65, переписано задачей 65). Сканируются ВСЕ исходники <c>src</c> (код, json, csproj; даже комментарии):
/// <list type="bullet">
/// <item>Вызовы торговых сервисов SDK T-Invest (сервис заявок, свойство <c>.Orders</c> клиента, отправка/замена/снятие, sandbox-заявки) — ТОЛЬКО в
/// <c>Bonds.Infrastructure/Execution/TInvestOrderGateway.cs</c> (реализация <see cref="ITInvestOrderGateway"/>).</item>
/// <item>Глаголы заявок (методы шлюза) — ТОЛЬКО внутри <c>Bonds.Infrastructure/Execution/</c> (шлюз и исполнитель); бот, эндпоинты и правила снимают
/// заявки только флагом.</item>
/// <item>Команды заявок Freedom (Tradernet: размещение, снятие, список заявок) — ТОЛЬКО в <c>Bonds.Infrastructure/Execution/FreedomOrderGateway.cs</c> (реализация
/// <see cref="IFreedomOrderGateway"/>, задача 66); стоп-заявки Freedom — НИГДЕ. Подписанный транспорт Freedom — только у клиента на чтение и этого шлюза.</item>
/// </list>
/// Менять этот тест — осознанно, а не случайно.
/// </summary>
public class NoOrderPlacementArchitectureTests
{
    private const string GatewayFile = "Bonds.Infrastructure/Execution/TInvestOrderGateway.cs";
    private const string ExecutionDir = "Bonds.Infrastructure/Execution/";
    private const string FreedomGatewayFile = "Bonds.Infrastructure/Execution/FreedomOrderGateway.cs";

    /// <summary>Торговые сервисы/методы SDK T-Invest.</summary>
    private static readonly Regex SdkOrders = new(
        @"OrdersService|OrdersStreamService|StopOrdersService|\.Orders\b|\.StopOrders\b|PostOrder|PostStopOrder|PostSandboxOrder|ReplaceOrder|ReplaceSandboxOrder|CancelSandboxOrder|CancelStopOrder|SetStopOrder|GetMaxLots",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Глаголы заявок (методы шлюза и SDK).</summary>
    private static readonly Regex OrderVerbs = new(@"PostLimitOrder|CancelOrder|PostOrder", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Команды заявок Freedom (Tradernet) — только в <see cref="FreedomGatewayFile"/> (задача 66).</summary>
    private static readonly Regex FreedomOrders = new(
        @"putTradeOrder|delTradeOrder|getNotifyOrderJson|cancelOrder\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Стоп-заявки и прочие торговые команды Freedom — запрещены везде (исполнитель — только лимитные заявки).</summary>
    private static readonly Regex FreedomForbidden = new(
        @"putStopLoss|putStopOrder|putTradeOrderStop|putMarketOrder|editTradeOrder|stopLoss",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Bonds.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Bonds.sln не найден выше каталога тестов");
    }

    private static IEnumerable<(string Rel, int LineNo, string Line)> SourceLines()
    {
        var src = Path.Combine(RepoRoot(), "src");
        Directory.Exists(src).Should().BeTrue();
        foreach (var file in Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories)
                     .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".json", StringComparison.Ordinal) || f.EndsWith(".csproj", StringComparison.Ordinal))
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
        {
            var rel = Path.GetRelativePath(src, file).Replace('\\', '/');
            var lineNo = 0;
            foreach (var line in File.ReadLines(file))
            {
                lineNo++;
                yield return (rel, lineNo, line);
            }
        }
    }

    [Fact]
    public void SdkOrderCalls_OnlyInTInvestOrderGateway()
    {
        var offenders = SourceLines()
            .Where(x => x.Rel != GatewayFile && SdkOrders.IsMatch(x.Line))
            .Select(x => $"{x.Rel}:{x.LineNo}: {SdkOrders.Match(x.Line).Value}")
            .ToList();

        offenders.Should().BeEmpty("торговые сервисы T-Invest вызываются только из ITInvestOrderGateway (Execution/TInvestOrderGateway.cs)");
    }

    [Fact]
    public void OrderVerbs_OnlyInsideExecutionFolder()
    {
        var offenders = SourceLines()
            .Where(x => !x.Rel.StartsWith(ExecutionDir, StringComparison.Ordinal) && OrderVerbs.IsMatch(x.Line))
            .Select(x => $"{x.Rel}:{x.LineNo}: {OrderVerbs.Match(x.Line).Value}")
            .ToList();

        offenders.Should().BeEmpty("отправка/снятие заявок — только шлюз и исполнитель в Bonds.Infrastructure/Execution/");
    }

    [Fact]
    public void FreedomOrderCommands_OnlyInFreedomOrderGateway()
    {
        var offenders = SourceLines()
            .Where(x => x.Rel != FreedomGatewayFile && FreedomOrders.IsMatch(x.Line))
            .Select(x => $"{x.Rel}:{x.LineNo}: {FreedomOrders.Match(x.Line).Value}")
            .ToList();

        offenders.Should().BeEmpty("команды заявок Freedom вызываются только из IFreedomOrderGateway (Execution/FreedomOrderGateway.cs)");
        SourceLines().Where(x => x.Rel == FreedomGatewayFile && x.Line.Contains("putTradeOrder", StringComparison.Ordinal)).Should().NotBeEmpty();
    }

    [Fact]
    public void FreedomStopOrders_ForbiddenEverywhere()
    {
        var offenders = SourceLines()
            .Where(x => FreedomForbidden.IsMatch(x.Line))
            .Select(x => $"{x.Rel}:{x.LineNo}: {FreedomForbidden.Match(x.Line).Value}")
            .ToList();

        offenders.Should().BeEmpty("стоп-/рыночные заявки Freedom исполнителю запрещены");
    }

    [Fact]
    public void FreedomSignedTransport_OnlyInReadClientAndGateway()
    {
        var allowed = new[] { "Bonds.Infrastructure/Connectors/Freedom/FreedomSignedHttp.cs", "Bonds.Infrastructure/Connectors/Freedom/FreedomApiClient.cs", FreedomGatewayFile };
        var offenders = SourceLines()
            .Where(x => !allowed.Contains(x.Rel) && x.Line.Contains("FreedomSignedHttp", StringComparison.Ordinal))
            .Select(x => $"{x.Rel}:{x.LineNo}")
            .ToList();

        offenders.Should().BeEmpty("подписанный транспорт Freedom — только клиент на чтение и торговый шлюз");
    }

    [Fact]
    public void FreedomGateway_HasSingleImplementation_InExecutionFolder()
    {
        var implementations = typeof(IFreedomOrderGateway).Assembly.GetTypes()
            .Where(t => !t.IsInterface && !t.IsAbstract && typeof(IFreedomOrderGateway).IsAssignableFrom(t))
            .Select(t => t.FullName)
            .ToList();
        implementations.Should().BeEquivalentTo(["Bonds.Infrastructure.Execution.FreedomOrderGateway"]);
        File.Exists(Path.Combine(RepoRoot(), "src", FreedomGatewayFile)).Should().BeTrue();
    }

    [Fact]
    public void Gateway_HasSingleImplementation_InExecutionFolder()
    {
        var implementations = typeof(ITInvestOrderGateway).Assembly.GetTypes()
            .Where(t => !t.IsInterface && !t.IsAbstract && typeof(ITInvestOrderGateway).IsAssignableFrom(t))
            .Select(t => t.FullName)
            .ToList();
        implementations.Should().BeEquivalentTo(["Bonds.Infrastructure.Execution.TInvestOrderGateway"]);
        File.Exists(Path.Combine(RepoRoot(), "src", GatewayFile)).Should().BeTrue();
    }

    [Fact]
    public void Assemblies_ExposeOrderPlacementMembers_OnlyInExecutionNamespace()
    {
        var assemblies = new[] { typeof(TradePlan).Assembly, typeof(IFreedomApiClient).Assembly };
        var pattern = new Regex(@"^(Place|Post|Submit|Send|Put|Create|Replace|Cancel|Del|Delete)(Trade|Stop|Limit|Market)?Orders?(Async)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        var offenders = assemblies
            .SelectMany(a => a.GetTypes())
            .Where(t => t.Namespace != "Bonds.Infrastructure.Execution")
            .SelectMany(t => t.GetMembers(flags).Select(m => (Type: t, Member: m)))
            .Where(x => pattern.IsMatch(x.Member.Name))
            .Select(x => $"{x.Type.FullName}.{x.Member.Name}")
            .ToList();

        offenders.Should().BeEmpty("члены размещения заявок допустимы только в Bonds.Infrastructure.Execution");
    }

    [Fact]
    public void BrokerClientInterfaces_AreReadOnly()
    {
        var verbs = new Regex(@"Order", RegexOptions.CultureInvariant);
        foreach (var type in new[] { typeof(IFreedomApiClient), typeof(ITInvestPortfolioClient) })
        {
            type.GetMethods().Where(m => verbs.IsMatch(m.Name)).Select(m => m.Name).Should().BeEmpty($"{type.Name} — только чтение (стаканы читает GetQuotesAsync)");
        }
    }

    [Fact]
    public void PlanModel_IsDataOnly_NoBehaviourToExecute()
    {
        // План — запись: у моделей планов нет методов исполнения.
        foreach (var type in new[] { typeof(TradePlan), typeof(TradePlanLine), typeof(TradePlanSummary), typeof(TradePlanDraft) })
        {
            type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => !m.IsSpecialName && m.Name is not ("Equals" or "GetHashCode" or "ToString" or "Deconstruct" or "PrintMembers" or "<Clone>$"))
                .Select(m => m.Name)
                .Should().BeEmpty($"{type.Name} — только данные");
        }
    }

    [Fact]
    public void AppSettings_NeverEnableExecution()
    {
        // Боевое включение — только env Execution__Enabled на сервере руками владельца; ни один appsettings*.json его не включает.
        var api = Path.Combine(RepoRoot(), "src", "Bonds.Api");
        var files = Directory.EnumerateFiles(api, "appsettings*.json").ToList();
        files.Should().NotBeEmpty();
        foreach (var file in files)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (doc.RootElement.TryGetProperty("Execution", out var execution) && execution.TryGetProperty("Enabled", out var enabled))
                enabled.ValueKind.Should().Be(JsonValueKind.False, $"{Path.GetFileName(file)}: Execution:Enabled должен быть false");
        }
    }
}

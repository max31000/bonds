using System.Reflection;
using System.Text.RegularExpressions;
using Bonds.Core.Policy;
using Bonds.Infrastructure.Connectors.Freedom;
using Bonds.Infrastructure.Connectors.TInvest;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Policy;

/// <summary>
/// Жёсткое правило задач 63–64: в коде НЕТ пути отправки заявок брокеру — ни вызовов <c>OrdersService</c>/<c>PostOrder</c>/<c>putTradeOrder</c>, ни интерфейсов под них.
/// Планы — только данные. Появление таких идентификаторов в <c>src</c> (в коде или комментариях) краснит тест: исполнитель — фаза 3, отдельная задача, и менять
/// этот тест осознанно, а не случайно.
/// </summary>
public class NoOrderPlacementArchitectureTests
{
    private static readonly Regex Forbidden = new(
        @"OrdersService|OrdersStreamService|StopOrdersService|PostOrder|PostStopOrder|putTradeOrder|delTradeOrder|ReplaceOrder|CancelOrder|SetStopOrder",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Bonds.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Bonds.sln не найден выше каталога тестов");
    }

    [Fact]
    public void Src_ContainsNoOrderPlacementReferences()
    {
        var src = Path.Combine(RepoRoot(), "src");
        Directory.Exists(src).Should().BeTrue();

        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories)
                     .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".json", StringComparison.Ordinal) || f.EndsWith(".csproj", StringComparison.Ordinal))
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
        {
            var lineNo = 0;
            foreach (var line in File.ReadLines(file))
            {
                lineNo++;
                var match = Forbidden.Match(line);
                if (match.Success) offenders.Add($"{Path.GetRelativePath(src, file)}:{lineNo}: {match.Value}");
            }
        }

        offenders.Should().BeEmpty("заявки брокерам не отправляются: исполнитель — фаза 3");
    }

    [Fact]
    public void Assemblies_ExposeNoOrderPlacementMembers()
    {
        var assemblies = new[] { typeof(TradePlan).Assembly, typeof(IFreedomApiClient).Assembly };
        var pattern = new Regex(@"^(Place|Post|Submit|Send|Put|Create|Replace|Cancel|Del|Delete)(Trade|Stop|Limit|Market)?Orders?(Async)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        var offenders = assemblies
            .SelectMany(a => a.GetTypes())
            .SelectMany(t => t.GetMembers(flags).Select(m => (Type: t, Member: m)))
            .Where(x => pattern.IsMatch(x.Member.Name))
            .Select(x => $"{x.Type.FullName}.{x.Member.Name}")
            .ToList();

        offenders.Should().BeEmpty("в сборках нет членов размещения заявок");
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
}

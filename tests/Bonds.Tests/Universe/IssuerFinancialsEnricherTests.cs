using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Bonds.Infrastructure.Connectors.GirBo;
using Bonds.Infrastructure.Universe;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Bonds.Tests.Universe;

/// <summary>
/// Задача 45: суточный шаг наполнения отчётности эмитентов из ГИР БО (<see cref="IssuerFinancialsEnricher"/>) —
/// статусы ok/not_found/hidden/cb/error и паттерн «источник недоступен → шаг не засчитан». Без сети и БД.
/// </summary>
public class IssuerFinancialsEnricherTests
{
    private sealed class RepoFake : IIssuerFinancialsRepository
    {
        public List<string> Queue { get; set; } = [];
        public List<IssuerFinancials> Upserted { get; } = [];
        public Dictionary<string, string> Statuses { get; } = [];
        public (int Limit, DateTime StaleBefore, DateTime ErrorRetryBefore)? RequestedQueue { get; private set; }

        public Task UpsertAsync(IReadOnlyList<IssuerFinancials> items, CancellationToken ct = default)
        {
            Upserted.AddRange(items);
            return Task.CompletedTask;
        }

        public Task UpsertStatusAsync(IssuerFinancialsStatus status, CancellationToken ct = default)
        {
            Statuses[status.Inn] = status.Status;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> GetRefreshQueueAsync(
            int limit, DateTime staleBeforeUtc, DateTime errorRetryBeforeUtc, CancellationToken ct = default)
        {
            RequestedQueue = (limit, staleBeforeUtc, errorRetryBeforeUtc);
            return Task.FromResult<IReadOnlyList<string>>(Queue);
        }

        public Task<IReadOnlyDictionary<string, IReadOnlyList<IssuerFinancials>>> GetLatestByInnsAsync(
            IReadOnlyCollection<string> inns, int yearsPerInn = 3, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyDictionary<string, IssuerFinancialsStatus>> GetStatusesAsync(
            IReadOnlyCollection<string> inns, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private static readonly DateTime Now = DateTime.UtcNow;

    private static IssuerFinancials Row(string inn, int period) => new()
    {
        Inn = inn, Period = period, Source = IssuerFinancialsSources.GirBo, FetchedAt = Now, Revenue = 1_000m,
    };

    private static IssuerFinancialsEnricher Create(Mock<IGirBoClient> girBo, RepoFake repo) =>
        new(girBo.Object, repo, NullLogger<IssuerFinancialsEnricher>.Instance);

    private static Task<IssuerFinancialsRunResult> Run(IssuerFinancialsEnricher e) =>
        e.EnrichBatchAsync(20, TimeSpan.FromDays(30), TimeSpan.FromHours(20), TimeSpan.Zero, Now);

    private static Mock<IGirBoClient> HealthyClient(string inn, long orgId = 10, params GirBoFiling[] filings)
    {
        var girBo = new Mock<IGirBoClient>();
        girBo.Setup(g => g.FindOrganizationAsync(inn, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GirBoOrganization(orgId, inn, "ООО Тест", false));
        girBo.Setup(g => g.GetFilingsAsync(orgId, It.IsAny<CancellationToken>())).ReturnsAsync(filings);
        return girBo;
    }

    [Fact]
    public async Task Ok_UsesLatestFilingByPeriod_WritesRowsAndOkStatus()
    {
        const string inn = "7814148471";
        var girBo = HealthyClient(inn, 10, new GirBoFiling(1, 2022, false), new GirBoFiling(3, 2024, false), new GirBoFiling(2, 2023, false));
        girBo.Setup(g => g.GetAnnualFinancialsAsync(3, inn, 2024, Now, It.IsAny<CancellationToken>()))
            .ReturnsAsync([Row(inn, 2024), Row(inn, 2023), Row(inn, 2022)]);
        var repo = new RepoFake { Queue = [inn] };

        var result = await Run(Create(girBo, repo));

        result.Should().Be(new IssuerFinancialsRunResult(1, 1, 0, 0, true));
        repo.Upserted.Select(r => r.Period).Should().Equal(2024, 2023, 2022);
        repo.Statuses[inn].Should().Be(IssuerFinancialsStatuses.Ok);
    }

    [Fact]
    public async Task NotFound_WhenOrganizationMissing_NoFurtherRequests()
    {
        var girBo = new Mock<IGirBoClient>(MockBehavior.Strict);
        girBo.Setup(g => g.FindOrganizationAsync("7700000001", It.IsAny<CancellationToken>())).ReturnsAsync((GirBoOrganization?)null);
        var repo = new RepoFake { Queue = ["7700000001"] };

        var result = await Run(Create(girBo, repo));

        result.Completed.Should().BeTrue();
        repo.Statuses["7700000001"].Should().Be(IssuerFinancialsStatuses.NotFound);
        repo.Upserted.Should().BeEmpty();
    }

    [Fact]
    public async Task Cb_WhenSearchMarksCreditOrganization_NoFilingsRequested()
    {
        var girBo = new Mock<IGirBoClient>(MockBehavior.Strict);
        girBo.Setup(g => g.FindOrganizationAsync("7707083893", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GirBoOrganization(5, "7707083893", "Банк", true));
        var repo = new RepoFake { Queue = ["7707083893"] };

        await Run(Create(girBo, repo));

        repo.Statuses["7707083893"].Should().Be(IssuerFinancialsStatuses.Cb);
    }

    [Fact]
    public async Task Cb_WhenLatestFilingIsCb_NoDetailsRequested()
    {
        var girBo = HealthyClient("7700000002", 7, new GirBoFiling(1, 2023, false), new GirBoFiling(2, 2024, true));
        var repo = new RepoFake { Queue = ["7700000002"] };

        await Run(Create(girBo, repo));

        repo.Statuses["7700000002"].Should().Be(IssuerFinancialsStatuses.Cb);
        girBo.Verify(g => g.GetAnnualFinancialsAsync(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Hidden_WhenNoFilings()
    {
        var girBo = HealthyClient("7700000003", 8);
        var repo = new RepoFake { Queue = ["7700000003"] };

        await Run(Create(girBo, repo));

        repo.Statuses["7700000003"].Should().Be(IssuerFinancialsStatuses.Hidden);
    }

    [Fact]
    public async Task Hidden_WhenDetailsHaveNoAnnualData()
    {
        const string inn = "7700000004";
        var girBo = HealthyClient(inn, 9, new GirBoFiling(4, 2024, false));
        girBo.Setup(g => g.GetAnnualFinancialsAsync(4, inn, 2024, Now, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var repo = new RepoFake { Queue = [inn] };

        var result = await Run(Create(girBo, repo));

        repo.Statuses[inn].Should().Be(IssuerFinancialsStatuses.Hidden);
        result.NoData.Should().Be(1);
        repo.Upserted.Should().BeEmpty();
    }

    [Fact]
    public async Task IsolatedFailure_WritesErrorStatus_AndContinuesWithNext()
    {
        const string bad = "7700000005";
        const string good = "7700000006";
        var girBo = new Mock<IGirBoClient>();
        girBo.Setup(g => g.FindOrganizationAsync(bad, It.IsAny<CancellationToken>())).ThrowsAsync(new GirBoUnavailableException("500"));
        girBo.Setup(g => g.FindOrganizationAsync(good, It.IsAny<CancellationToken>())).ReturnsAsync((GirBoOrganization?)null);
        var repo = new RepoFake { Queue = [bad, good] };

        var result = await Run(Create(girBo, repo));

        result.Should().Be(new IssuerFinancialsRunResult(2, 0, 1, 1, true));
        repo.Statuses[bad].Should().Be(IssuerFinancialsStatuses.Error);
        repo.Statuses[good].Should().Be(IssuerFinancialsStatuses.NotFound);
    }

    [Fact]
    public async Task TrailingIsolatedFailure_AtEndOfQueue_StillWritesErrorStatus()
    {
        const string bad = "7700000007";
        var girBo = new Mock<IGirBoClient>();
        girBo.Setup(g => g.FindOrganizationAsync(bad, It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("boom"));
        var repo = new RepoFake { Queue = [bad] };

        var result = await Run(Create(girBo, repo));

        result.Completed.Should().BeTrue();
        repo.Statuses[bad].Should().Be(IssuerFinancialsStatuses.Error);
    }

    [Fact]
    public async Task ConsecutiveFailures_AbortTick_NotCompleted_NoErrorStatusesWritten()
    {
        var inns = Enumerable.Range(0, 8).Select(i => $"77000001{i:D2}").ToList();
        var girBo = new Mock<IGirBoClient>();
        girBo.Setup(g => g.FindOrganizationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GirBoUnavailableException("down"));
        var repo = new RepoFake { Queue = inns };

        var result = await Run(Create(girBo, repo));

        result.Completed.Should().BeFalse("ГИР БО недоступен — шаг не засчитан");
        repo.Statuses.Should().BeEmpty("упавший сервис не должен метить эмитентов статусом error впрок");
        girBo.Verify(g => g.FindOrganizationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Exactly(IssuerFinancialsEnricher.MaxConsecutiveFailures), "после серии сбоев остальные запросы не отправляются");
    }

    [Fact]
    public async Task EmptyQueue_IsCompleted_NoRequests()
    {
        var girBo = new Mock<IGirBoClient>(MockBehavior.Strict);
        var repo = new RepoFake();

        var result = await Run(Create(girBo, repo));

        result.Should().Be(new IssuerFinancialsRunResult(0, 0, 0, 0, true));
    }

    [Fact]
    public async Task PassesBatchSizeAndThresholdsToQueue()
    {
        var repo = new RepoFake();

        await Create(new Mock<IGirBoClient>(), repo)
            .EnrichBatchAsync(20, TimeSpan.FromDays(30), TimeSpan.FromHours(20), TimeSpan.Zero, Now);

        repo.RequestedQueue.Should().Be((20, Now.AddDays(-30), Now.AddHours(-20)));
    }
}

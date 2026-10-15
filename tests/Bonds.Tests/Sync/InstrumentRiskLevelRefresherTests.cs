using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Bonds.Infrastructure.Connectors.TInvest;
using Bonds.Infrastructure.Sync;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tinkoff.InvestApi.V1;
using Xunit;

namespace Bonds.Tests.Sync;

/// <summary>Задача 46 часть C: суточный bulk-вызов risk_level — БД-ворота, маппинг, пустой ответ. Без сети (Moq).</summary>
public class InstrumentRiskLevelRefresherTests
{
    private readonly Mock<ITInvestPortfolioClient> _client = new();
    private readonly Mock<IInstrumentRiskLevelRepository> _repo = new();
    private static readonly DateTime Now = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);

    private InstrumentRiskLevelRefresher Create() => new(_client.Object, _repo.Object, NullLogger<InstrumentRiskLevelRefresher>.Instance);

    private void ClientReturns(params TInvestBondRiskLevel[] levels) =>
        _client.Setup(c => c.GetBondRiskLevelsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(levels);

    [Fact]
    public async Task NeverFetched_CallsBondsOnce_AndUpsertsAllWithNowTimestamp()
    {
        _repo.Setup(r => r.GetLastFetchedAtAsync(It.IsAny<CancellationToken>())).ReturnsAsync((DateTime?)null);
        ClientReturns(
            new TInvestBondRiskLevel("RU000A0JX0J2", BrokerRiskLevel.Low),
            new TInvestBondRiskLevel("RU000A105TZ5", BrokerRiskLevel.High));
        IReadOnlyList<InstrumentRiskLevel>? saved = null;
        _repo.Setup(r => r.UpsertBatchAsync(It.IsAny<IReadOnlyList<InstrumentRiskLevel>>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<InstrumentRiskLevel>, CancellationToken>((items, _) => saved = items)
            .Returns(Task.CompletedTask);

        var count = await Create().RefreshIfDueAsync(Now);

        count.Should().Be(2);
        _client.Verify(c => c.GetBondRiskLevelsAsync(It.IsAny<CancellationToken>()), Times.Once);
        saved.Should().BeEquivalentTo([
            new InstrumentRiskLevel("RU000A0JX0J2", BrokerRiskLevel.Low, Now),
            new InstrumentRiskLevel("RU000A105TZ5", BrokerRiskLevel.High, Now),
        ]);
    }

    [Fact]
    public async Task FetchedTwoHoursAgo_SkipsWithoutCallingBroker()
    {
        _repo.Setup(r => r.GetLastFetchedAtAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Now.AddHours(-2));

        var count = await Create().RefreshIfDueAsync(Now);

        count.Should().Be(0);
        _client.Verify(c => c.GetBondRiskLevelsAsync(It.IsAny<CancellationToken>()), Times.Never);
        _repo.Verify(r => r.UpsertBatchAsync(It.IsAny<IReadOnlyList<InstrumentRiskLevel>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task FetchedTwentyOneHoursAgo_RefreshesAgain()
    {
        _repo.Setup(r => r.GetLastFetchedAtAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Now.AddHours(-21));
        ClientReturns(new TInvestBondRiskLevel("RU000A0JX0J2", BrokerRiskLevel.Moderate));

        (await Create().RefreshIfDueAsync(Now)).Should().Be(1);
    }

    [Fact]
    public async Task EmptyBrokerResponse_WritesNothing_SoNextCycleRetries()
    {
        _repo.Setup(r => r.GetLastFetchedAtAsync(It.IsAny<CancellationToken>())).ReturnsAsync((DateTime?)null);
        ClientReturns();

        (await Create().RefreshIfDueAsync(Now)).Should().Be(0);
        _repo.Verify(r => r.UpsertBatchAsync(It.IsAny<IReadOnlyList<InstrumentRiskLevel>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task BrokerFailure_PropagatesToCallingStep()
    {
        _repo.Setup(r => r.GetLastFetchedAtAsync(It.IsAny<CancellationToken>())).ReturnsAsync((DateTime?)null);
        _client.Setup(c => c.GetBondRiskLevelsAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("outage"));

        var act = () => Create().RefreshIfDueAsync(Now);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Theory]
    [InlineData(RiskLevel.Low, BrokerRiskLevel.Low)]
    [InlineData(RiskLevel.Moderate, BrokerRiskLevel.Moderate)]
    [InlineData(RiskLevel.High, BrokerRiskLevel.High)]
    [InlineData(RiskLevel.Unspecified, BrokerRiskLevel.Unspecified)]
    [InlineData((RiskLevel)99, BrokerRiskLevel.Unspecified)]
    public void MapRiskLevel_MapsProtoEnum(RiskLevel proto, BrokerRiskLevel expected) =>
        TInvestPortfolioClient.MapRiskLevel(proto).Should().Be(expected);
}

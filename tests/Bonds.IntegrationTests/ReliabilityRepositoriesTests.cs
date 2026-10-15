using Bonds.Core.Models;
using Bonds.Infrastructure.Repositories;
using Bonds.IntegrationTests.Infrastructure;
using FluentAssertions;
using Xunit;

namespace Bonds.IntegrationTests;

/// <summary>Задача 46: хранилища <c>instrument_risk_levels</c> и <c>issuer_ratings</c> (идемпотентный upsert, чтение пачкой).</summary>
[Collection("Integration")]
public class ReliabilityRepositoriesTests
{
    private readonly TestWebApplicationFactory _factory;

    public ReliabilityRepositoriesTests(TestWebApplicationFactory factory) => _factory = factory;

    private string Conn => _factory.Database.ConnectionString;

    private static string Isin() => "RU" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();

    [Fact]
    public async Task RiskLevels_UpsertIsIdempotent_UpdatesLevelAndTimestamp_AndRoundTripsEnum()
    {
        var repo = new InstrumentRiskLevelRepository(Conn);
        var isinA = Isin();
        var isinB = Isin();
        var first = DateTime.UtcNow.AddDays(-2);

        await repo.UpsertBatchAsync([new InstrumentRiskLevel(isinA, BrokerRiskLevel.Low, first), new InstrumentRiskLevel(isinB, BrokerRiskLevel.High, first)]);
        await repo.UpsertBatchAsync([new InstrumentRiskLevel(isinA, BrokerRiskLevel.Moderate, DateTime.UtcNow)]);

        var all = await repo.GetAllAsync();
        all[isinA].Should().Be(BrokerRiskLevel.Moderate, "повторная запись обновляет, а не дублирует");
        all[isinB].Should().Be(BrokerRiskLevel.High);
        all[isinA.ToLowerInvariant()].Should().Be(BrokerRiskLevel.Moderate, "ключи нечувствительны к регистру");
        (await repo.GetLastFetchedAtAsync()).Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task Ratings_UpsertByInnAndAgency_ReadByInnsAndDelete()
    {
        var repo = new IssuerRatingRepository(Conn);
        var inn = ReliabilitySeed.NewInn();
        var other = ReliabilitySeed.NewInn();
        var date = DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(-3));

        await repo.UpsertAsync(new IssuerRating { Inn = inn, Agency = "АКРА", Rating = "A(RU)", RatingDate = date, Note = "первичный" });
        await repo.UpsertAsync(new IssuerRating { Inn = inn, Agency = "АКРА", Rating = "AA(RU)", RatingDate = date.AddDays(10), Note = null });
        await repo.UpsertAsync(new IssuerRating { Inn = inn, Agency = "Эксперт РА", Rating = "ruA", RatingDate = date });
        await repo.UpsertAsync(new IssuerRating { Inn = other, Agency = "АКРА", Rating = "BBB", RatingDate = date });

        var byInn = await repo.GetByInnAsync(inn);
        byInn.Should().HaveCount(2);
        byInn.Single(r => r.Agency == "АКРА").Rating.Should().Be("AA(RU)");
        byInn.Single(r => r.Agency == "АКРА").RatingDate.Should().Be(date.AddDays(10));
        byInn.Single(r => r.Agency == "АКРА").Note.Should().BeNull();

        var batch = await repo.GetByInnsAsync([inn, other, ReliabilitySeed.NewInn()]);
        batch.Keys.Should().BeEquivalentTo([inn, other]);

        (await repo.DeleteAsync(inn, "АКРА")).Should().BeTrue();
        (await repo.DeleteAsync(inn, "АКРА")).Should().BeFalse();
        (await repo.GetByInnAsync(inn)).Should().ContainSingle();
    }
}

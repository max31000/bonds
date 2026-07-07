using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Bonds.Core.Models;
using Bonds.Infrastructure.Connectors.Moex;
using Bonds.Infrastructure.Repositories;
using Bonds.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using Xunit;

namespace Bonds.IntegrationTests;

/// <summary>
/// Задача 27 часть A — критерии приёмки POST /api/universe/{secid}/materialize: заводит
/// Instrument + котировку тем же путём, что watchlist (<see cref="Bonds.Infrastructure.Sync.InstrumentEnrichmentService"/>),
/// отдаёт полные метрики движка; идемпотентно; несуществующий SECID/бумага не на MOEX → 422;
/// watchlist НЕ пополняется. Сеть не используется — IMoexIssClient подменяется моком (тот же
/// паттерн, что WatchlistEndpointsTests).
/// </summary>
[Collection("Integration")]
public class UniverseMaterializeEndpointTests
{
    private readonly TestWebApplicationFactory _factory;

    public UniverseMaterializeEndpointTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private HttpClient CreateClientWithMoexStub(Mock<IMoexIssClient> moexMock)
    {
        var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IMoexIssClient>();
                services.AddScoped(_ => moexMock.Object);
            });
        });
        return factory.CreateClient();
    }

    private async Task<HttpClient> CreateAuthorizedClientAsync(HttpClient client)
    {
        var userRepo = new UserRepository(_factory.Database.ConnectionString);
        var userId = await userRepo.CreateAsync(new User { TelegramId = Random.Shared.NextInt64(1, long.MaxValue) });

        var token = JwtTestHelper.GenerateToken(userId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return client;
    }

    private static Mock<IMoexIssClient> CreateHappyPathMoexMock(string isin, string secid)
    {
        var moexMock = new Mock<IMoexIssClient>();
        moexMock.Setup(m => m.ResolveSecidByIsinAsync(isin, It.IsAny<CancellationToken>())).ReturnsAsync(secid);
        moexMock.Setup(m => m.GetSecurityInfoAsync(secid, It.IsAny<CancellationToken>())).ReturnsAsync(new MoexSecurityInfo
        {
            Secid = secid,
            BoardId = "TQOB",
            FaceValue = 1000m,
            MatDate = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(3)),
            PrevPrice = 98m,
        });
        moexMock.Setup(m => m.GetBondizationAsync(secid, It.IsAny<CancellationToken>())).ReturnsAsync(new MoexBondizationResult
        {
            Secid = secid,
            Coupons =
            [
                new CouponSchedule { CouponDate = DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(-1)), ValueRub = 40m, PeriodDays = 182, IsKnown = true },
                new CouponSchedule { CouponDate = DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(5)), ValueRub = 40m, PeriodDays = 182, IsKnown = true },
            ],
        });
        moexMock.Setup(m => m.GetSecuritySearchAsync(isin, It.IsAny<CancellationToken>())).ReturnsAsync(new MoexSecuritySearch
        {
            Secid = secid,
            EmitentTitle = "Минфин РФ",
            TypeCode = "ofz_bond",
        });
        return moexMock;
    }

    private static BondUniverseEntry HealthyEntry(string secid, string isin) => new()
    {
        Secid = secid,
        Isin = isin,
        ShortName = $"BOND {secid}",
        SecName = $"Full name {secid}",
        FaceValue = 1000m,
        Sector = "Корпоративные",
        YieldFraction = 0.15m,
        DurationYears = 2m,
        PricePercent = 98m,
        TurnoverRub = 1_000_000m,
        BidPercent = 97.5m,
        OfferPercent = 98.5m,
        NumTrades = 10,
        ListLevel = 1,
        MaturityDate = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(3)),
        UpdatedAt = DateTime.UtcNow,
    };

    [Fact]
    public async Task PostMaterialize_KnownSecid_Returns200WithInstrumentAndMetrics()
    {
        var marker = Guid.NewGuid().ToString("N")[..5];
        var secid = $"MAT{marker}";
        var isin = $"RU000{marker}MA";

        var universeRepo = new BondUniverseRepository(_factory.Database.ConnectionString);
        await universeRepo.UpsertSnapshotBatchAsync([HealthyEntry(secid, isin)]);

        var moexMock = CreateHappyPathMoexMock(isin, secid);
        var httpClient = CreateClientWithMoexStub(moexMock);
        var client = await CreateAuthorizedClientAsync(httpClient);

        var response = await client.PostAsync($"/api/universe/{secid}/materialize", null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("secid").GetString().Should().Be(secid);
        body.GetProperty("isin").GetString().Should().Be(isin);
        body.GetProperty("instrumentId").GetUInt64().Should().BeGreaterThan(0);

        var metrics = body.GetProperty("metrics");
        metrics.GetProperty("modifiedDuration").ValueKind.Should().NotBe(JsonValueKind.Null);
        metrics.GetProperty("ytmEffective").ValueKind.Should().NotBe(JsonValueKind.Null);
        metrics.GetProperty("effectiveYield").ValueKind.Should().NotBe(JsonValueKind.Null);
        metrics.GetProperty("dataIncomplete").GetBoolean().Should().BeFalse();

        body.GetProperty("disclaimer").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task PostMaterialize_CalledTwice_IsIdempotent_SingleInstrument()
    {
        var marker = Guid.NewGuid().ToString("N")[..5];
        var secid = $"IDEM{marker}";
        var isin = $"RU000{marker}ID";

        var universeRepo = new BondUniverseRepository(_factory.Database.ConnectionString);
        await universeRepo.UpsertSnapshotBatchAsync([HealthyEntry(secid, isin)]);

        var moexMock = CreateHappyPathMoexMock(isin, secid);
        var httpClient = CreateClientWithMoexStub(moexMock);
        var client = await CreateAuthorizedClientAsync(httpClient);

        var first = await client.PostAsync($"/api/universe/{secid}/materialize", null);
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>();
        var firstInstrumentId = firstBody.GetProperty("instrumentId").GetUInt64();

        var second = await client.PostAsync($"/api/universe/{secid}/materialize", null);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        var secondBody = await second.Content.ReadFromJsonAsync<JsonElement>();
        var secondInstrumentId = secondBody.GetProperty("instrumentId").GetUInt64();

        secondInstrumentId.Should().Be(firstInstrumentId, "повторный вызов не должен заводить дубль инструмента");
    }

    [Fact]
    public async Task PostMaterialize_UnknownSecid_Returns422()
    {
        var httpClient = CreateClientWithMoexStub(new Mock<IMoexIssClient>());
        var client = await CreateAuthorizedClientAsync(httpClient);

        var response = await client.PostAsync("/api/universe/DOES-NOT-EXIST/materialize", null);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("type").GetString().Should().Be("ValidationException");
    }

    [Fact]
    public async Task PostMaterialize_SecidNotOnMoex_Returns422()
    {
        var marker = Guid.NewGuid().ToString("N")[..5];
        var secid = $"NOMOEX{marker}";
        var isin = $"RU000{marker}NO";

        var universeRepo = new BondUniverseRepository(_factory.Database.ConnectionString);
        await universeRepo.UpsertSnapshotBatchAsync([HealthyEntry(secid, isin)]);

        var moexMock = new Mock<IMoexIssClient>();
        moexMock.Setup(m => m.ResolveSecidByIsinAsync(isin, It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);

        var httpClient = CreateClientWithMoexStub(moexMock);
        var client = await CreateAuthorizedClientAsync(httpClient);

        var response = await client.PostAsync($"/api/universe/{secid}/materialize", null);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task PostMaterialize_DoesNotAddToWatchlist()
    {
        var marker = Guid.NewGuid().ToString("N")[..5];
        var secid = $"NOWL{marker}";
        var isin = $"RU000{marker}WL";

        var universeRepo = new BondUniverseRepository(_factory.Database.ConnectionString);
        await universeRepo.UpsertSnapshotBatchAsync([HealthyEntry(secid, isin)]);

        var moexMock = CreateHappyPathMoexMock(isin, secid);
        var httpClient = CreateClientWithMoexStub(moexMock);
        var client = await CreateAuthorizedClientAsync(httpClient);

        var response = await client.PostAsync($"/api/universe/{secid}/materialize", null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var watchlistResponse = await client.GetAsync("/api/watchlist");
        var watchlistBody = await watchlistResponse.Content.ReadFromJsonAsync<JsonElement>();
        watchlistBody.GetProperty("items").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task PostMaterialize_NoToken_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsync("/api/universe/ANY/materialize", null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}

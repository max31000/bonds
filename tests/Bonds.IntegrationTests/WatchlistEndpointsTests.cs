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
/// Задача 20 (часть A) — критерии приёмки: POST /api/watchlist валидирует ISIN на MOEX (422 —
/// не найден/не облигация), синхронно заводит инструмент в общий справочник и обогащает его тем же
/// путём, что позиции (BondSyncService); GET /api/watchlist отдаёт полный набор метрик тем же
/// расчётным путём (PortfolioHoldingsBuilder → BondMetricsCalculator); DELETE удаляет запись.
/// Сеть не используется — IMoexIssClient подменяется моком (тот же паттерн, что
/// PositionDetailEndpointTests/SettingsTokenValidationTests).
/// </summary>
[Collection("Integration")]
public class WatchlistEndpointsTests
{
    private readonly TestWebApplicationFactory _factory;

    public WatchlistEndpointsTests(TestWebApplicationFactory factory)
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

    [Fact]
    public async Task PostWatchlist_ValidIsin_Returns201_AndGetReturnsMetrics()
    {
        const string isin = "RU000A1WL001";
        const string secid = "SECWL001";
        var moexMock = CreateHappyPathMoexMock(isin, secid);

        var httpClient = CreateClientWithMoexStub(moexMock);
        var client = await CreateAuthorizedClientAsync(httpClient);

        var postResponse = await client.PostAsJsonAsync("/api/watchlist", new { isin, note = "увидел в обзоре" });
        postResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        var getResponse = await client.GetAsync("/api/watchlist");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await getResponse.Content.ReadFromJsonAsync<JsonElement>();
        var items = body.GetProperty("items");
        items.GetArrayLength().Should().Be(1);

        var item = items[0];
        item.GetProperty("isin").GetString().Should().Be(isin);
        item.GetProperty("note").GetString().Should().Be("увидел в обзоре");
        item.GetProperty("instrumentId").ValueKind.Should().NotBe(JsonValueKind.Null);
        item.GetProperty("modifiedDuration").ValueKind.Should().NotBe(JsonValueKind.Null);
        item.GetProperty("ytmEffective").ValueKind.Should().NotBe(JsonValueKind.Null);
        item.GetProperty("effectiveYield").ValueKind.Should().NotBe(JsonValueKind.Null);
        item.GetProperty("dataIncomplete").GetBoolean().Should().BeFalse();

        body.GetProperty("disclaimer").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task PostWatchlist_IsinNotFoundOnMoex_Returns422()
    {
        const string isin = "RU000NOTFOUND";
        var moexMock = new Mock<IMoexIssClient>();
        moexMock.Setup(m => m.ResolveSecidByIsinAsync(isin, It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);

        var httpClient = CreateClientWithMoexStub(moexMock);
        var client = await CreateAuthorizedClientAsync(httpClient);

        var response = await client.PostAsJsonAsync("/api/watchlist", new { isin });

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("type").GetString().Should().Be("ValidationException");
    }

    [Fact]
    public async Task PostWatchlist_NotABond_ResolvedSecidButNoSecurityInfo_Returns422()
    {
        // ResolveSecidByIsinAsync ищет только группу stock_bonds, но на случай, если securities.json
        // (рынок облигаций) всё равно не находит бумагу по SECID — тоже 422, не 500/200.
        const string isin = "RU000STOCK001";
        const string secid = "SECSTOCK1";
        var moexMock = new Mock<IMoexIssClient>();
        moexMock.Setup(m => m.ResolveSecidByIsinAsync(isin, It.IsAny<CancellationToken>())).ReturnsAsync(secid);
        moexMock.Setup(m => m.GetSecurityInfoAsync(secid, It.IsAny<CancellationToken>())).ReturnsAsync((MoexSecurityInfo?)null);

        var httpClient = CreateClientWithMoexStub(moexMock);
        var client = await CreateAuthorizedClientAsync(httpClient);

        var response = await client.PostAsJsonAsync("/api/watchlist", new { isin });

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task PostWatchlist_DuplicateIsin_Returns422()
    {
        const string isin = "RU000A1WL002";
        const string secid = "SECWL002";
        var moexMock = CreateHappyPathMoexMock(isin, secid);

        var httpClient = CreateClientWithMoexStub(moexMock);
        var client = await CreateAuthorizedClientAsync(httpClient);

        var first = await client.PostAsJsonAsync("/api/watchlist", new { isin });
        first.StatusCode.Should().Be(HttpStatusCode.Created);

        var second = await client.PostAsJsonAsync("/api/watchlist", new { isin });
        second.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task DeleteWatchlist_RemovesItem_SubsequentGetIsEmpty()
    {
        const string isin = "RU000A1WL003";
        const string secid = "SECWL003";
        var moexMock = CreateHappyPathMoexMock(isin, secid);

        var httpClient = CreateClientWithMoexStub(moexMock);
        var client = await CreateAuthorizedClientAsync(httpClient);

        var postResponse = await client.PostAsJsonAsync("/api/watchlist", new { isin });
        var created = await postResponse.Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetUInt64();

        var deleteResponse = await client.DeleteAsync($"/api/watchlist/{id}");
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var getResponse = await client.GetAsync("/api/watchlist");
        var body = await getResponse.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("items").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task DeleteWatchlist_NotFound_Returns404()
    {
        var httpClient = CreateClientWithMoexStub(new Mock<IMoexIssClient>());
        var client = await CreateAuthorizedClientAsync(httpClient);

        var response = await client.DeleteAsync("/api/watchlist/999999");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Bonds.Core.Models;
using Bonds.Infrastructure.Repositories;
using Bonds.IntegrationTests.Infrastructure;
using FluentAssertions;
using Xunit;

namespace Bonds.IntegrationTests;

/// <summary>
/// Plan/16 часть A, критерий приёмки: "Интеграционный тест — засеять тики двух инструментов со
/// сдвинутыми временами → /api/live/portfolio-intraday возвращает forward-filled сумму". Также
/// покрывает GET /api/live/positions (401 без токена, пустой портфель, isStale-фолбэк на
/// market_quotes при отсутствии intraday-тиков, актуальная цена/дневное изменение при наличии тика).
/// </summary>
[Collection("Integration")]
public class LiveEndpointsTests
{
    private readonly TestWebApplicationFactory _factory;

    public LiveEndpointsTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private async Task<(HttpClient Client, ulong AccountId)> CreateAuthorizedClientAsync()
    {
        var userRepo = new UserRepository(_factory.Database.ConnectionString);
        var userId = await userRepo.CreateAsync(new User { TelegramId = Random.Shared.NextInt64(1, long.MaxValue) });

        var accountRepo = new AccountRepository(_factory.Database.ConnectionString);
        var accountId = await accountRepo.CreateAsync(new Account { UserId = userId, Name = "Основной счёт" });

        var client = _factory.CreateClient();
        var token = JwtTestHelper.GenerateToken(userId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return (client, accountId);
    }

    private async Task<(ulong InstrumentId, ulong PositionId)> SeedPositionAsync(ulong accountId, decimal quantity = 10, string? figi = null)
    {
        var instrumentRepo = new InstrumentRepository(_factory.Database.ConnectionString);
        var isin = $"RU{Guid.NewGuid():N}".Substring(0, 12);
        var instrumentId = await instrumentRepo.UpsertAsync(new Instrument
        {
            Isin = isin,
            Figi = figi ?? $"FIGI{Guid.NewGuid():N}".Substring(0, 12),
            Issuer = "Минфин РФ",
            FaceValue = 1000m,
            Currency = "RUB",
            CouponType = CouponType.Fixed,
            MaturityDate = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(3)),
        });

        var positionRepo = new PositionRepository(_factory.Database.ConnectionString);
        var positionId = await positionRepo.UpsertAsync(new Position
        {
            AccountId = accountId,
            InstrumentId = instrumentId,
            Quantity = quantity,
            AvgPurchasePrice = 1000m,
        });

        return (instrumentId, positionId);
    }

    // ─── 401 без токена ─────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("GET", "/api/live/positions")]
    [InlineData("GET", "/api/live/portfolio-intraday")]
    public async Task Endpoint_WithoutToken_Returns401(string method, string path)
    {
        var client = _factory.CreateClient();
        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ─── GET /api/live/positions ────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetLivePositions_EmptyPortfolio_Returns200_WithEmptyList()
    {
        var (client, _) = await CreateAuthorizedClientAsync();

        var response = await client.GetAsync("/api/live/positions");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("positions").GetArrayLength().Should().Be(0);
        body.GetProperty("totalMarketValueRub").GetDecimal().Should().Be(0m);
    }

    [Fact]
    public async Task GetLivePositions_NoIntradayTickYet_FallsBackToLastMarketQuote_MarkedStale()
    {
        var (client, accountId) = await CreateAuthorizedClientAsync();
        var (instrumentId, positionId) = await SeedPositionAsync(accountId, quantity: 10);

        var quoteRepo = new MarketQuoteRepository(_factory.Database.ConnectionString);
        await quoteRepo.UpsertAsync(new MarketQuote
        {
            InstrumentId = instrumentId,
            AsOf = DateOnly.FromDateTime(DateTime.UtcNow),
            CleanPrice = 980m,
            DirtyPrice = 1000m,
            Accrued = 20m,
            Source = MarketQuoteSource.TInvest,
        });

        var response = await client.GetAsync("/api/live/positions");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var row = body.GetProperty("positions")[0];

        row.GetProperty("positionId").GetUInt64().Should().Be(positionId);
        row.GetProperty("isStale").GetBoolean().Should().BeTrue();
        row.GetProperty("lastPriceRub").GetDecimal().Should().Be(1000m);
        row.GetProperty("marketValueRub").GetDecimal().Should().Be(10000m); // 1000 × 10
    }

    [Fact]
    public async Task GetLivePositions_WithIntradayTick_UsesTickPrice_NotStale_AndComputesDayChange()
    {
        var (client, accountId) = await CreateAuthorizedClientAsync();
        var (instrumentId, _) = await SeedPositionAsync(accountId, quantity: 10);

        var quoteRepo = new MarketQuoteRepository(_factory.Database.ConnectionString);
        await quoteRepo.UpsertAsync(new MarketQuote
        {
            InstrumentId = instrumentId,
            AsOf = DateOnly.FromDateTime(DateTime.UtcNow),
            CleanPrice = 980m,
            DirtyPrice = 1000m, // Точка отсчёта "день"
            Accrued = 20m,
            Source = MarketQuoteSource.TInvest,
        });

        var intradayRepo = new IntradayQuoteRepository(_factory.Database.ConnectionString);
        await intradayRepo.InsertAndPruneAsync(
            new IntradayQuote { InstrumentId = instrumentId, TsUtc = DateTime.UtcNow, DirtyPriceRub = 1010m },
            DateTime.UtcNow.AddDays(-8));

        var response = await client.GetAsync("/api/live/positions");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var row = body.GetProperty("positions")[0];

        row.GetProperty("isStale").GetBoolean().Should().BeFalse();
        row.GetProperty("lastPriceRub").GetDecimal().Should().Be(1010m);
        row.GetProperty("marketValueRub").GetDecimal().Should().Be(10100m); // 1010 × 10
        // (1010 - 1000) / 1000 = 0.01
        row.GetProperty("changeDayPercent").GetDecimal().Should().Be(0.01m);
        body.GetProperty("totalMarketValueRub").GetDecimal().Should().Be(10100m);
    }

    // ─── GET /api/live/portfolio-intraday — критерий приёмки плана ─────────────────────────────

    [Fact]
    public async Task GetPortfolioIntraday_TwoInstrumentsOffsetTimestamps_ReturnsForwardFilledSum()
    {
        var (client, accountId) = await CreateAuthorizedClientAsync();
        var (instrumentId1, _) = await SeedPositionAsync(accountId, quantity: 10);
        var (instrumentId2, _) = await SeedPositionAsync(accountId, quantity: 20);

        var t0 = DateTime.UtcNow.AddMinutes(-10);
        var intradayRepo = new IntradayQuoteRepository(_factory.Database.ConnectionString);
        var cutoff = DateTime.UtcNow.AddDays(-8);

        // Инструмент 1 тикает в t0 и t0+4мин; инструмент 2 — в t0+2мин (сдвинуто).
        await intradayRepo.InsertAndPruneAsync(
            new IntradayQuote { InstrumentId = instrumentId1, TsUtc = t0, DirtyPriceRub = 1000m }, cutoff);
        await intradayRepo.InsertAndPruneAsync(
            new IntradayQuote { InstrumentId = instrumentId2, TsUtc = t0.AddMinutes(2), DirtyPriceRub = 500m }, cutoff);
        await intradayRepo.InsertAndPruneAsync(
            new IntradayQuote { InstrumentId = instrumentId1, TsUtc = t0.AddMinutes(4), DirtyPriceRub = 1020m }, cutoff);

        var response = await client.GetAsync("/api/live/portfolio-intraday?range=1d");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var points = body.GetProperty("points").EnumerateArray().ToList();

        points.Should().HaveCount(3);

        // t0: только инструмент 1 (10 × 1000 = 10000) — инструмент 2 ещё не тикнул.
        points[0].GetProperty("totalMarketValueRub").GetDecimal().Should().Be(10000m);

        // t0+2: инструмент 1 forward-fill (10×1000=10000) + инструмент 2 новый тик (20×500=10000) = 20000.
        points[1].GetProperty("totalMarketValueRub").GetDecimal().Should().Be(20000m);

        // t0+4: инструмент 1 новый тик (10×1020=10200) + инструмент 2 forward-fill (20×500=10000) = 20200.
        points[2].GetProperty("totalMarketValueRub").GetDecimal().Should().Be(20200m);
    }

    [Fact]
    public async Task GetPortfolioIntraday_NoTicks_ReturnsEmptyPoints()
    {
        var (client, accountId) = await CreateAuthorizedClientAsync();
        await SeedPositionAsync(accountId);

        var response = await client.GetAsync("/api/live/portfolio-intraday");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("points").GetArrayLength().Should().Be(0);
    }
}

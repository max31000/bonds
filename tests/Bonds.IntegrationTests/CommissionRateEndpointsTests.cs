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
/// Plan/22 — критерии приёмки частей D/E: GET /api/settings отдаёт авто-оценку ставки комиссии
/// и эффективную ставку+источник; PUT валидирует override; replacement/allocation/ifSoldNow
/// применяют оценённую ставку из журнала, а override в настройках побеждает оценку.
/// </summary>
[Collection("Integration")]
public class CommissionRateEndpointsTests
{
    private readonly TestWebApplicationFactory _factory;

    public CommissionRateEndpointsTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private async Task<(HttpClient Client, ulong UserId, ulong AccountId)> CreateAuthorizedClientAsync()
    {
        var userRepo = new UserRepository(_factory.Database.ConnectionString);
        var userId = await userRepo.CreateAsync(new User { TelegramId = Random.Shared.NextInt64(1, long.MaxValue) });

        var accountRepo = new AccountRepository(_factory.Database.ConnectionString);
        var accountId = await accountRepo.CreateAsync(new Account { UserId = userId, Name = "Основной счёт" });

        var client = _factory.CreateClient();
        var token = JwtTestHelper.GenerateToken(userId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return (client, userId, accountId);
    }

    private async Task<(ulong InstrumentId, ulong PositionId)> SeedPositionAsync(ulong accountId)
    {
        var instrumentRepo = new InstrumentRepository(_factory.Database.ConnectionString);
        var isin = $"RU{Guid.NewGuid():N}".Substring(0, 12);
        var instrumentId = await instrumentRepo.UpsertAsync(new Instrument
        {
            Isin = isin,
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
            Quantity = 10,
            AvgPurchasePrice = 1000m,
        });

        return (instrumentId, positionId);
    }

    private async Task SeedFeeJournalAsync(ulong accountId)
    {
        // ~0.046% ставка: 100_000 оборот, 46 комиссия (Buy) + 50_000 оборот, 23 комиссия (Sell).
        var operationRepo = new OperationRepository(_factory.Database.ConnectionString);
        await operationRepo.UpsertManyByExternalIdAsync(new[]
        {
            new Operation { AccountId = accountId, Type = OperationType.Buy, Date = DateTime.UtcNow.AddDays(-10), AmountRub = -100_000m, ExternalId = $"buy-{Guid.NewGuid()}" },
            new Operation { AccountId = accountId, Type = OperationType.Fee, Date = DateTime.UtcNow.AddDays(-10), AmountRub = -46m, ExternalId = $"fee1-{Guid.NewGuid()}" },
            new Operation { AccountId = accountId, Type = OperationType.Sell, Date = DateTime.UtcNow.AddDays(-5), AmountRub = 50_000m, ExternalId = $"sell-{Guid.NewGuid()}" },
            new Operation { AccountId = accountId, Type = OperationType.Fee, Date = DateTime.UtcNow.AddDays(-5), AmountRub = -23m, ExternalId = $"fee2-{Guid.NewGuid()}" },
        });
    }

    // ─── GET /api/settings — контекст комиссии ─────────────────────────────────────────────

    [Fact]
    public async Task GetSettings_NoJournalNoOverride_EffectiveRateIsDefault()
    {
        var (client, _, _) = await CreateAuthorizedClientAsync();

        var response = await client.GetAsync("/api/settings");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("commissionAutoEstimate").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("commissionEffectiveRate").GetDecimal().Should().Be(0.003m);
        body.GetProperty("commissionEffectiveSource").GetString().Should().Be("Default");
        body.GetProperty("commissionRateOverride").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task GetSettings_WithFeeJournal_ReturnsAutoEstimate_AndUsesItAsEffective()
    {
        var (client, _, accountId) = await CreateAuthorizedClientAsync();
        await SeedFeeJournalAsync(accountId);

        var response = await client.GetAsync("/api/settings");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var autoEstimate = body.GetProperty("commissionAutoEstimate");
        autoEstimate.ValueKind.Should().NotBe(JsonValueKind.Null);
        autoEstimate.GetProperty("tradeCount").GetInt32().Should().Be(2);
        autoEstimate.GetProperty("rate").GetDecimal().Should().BeApproximately(69m / 150_000m, 0.0000001m);

        body.GetProperty("commissionEffectiveSource").GetString().Should().Be("EstimatedFromTrades");
        body.GetProperty("commissionEffectiveRate").GetDecimal().Should().BeApproximately(69m / 150_000m, 0.0000001m);
    }

    [Fact]
    public async Task PutSettings_OverrideOutOfRange_Returns422_AndDoesNotPersist()
    {
        var (client, _, _) = await CreateAuthorizedClientAsync();

        var response = await client.PutAsJsonAsync("/api/settings", new { commissionRateOverride = 0.10m });

        response.StatusCode.Should().Be((HttpStatusCode)422);

        var getResponse = await client.GetAsync("/api/settings");
        var getBody = await getResponse.Content.ReadFromJsonAsync<JsonElement>();
        getBody.GetProperty("commissionRateOverride").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task PutSettings_ValidOverride_Persists_AndWinsOverJournalEstimate()
    {
        var (client, _, accountId) = await CreateAuthorizedClientAsync();
        await SeedFeeJournalAsync(accountId); // была бы оценка ~0.046%

        var response = await client.PutAsJsonAsync("/api/settings", new { commissionRateOverride = 0.0004m });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("commissionRateOverride").GetDecimal().Should().Be(0.0004m);
        body.GetProperty("commissionEffectiveSource").GetString().Should().Be("UserOverride");
        body.GetProperty("commissionEffectiveRate").GetDecimal().Should().Be(0.0004m);
    }

    // ─── POST /api/analytics/replacement — резолвер вместо дефолта ─────────────────────────

    [Fact]
    public async Task PostReplacement_NoExplicitRate_UsesJournalEstimate_NotHardcodedDefault()
    {
        var (client, _, accountId) = await CreateAuthorizedClientAsync();
        var (_, positionId1) = await SeedPositionAsync(accountId);
        var (_, positionId2) = await SeedPositionAsync(accountId);
        await SeedFeeJournalAsync(accountId);

        var response = await client.PostAsJsonAsync("/api/analytics/replacement", new
        {
            holdPositionId = positionId1,
            targetPositionId = positionId2,
            horizonYears = 2,
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("commissionRateSource").GetString().Should().Be("EstimatedFromTrades");
        body.GetProperty("sellCommissionRateUsed").GetDecimal().Should().BeApproximately(69m / 150_000m, 0.0000001m);
        body.GetProperty("sellCommissionRateUsed").GetDecimal().Should().NotBe(0.003m, "дефолт 0.3% должен быть заменён оценкой из журнала");
    }

    [Fact]
    public async Task PostReplacement_ExplicitRate_StillWinsOverResolver()
    {
        var (client, _, accountId) = await CreateAuthorizedClientAsync();
        var (_, positionId1) = await SeedPositionAsync(accountId);
        var (_, positionId2) = await SeedPositionAsync(accountId);
        await SeedFeeJournalAsync(accountId);

        var response = await client.PostAsJsonAsync("/api/analytics/replacement", new
        {
            holdPositionId = positionId1,
            targetPositionId = positionId2,
            horizonYears = 2,
            sellCommissionRate = 0.01m,
            buyCommissionRate = 0.01m,
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("sellCommissionRateUsed").GetDecimal().Should().Be(0.01m);
        body.GetProperty("commissionRateSource").GetString().Should().Be("ExplicitRequest");
    }

    [Fact]
    public async Task PostReplacement_OverrideInSettings_WinsOverJournalEstimate()
    {
        var (client, _, accountId) = await CreateAuthorizedClientAsync();
        var (_, positionId1) = await SeedPositionAsync(accountId);
        var (_, positionId2) = await SeedPositionAsync(accountId);
        await SeedFeeJournalAsync(accountId);
        await client.PutAsJsonAsync("/api/settings", new { commissionRateOverride = 0.0004m });

        var response = await client.PostAsJsonAsync("/api/analytics/replacement", new
        {
            holdPositionId = positionId1,
            targetPositionId = positionId2,
            horizonYears = 2,
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("commissionRateSource").GetString().Should().Be("UserOverride");
        body.GetProperty("sellCommissionRateUsed").GetDecimal().Should().Be(0.0004m);
    }

    // ─── GET /api/analytics/allocation — резолвер вместо дефолта ────────────────────────────

    [Fact]
    public async Task GetAllocation_WithJournalEstimate_UsesEstimatedRate()
    {
        var (client, _, accountId) = await CreateAuthorizedClientAsync();
        await SeedFeeJournalAsync(accountId);

        var response = await client.GetAsync("/api/analytics/allocation?amountRub=15000");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("commissionRateSource").GetString().Should().Be("EstimatedFromTrades");
        body.GetProperty("commissionRateUsed").GetDecimal().Should().BeApproximately(69m / 150_000m, 0.0000001m);
    }

    // ─── GET /api/positions/{id} → ifSoldNow — резолвер вместо дефолта ─────────────────────

    [Fact]
    public async Task GetPositionById_IfSoldNow_UsesJournalEstimate_NotHardcodedDefault()
    {
        var (client, _, accountId) = await CreateAuthorizedClientAsync();
        var (_, positionId) = await SeedPositionAsync(accountId);
        await SeedFeeJournalAsync(accountId);

        var response = await client.GetAsync($"/api/positions/{positionId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var ifSoldNow = body.GetProperty("ifSoldNow");
        ifSoldNow.GetProperty("commissionRateSource").GetString().Should().Be("EstimatedFromTrades");
        ifSoldNow.GetProperty("commissionRate").GetDecimal().Should().BeApproximately(69m / 150_000m, 0.0000001m);
    }

    [Fact]
    public async Task GetPositionById_IfSoldNow_NoJournal_FallsBackToDefaultRate()
    {
        var (client, _, accountId) = await CreateAuthorizedClientAsync();
        var (_, positionId) = await SeedPositionAsync(accountId);

        var response = await client.GetAsync($"/api/positions/{positionId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var ifSoldNow = body.GetProperty("ifSoldNow");
        ifSoldNow.GetProperty("commissionRateSource").GetString().Should().Be("Default");
        ifSoldNow.GetProperty("commissionRate").GetDecimal().Should().Be(0.003m);
    }
}

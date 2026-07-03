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
/// Plan/19 §A — критерий приёмки: GET /api/positions/{id} отдаёт график цены (закэшированный из
/// MOEX ISS), полный календарь бумаги (купоны/амортизации/оферты — прошедшие и будущие), журнал
/// операций пользователя по инструменту и оценку «если продать сейчас». Сеть не используется —
/// <see cref="IMoexIssClient"/> подменяется моком (см. doc-comment <see cref="TestWebApplicationFactory"/>
/// про паттерн <c>WithWebHostBuilder</c> + <c>RemoveAll</c>, как в SettingsTokenValidationTests).
/// </summary>
[Collection("Integration")]
public class PositionDetailEndpointTests
{
    private readonly TestWebApplicationFactory _factory;

    public PositionDetailEndpointTests(TestWebApplicationFactory factory)
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

    private async Task<(HttpClient Client, ulong AccountId)> CreateAuthorizedClientAsync(HttpClient client)
    {
        var userRepo = new UserRepository(_factory.Database.ConnectionString);
        var userId = await userRepo.CreateAsync(new User { TelegramId = Random.Shared.NextInt64(1, long.MaxValue) });

        var accountRepo = new AccountRepository(_factory.Database.ConnectionString);
        var accountId = await accountRepo.CreateAsync(new Account { UserId = userId, Name = "Основной счёт" });

        var token = JwtTestHelper.GenerateToken(userId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return (client, accountId);
    }

    private async Task<(ulong InstrumentId, ulong PositionId)> SeedPositionAsync(ulong accountId, string secid = "RU000A10AZ45")
    {
        var instrumentRepo = new InstrumentRepository(_factory.Database.ConnectionString);
        var isin = $"RU{Guid.NewGuid():N}".Substring(0, 12);
        var instrumentId = await instrumentRepo.UpsertAsync(new Instrument
        {
            Isin = isin,
            Secid = secid,
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

    [Fact]
    public async Task GetPositionById_Existing_Returns200_WithAllSectionsPresent()
    {
        var moexMock = new Mock<IMoexIssClient>();
        moexMock
            .Setup(m => m.GetHistoryPricesAsync(It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MoexHistoryPricePoint>
            {
                new(DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-2)), 101.2m, 3.5m),
                new(DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)), 101.5m, 4.0m),
            });

        var httpClient = CreateClientWithMoexStub(moexMock);
        var (client, accountId) = await CreateAuthorizedClientAsync(httpClient);
        var (instrumentId, positionId) = await SeedPositionAsync(accountId);

        // Купон в прошлом и в будущем + амортизация + оферта — проверяем, что все секции календаря отданы.
        var couponRepo = new CouponScheduleRepository(_factory.Database.ConnectionString);
        await couponRepo.ReplaceForInstrumentAsync(instrumentId, new[]
        {
            new CouponSchedule { InstrumentId = instrumentId, CouponDate = DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(-1)), ValueRub = 40m, PeriodDays = 182, IsKnown = true },
            new CouponSchedule { InstrumentId = instrumentId, CouponDate = DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(2)), ValueRub = 40m, PeriodDays = 182, IsKnown = true },
        });

        var amortizationRepo = new AmortizationScheduleRepository(_factory.Database.ConnectionString);
        await amortizationRepo.ReplaceForInstrumentAsync(instrumentId, new[]
        {
            new AmortizationSchedule { InstrumentId = instrumentId, Date = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(2)), AmountRub = 500m },
        });

        var offerRepo = new OfferScheduleRepository(_factory.Database.ConnectionString);
        await offerRepo.ReplaceForInstrumentAsync(instrumentId, new[]
        {
            new OfferSchedule { InstrumentId = instrumentId, Date = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(1)), OfferType = OfferType.Put, IsExecuted = false },
        });

        var operationRepo = new OperationRepository(_factory.Database.ConnectionString);
        await operationRepo.UpsertManyByExternalIdAsync(new[]
        {
            new Operation
            {
                AccountId = accountId,
                InstrumentId = instrumentId,
                Type = OperationType.Buy,
                Date = DateTime.UtcNow.AddMonths(-6),
                AmountRub = -10_000m,
                Quantity = 10m,
                ExternalId = $"buy-{Guid.NewGuid()}",
            },
            new Operation
            {
                AccountId = accountId,
                InstrumentId = instrumentId,
                Type = OperationType.Coupon,
                Date = DateTime.UtcNow.AddMonths(-1),
                AmountRub = 400m,
                ExternalId = $"coupon-{Guid.NewGuid()}",
            },
        });

        var response = await client.GetAsync($"/api/positions/{positionId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        // priceHistory — из мока MOEX, закэшировано.
        var priceHistory = body.GetProperty("priceHistory");
        priceHistory.ValueKind.Should().Be(JsonValueKind.Array);
        priceHistory.GetArrayLength().Should().Be(2);

        // couponSchedule — обе точки (прошлая и будущая), сумма на позицию = ValueRub × Quantity.
        var coupons = body.GetProperty("couponSchedule");
        coupons.GetArrayLength().Should().Be(2);
        var pastCoupon = coupons.EnumerateArray().Single(c => c.GetProperty("isPast").GetBoolean());
        pastCoupon.GetProperty("valueForPositionRub").GetDecimal().Should().Be(400m); // 40 × 10

        // amortizationSchedule / offerSchedule присутствуют.
        body.GetProperty("amortizationSchedule").GetArrayLength().Should().Be(1);
        body.GetProperty("offerSchedule").GetArrayLength().Should().Be(1);

        // operations — журнал по инструменту, новые сверху (Coupon позже Buy).
        var operations = body.GetProperty("operations");
        operations.GetArrayLength().Should().Be(2);
        operations[0].GetProperty("type").GetString().Should().Be("Coupon");

        // ifSoldNow — выручка минус комиссия, P&L доступен (журнал полностью покрывает остаток).
        var ifSoldNow = body.GetProperty("ifSoldNow");
        ifSoldNow.GetProperty("pnlAvailable").GetBoolean().Should().BeTrue();
        ifSoldNow.GetProperty("disclaimer").GetString().Should().NotBeNullOrEmpty();

        body.GetProperty("disclaimer").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task GetPositionById_NoMoexData_ReturnsEmptyPriceHistory_DoesNotFail()
    {
        // Устойчивость к неполноте источника (spec §4.4): MOEX не вернул ничего — эндпоинт
        // не падает, отдаёт пустой priceHistory.
        var moexMock = new Mock<IMoexIssClient>();
        moexMock
            .Setup(m => m.GetHistoryPricesAsync(It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MoexHistoryPricePoint>());

        var httpClient = CreateClientWithMoexStub(moexMock);
        var (client, accountId) = await CreateAuthorizedClientAsync(httpClient);
        var (_, positionId) = await SeedPositionAsync(accountId);

        var response = await client.GetAsync($"/api/positions/{positionId}?range=1m");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("priceHistory").GetArrayLength().Should().Be(0);
        body.GetProperty("ifSoldNow").GetProperty("pnlAvailable").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task GetPositionById_SecondRequest_OnlyRefetchesTail_FromMoex()
    {
        // Дозагрузка хвоста (plan/19 §A.1): второй запрос за тот же диапазон не должен запрашивать
        // у MOEX весь диапазон заново — только дни после уже закэшированного максимума.
        var moexMock = new Mock<IMoexIssClient>();
        var calls = new List<(DateOnly From, DateOnly To)>();
        moexMock
            .Setup(m => m.GetHistoryPricesAsync(It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .Callback<string, DateOnly, DateOnly, CancellationToken>((_, from, to, _) => calls.Add((from, to)))
            .ReturnsAsync(new List<MoexHistoryPricePoint>
            {
                new(DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-3)), 100m, 1m),
            });

        var httpClient = CreateClientWithMoexStub(moexMock);
        var (client, accountId) = await CreateAuthorizedClientAsync(httpClient);
        var (_, positionId) = await SeedPositionAsync(accountId);

        var first = await client.GetAsync($"/api/positions/{positionId}?range=1m");
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        var second = await client.GetAsync($"/api/positions/{positionId}?range=1m");
        second.StatusCode.Should().Be(HttpStatusCode.OK);

        calls.Should().HaveCount(2);
        // Второй запрос начинается позже первого (не повторяет уже закэшированный диапазон целиком).
        calls[1].From.Should().BeAfter(calls[0].From);
    }
}

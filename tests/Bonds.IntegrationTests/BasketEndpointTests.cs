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
/// Задача 29 — POST /api/analytics/basket: критерии приёмки — корзина из позиции портфеля +
/// watchlist-бумаги (вне портфеля) считается одним запросом; 422-валидации (сумма/веса/несуществующий
/// инструмент); what-if дельты (before/after) присутствуют в ответе. Сеть не используется — счёт и
/// котировки засеяны напрямую через репозитории (тот же паттерн, что ReplacementMatrixEndpointTests).
/// </summary>
[Collection("Integration")]
public class BasketEndpointTests
{
    private readonly TestWebApplicationFactory _factory;

    public BasketEndpointTests(TestWebApplicationFactory factory)
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

    /// <summary>Заводит позицию с реальной котировкой + купоном, дающими сходящийся YTM (тот же паттерн, что ReplacementMatrixEndpointTests).</summary>
    private async Task<(ulong InstrumentId, ulong PositionId)> SeedYieldingPositionAsync(
        ulong accountId, decimal cleanPrice, decimal couponValueRub, int maturityYears = 3, decimal quantity = 10m)
    {
        var instrumentRepo = new InstrumentRepository(_factory.Database.ConnectionString);
        var isin = $"RU{Guid.NewGuid():N}".Substring(0, 12);
        var instrumentId = await instrumentRepo.UpsertAsync(new Instrument
        {
            Isin = isin,
            Issuer = "Эмитент " + isin,
            FaceValue = 1000m,
            Currency = "RUB",
            CouponType = CouponType.Fixed,
            MaturityDate = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(maturityYears)),
        });

        var couponRepo = new CouponScheduleRepository(_factory.Database.ConnectionString);
        await couponRepo.ReplaceForInstrumentAsync(instrumentId, new[]
        {
            new CouponSchedule { InstrumentId = instrumentId, CouponDate = DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(-1)), ValueRub = couponValueRub, PeriodDays = 182, IsKnown = true },
            new CouponSchedule { InstrumentId = instrumentId, CouponDate = DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(5)), ValueRub = couponValueRub, PeriodDays = 182, IsKnown = true },
        });

        var quoteRepo = new MarketQuoteRepository(_factory.Database.ConnectionString);
        await quoteRepo.UpsertAsync(new MarketQuote
        {
            InstrumentId = instrumentId,
            AsOf = DateOnly.FromDateTime(DateTime.UtcNow),
            CleanPrice = cleanPrice,
            DirtyPrice = cleanPrice,
            Accrued = 0m,
            Source = MarketQuoteSource.Moex,
        });

        var positionRepo = new PositionRepository(_factory.Database.ConnectionString);
        var positionId = await positionRepo.UpsertAsync(new Position
        {
            AccountId = accountId,
            InstrumentId = instrumentId,
            Quantity = quantity,
            AvgPurchasePrice = cleanPrice,
        });

        return (instrumentId, positionId);
    }

    /// <summary>Заводит инструмент + котировку/купон БЕЗ позиции (кандидат вне портфеля — тот же путь, что watchlist в GetAllocation/GetReplacementMatrix).</summary>
    private async Task<ulong> SeedInstrumentWithoutPositionAsync(decimal cleanPrice, decimal couponValueRub, int maturityYears = 3)
    {
        var instrumentRepo = new InstrumentRepository(_factory.Database.ConnectionString);
        var isin = $"RU{Guid.NewGuid():N}".Substring(0, 12);
        var instrumentId = await instrumentRepo.UpsertAsync(new Instrument
        {
            Isin = isin,
            Issuer = "Watchlist Эмитент " + isin,
            FaceValue = 1000m,
            Currency = "RUB",
            CouponType = CouponType.Fixed,
            MaturityDate = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(maturityYears)),
        });

        var couponRepo = new CouponScheduleRepository(_factory.Database.ConnectionString);
        await couponRepo.ReplaceForInstrumentAsync(instrumentId, new[]
        {
            new CouponSchedule { InstrumentId = instrumentId, CouponDate = DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(-1)), ValueRub = couponValueRub, PeriodDays = 182, IsKnown = true },
            new CouponSchedule { InstrumentId = instrumentId, CouponDate = DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(5)), ValueRub = couponValueRub, PeriodDays = 182, IsKnown = true },
        });

        var quoteRepo = new MarketQuoteRepository(_factory.Database.ConnectionString);
        await quoteRepo.UpsertAsync(new MarketQuote
        {
            InstrumentId = instrumentId,
            AsOf = DateOnly.FromDateTime(DateTime.UtcNow),
            CleanPrice = cleanPrice,
            DirtyPrice = cleanPrice,
            Accrued = 0m,
            Source = MarketQuoteSource.Moex,
        });

        return instrumentId;
    }

    [Fact]
    public async Task PostBasket_PositionPlusWatchlistBond_ComputesBasketAndWhatIf()
    {
        var (client, userId, accountId) = await CreateAuthorizedClientAsync();
        var (positionInstrumentId, _) = await SeedYieldingPositionAsync(accountId, cleanPrice: 1000m, couponValueRub: 30m, quantity: 50m);
        var watchlistInstrumentId = await SeedInstrumentWithoutPositionAsync(cleanPrice: 900m, couponValueRub: 80m);

        var watchlistRepo = new WatchlistItemRepository(_factory.Database.ConnectionString);
        var watchlistIsin = (await new InstrumentRepository(_factory.Database.ConnectionString).GetByIdAsync(watchlistInstrumentId))!.Isin;
        await watchlistRepo.CreateAsync(new WatchlistItem { UserId = userId, Isin = watchlistIsin, AddedAtUtc = DateTime.UtcNow });

        var response = await client.PostAsJsonAsync("/api/analytics/basket", new
        {
            amountRub = 20_000m,
            lines = new[]
            {
                new { instrumentId = positionInstrumentId, weightFraction = 0.5m },
                new { instrumentId = watchlistInstrumentId, weightFraction = 0.5m },
            },
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        var basket = body.GetProperty("basket");
        var lines = basket.GetProperty("lines").EnumerateArray().ToList();
        lines.Should().HaveCount(2);
        lines.Should().Contain(l => l.GetProperty("instrumentId").GetUInt64() == positionInstrumentId && l.GetProperty("quantity").GetDecimal() > 0m);
        lines.Should().Contain(l => l.GetProperty("instrumentId").GetUInt64() == watchlistInstrumentId && l.GetProperty("quantity").GetDecimal() > 0m);
        basket.GetProperty("metrics").GetProperty("weightedYield").ValueKind.Should().NotBe(JsonValueKind.Undefined);

        var whatIf = body.GetProperty("whatIf");
        var before = whatIf.GetProperty("before");
        var after = whatIf.GetProperty("after");
        after.GetProperty("totalValueRub").GetDecimal().Should().BeGreaterThan(before.GetProperty("totalValueRub").GetDecimal());

        body.GetProperty("disclaimer").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task PostBasket_EmptyPortfolio_WatchlistOnlyBasket_BeforeIsZero()
    {
        var (client, _, _) = await CreateAuthorizedClientAsync();
        var instrumentId = await SeedInstrumentWithoutPositionAsync(cleanPrice: 1000m, couponValueRub: 40m);

        var response = await client.PostAsJsonAsync("/api/analytics/basket", new
        {
            amountRub = 5_000m,
            lines = new[] { new { instrumentId, weightFraction = 1.0m } },
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("whatIf").GetProperty("before").GetProperty("totalValueRub").GetDecimal().Should().Be(0m);
        body.GetProperty("whatIf").GetProperty("after").GetProperty("totalValueRub").GetDecimal().Should().BeGreaterThan(0m);
    }

    [Fact]
    public async Task PostBasket_NonPositiveAmount_Returns422()
    {
        var (client, _, accountId) = await CreateAuthorizedClientAsync();
        var (instrumentId, _) = await SeedYieldingPositionAsync(accountId, cleanPrice: 1000m, couponValueRub: 30m);

        var response = await client.PostAsJsonAsync("/api/analytics/basket", new
        {
            amountRub = 0m,
            lines = new[] { new { instrumentId, weightFraction = 1.0m } },
        });

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task PostBasket_WeightAboveOne_Returns422()
    {
        var (client, _, accountId) = await CreateAuthorizedClientAsync();
        var (instrumentId, _) = await SeedYieldingPositionAsync(accountId, cleanPrice: 1000m, couponValueRub: 30m);

        var response = await client.PostAsJsonAsync("/api/analytics/basket", new
        {
            amountRub = 10_000m,
            lines = new[] { new { instrumentId, weightFraction = 1.5m } },
        });

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task PostBasket_SumOfWeightsAboveOne_Returns422()
    {
        var (client, _, accountId) = await CreateAuthorizedClientAsync();
        var (instrumentId1, _) = await SeedYieldingPositionAsync(accountId, cleanPrice: 1000m, couponValueRub: 30m);
        var (instrumentId2, _) = await SeedYieldingPositionAsync(accountId, cleanPrice: 950m, couponValueRub: 60m);

        var response = await client.PostAsJsonAsync("/api/analytics/basket", new
        {
            amountRub = 10_000m,
            lines = new[]
            {
                new { instrumentId = instrumentId1, weightFraction = 0.7m },
                new { instrumentId = instrumentId2, weightFraction = 0.7m },
            },
        });

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task PostBasket_NonExistentInstrumentId_Returns422()
    {
        var (client, _, _) = await CreateAuthorizedClientAsync();

        var response = await client.PostAsJsonAsync("/api/analytics/basket", new
        {
            amountRub = 10_000m,
            lines = new[] { new { instrumentId = 9_999_999UL, weightFraction = 1.0m } },
        });

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task PostBasket_EmptyLines_Returns422()
    {
        var (client, _, _) = await CreateAuthorizedClientAsync();

        var response = await client.PostAsJsonAsync("/api/analytics/basket", new
        {
            amountRub = 10_000m,
            lines = Array.Empty<object>(),
        });

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task PostBasket_ConcentrationBreach_ProducesWarningInWhatIf()
    {
        var (client, _, accountId) = await CreateAuthorizedClientAsync();
        // Один эмитент уже занимает почти весь портфель — докупка того же эмитента должна дать warning.
        var (instrumentId, _) = await SeedYieldingPositionAsync(accountId, cleanPrice: 1000m, couponValueRub: 30m, quantity: 100m);

        var response = await client.PostAsJsonAsync("/api/analytics/basket", new
        {
            amountRub = 5_000m,
            lines = new[] { new { instrumentId, weightFraction = 1.0m } },
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        var warnings = body.GetProperty("whatIf").GetProperty("warnings").EnumerateArray().ToList();
        warnings.Should().Contain(w => w.GetProperty("kind").GetString() == "ConcentrationLimitBreached");
    }

    [Fact]
    public async Task PostBasket_Requires401WithoutToken()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/analytics/basket", new
        {
            amountRub = 10_000m,
            lines = new[] { new { instrumentId = 1UL, weightFraction = 1.0m } },
        });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}

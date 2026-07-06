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
/// Задача 23 — критерии приёмки: GET /api/analytics/replacement-matrix перебирает ВСЕ пары портфеля
/// (+ watchlist-таргеты) на сервере, ранжирует bestPairs по netBenefit, показывает rejectedPairs с
/// причинами, и применяет ставку комиссии из ICommissionRateProvider (задача 22), а не константу.
/// Сеть не используется — портфель засеян напрямую через репозитории (котировка + купон дают YTM,
/// без которого сравнение доходностей невозможно).
/// </summary>
[Collection("Integration")]
public class ReplacementMatrixEndpointTests
{
    private readonly TestWebApplicationFactory _factory;

    public ReplacementMatrixEndpointTests(TestWebApplicationFactory factory)
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

    /// <summary>Заводит позицию с реальной котировкой + купоном, дающими сходящийся YTM (без этого EffectiveYield=null и пара не считается вовсе).</summary>
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

    [Fact]
    public async Task GetReplacementMatrix_EmptyPortfolio_Returns200_WithEmptyMatrix()
    {
        var (client, _, _) = await CreateAuthorizedClientAsync();

        var response = await client.GetAsync("/api/analytics/replacement-matrix");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("bestPairs").GetArrayLength().Should().Be(0);
        body.GetProperty("rejectedPairs").GetArrayLength().Should().Be(0);
        body.GetProperty("totalConsideredPairs").GetInt32().Should().Be(0);
        body.GetProperty("disclaimer").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task GetReplacementMatrix_WeakAndStrongPosition_RanksBestPairByNetBenefit_WithFullBreakdown()
    {
        var (client, _, accountId) = await CreateAuthorizedClientAsync();
        // Низкая цена (100% номинала) и маленький купон => низкий YTM.
        var (weakInstrumentId, weakPositionId) = await SeedYieldingPositionAsync(accountId, cleanPrice: 1000m, couponValueRub: 30m);
        // Более высокая купонная ставка => выше YTM при той же цене — выгодная замена.
        var (strongInstrumentId, strongPositionId) = await SeedYieldingPositionAsync(accountId, cleanPrice: 950m, couponValueRub: 60m);

        var response = await client.GetAsync("/api/analytics/replacement-matrix");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var bestPairs = body.GetProperty("bestPairs");
        bestPairs.GetArrayLength().Should().BeGreaterThan(0, "слабая позиция должна дать хотя бы одну выгодную замену в сильную");

        var pair = bestPairs[0];
        pair.GetProperty("holdPositionId").GetUInt64().Should().Be(weakPositionId);
        pair.GetProperty("targetPositionId").GetUInt64().Should().Be(strongPositionId);
        pair.GetProperty("netBenefitRub").GetDecimal().Should().BeGreaterThan(0m);
        pair.GetProperty("spreadFraction").GetDecimal().Should().BeGreaterThan(0m);
        pair.GetProperty("capitalRub").GetDecimal().Should().BeGreaterThan(0m);
        pair.GetProperty("horizonYears").GetDecimal().Should().BeGreaterThan(0m);
        pair.GetProperty("grossGainRub").ValueKind.Should().NotBe(JsonValueKind.Undefined);
        pair.GetProperty("sellCommissionRub").GetDecimal().Should().BeGreaterThan(0m);
        pair.GetProperty("buyCommissionRub").GetDecimal().Should().BeGreaterThan(0m);
        pair.GetProperty("annualizedBenefitFraction").ValueKind.Should().NotBe(JsonValueKind.Null);
        pair.GetProperty("commissionRateUsed").GetDecimal().Should().Be(0.003m, "нет ни override, ни журнала сделок — резолвер отдаёт дефолт");
        pair.GetProperty("commissionRateSource").GetString().Should().Be("Default");
        pair.GetProperty("isWatchlistTarget").GetBoolean().Should().BeFalse();

        var totalConsidered = body.GetProperty("totalConsideredPairs").GetInt32();
        totalConsidered.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task GetReplacementMatrix_UsesCommissionRateFromResolver_NotHardcodedDefault()
    {
        var (client, _, accountId) = await CreateAuthorizedClientAsync();
        await SeedYieldingPositionAsync(accountId, cleanPrice: 1000m, couponValueRub: 30m);
        await SeedYieldingPositionAsync(accountId, cleanPrice: 950m, couponValueRub: 60m);

        // ~0.046% ставка из журнала — тот же сценарий, что CommissionRateEndpointsTests.SeedFeeJournalAsync.
        var operationRepo = new OperationRepository(_factory.Database.ConnectionString);
        await operationRepo.UpsertManyByExternalIdAsync(new[]
        {
            new Operation { AccountId = accountId, Type = OperationType.Buy, Date = DateTime.UtcNow.AddDays(-10), AmountRub = -100_000m, ExternalId = $"buy-{Guid.NewGuid()}" },
            new Operation { AccountId = accountId, Type = OperationType.Fee, Date = DateTime.UtcNow.AddDays(-10), AmountRub = -46m, ExternalId = $"fee1-{Guid.NewGuid()}" },
            new Operation { AccountId = accountId, Type = OperationType.Sell, Date = DateTime.UtcNow.AddDays(-5), AmountRub = 50_000m, ExternalId = $"sell-{Guid.NewGuid()}" },
            new Operation { AccountId = accountId, Type = OperationType.Fee, Date = DateTime.UtcNow.AddDays(-5), AmountRub = -23m, ExternalId = $"fee2-{Guid.NewGuid()}" },
        });

        var response = await client.GetAsync("/api/analytics/replacement-matrix");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var bestPairs = body.GetProperty("bestPairs");
        bestPairs.GetArrayLength().Should().BeGreaterThan(0);

        var pair = bestPairs[0];
        pair.GetProperty("commissionRateSource").GetString().Should().Be("EstimatedFromTrades");
        pair.GetProperty("commissionRateUsed").GetDecimal().Should().BeApproximately(69m / 150_000m, 0.0000001m);
        pair.GetProperty("commissionRateUsed").GetDecimal().Should().NotBe(0.003m, "дефолт 0.3% должен быть заменён оценкой из журнала (задача 22)");
    }

    [Fact]
    public async Task GetReplacementMatrix_SamePairInBothDirections_WeakerDirectionIsRejectedNotProfitable()
    {
        var (client, _, accountId) = await CreateAuthorizedClientAsync();
        await SeedYieldingPositionAsync(accountId, cleanPrice: 1000m, couponValueRub: 30m);
        await SeedYieldingPositionAsync(accountId, cleanPrice: 950m, couponValueRub: 60m);

        var response = await client.GetAsync("/api/analytics/replacement-matrix");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        // Обратное направление (сильная -> слабая) должно быть либо не рассмотрено вовсе (targetYield <= holdYield,
        // самый вероятный случай), либо явно отвергнуто — но точно не в bestPairs.
        var bestPairs = body.GetProperty("bestPairs").EnumerateArray().ToList();
        var rejectedPairs = body.GetProperty("rejectedPairs").EnumerateArray().ToList();

        foreach (var p in bestPairs.Concat(rejectedPairs))
        {
            if (p.GetProperty("targetPositionId").ValueKind == JsonValueKind.Number)
            {
                // Никакая пара с netBenefit <= 0 не должна оказаться в bestPairs.
                if (bestPairs.Contains(p))
                {
                    p.GetProperty("netBenefitRub").GetDecimal().Should().BeGreaterThan(0m);
                }
            }
        }
    }

    [Fact]
    public async Task GetReplacementMatrix_WatchlistBond_AppearsAsTarget_FlaggedIsWatchlistTarget()
    {
        var (client, userId, accountId) = await CreateAuthorizedClientAsync();
        var (_, weakPositionId) = await SeedYieldingPositionAsync(accountId, cleanPrice: 1000m, couponValueRub: 20m);

        // Watchlist-бумага БЕЗ позиции — заводим инструмент напрямую (без MOEX-сети, тот же путь,
        // которым GetScatter/GetAllocation резолвят watchlist через GetByIsinAsync).
        var instrumentRepo = new InstrumentRepository(_factory.Database.ConnectionString);
        var watchlistIsin = $"RU{Guid.NewGuid():N}".Substring(0, 12);
        var watchlistInstrumentId = await instrumentRepo.UpsertAsync(new Instrument
        {
            Isin = watchlistIsin,
            Issuer = "Watchlist Эмитент",
            FaceValue = 1000m,
            Currency = "RUB",
            CouponType = CouponType.Fixed,
            MaturityDate = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(3)),
        });

        var couponRepo = new CouponScheduleRepository(_factory.Database.ConnectionString);
        await couponRepo.ReplaceForInstrumentAsync(watchlistInstrumentId, new[]
        {
            new CouponSchedule { InstrumentId = watchlistInstrumentId, CouponDate = DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(-1)), ValueRub = 80m, PeriodDays = 182, IsKnown = true },
            new CouponSchedule { InstrumentId = watchlistInstrumentId, CouponDate = DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(5)), ValueRub = 80m, PeriodDays = 182, IsKnown = true },
        });

        var quoteRepo = new MarketQuoteRepository(_factory.Database.ConnectionString);
        await quoteRepo.UpsertAsync(new MarketQuote
        {
            InstrumentId = watchlistInstrumentId,
            AsOf = DateOnly.FromDateTime(DateTime.UtcNow),
            CleanPrice = 900m,
            DirtyPrice = 900m,
            Accrued = 0m,
            Source = MarketQuoteSource.Moex,
        });

        var watchlistRepo = new WatchlistItemRepository(_factory.Database.ConnectionString);
        await watchlistRepo.CreateAsync(new WatchlistItem { UserId = userId, Isin = watchlistIsin, AddedAtUtc = DateTime.UtcNow });

        var response = await client.GetAsync("/api/analytics/replacement-matrix");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var bestPairs = body.GetProperty("bestPairs").EnumerateArray().ToList();

        var watchlistPair = bestPairs.FirstOrDefault(p =>
            p.GetProperty("targetInstrumentId").GetUInt64() == watchlistInstrumentId);
        watchlistPair.ValueKind.Should().NotBe(JsonValueKind.Undefined, "матрица должна содержать пару с watchlist-таргетом (plan/23 п.1)");
        watchlistPair.GetProperty("isWatchlistTarget").GetBoolean().Should().BeTrue();
        watchlistPair.GetProperty("holdPositionId").GetUInt64().Should().Be(weakPositionId);
    }
}

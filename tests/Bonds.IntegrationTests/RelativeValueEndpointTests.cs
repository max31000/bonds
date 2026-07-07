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
/// Задача 30 часть C — критерии приёмки GET /api/analytics/relative-value: засеянная вселенная
/// (сектор × дюрационная корзина, ≥5 бумаг) + позиция портфеля с заниженным спредом → verdict Rich
/// + кандидаты из ЕЁ ЖЕ корзины; молодая история (0 дней) → basedOnDays честный. Кэш
/// RelativeValueSnapshotBuilder отключён для тестов (BondUniverse:RelativeValueCacheDuration=0,
/// см. TestWebApplicationFactory) — иначе singleton-кэш пережил бы сидирование между тестами
/// одной коллекции. Сеть не используется — портфель/банк засеяны напрямую через репозитории.
/// </summary>
[Collection("Integration")]
public class RelativeValueEndpointTests
{
    private readonly TestWebApplicationFactory _factory;

    public RelativeValueEndpointTests(TestWebApplicationFactory factory)
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

    private async Task SeedYieldCurveAsync()
    {
        var curveRepo = new YieldCurveRepository(_factory.Database.ConnectionString);
        await curveRepo.UpsertAsync(new YieldCurveSnapshot
        {
            AsOf = DateOnly.FromDateTime(DateTime.UtcNow),
            B1 = 7.5m, B2 = -2.1m, B3 = 1.3m, T1 = 1.7m,
            G1 = 7.1m, G2 = 7.3m, G3 = 7.5m, G4 = 7.6m, G5 = 7.7m,
            G6 = 7.8m, G7 = 7.9m, G8 = 8.0m, G9 = 8.1m,
        });
    }

    /// <summary>Заводит позицию портфеля с реальной котировкой + купоном (сходящийся YTM/дюрация/G-спред
    /// нужны — иначе GSpread=null и позиция не попадёт в relative-value вовсе, тот же принцип, что
    /// ReplacementMatrixEndpointTests.SeedYieldingPositionAsync). Sector — как в bond_universe.sector
    /// (простая классификация), чтобб позиция и рыночные кандидаты легли в ОДНУ корзину.</summary>
    private async Task<(ulong InstrumentId, ulong PositionId)> SeedYieldingPositionAsync(
        ulong accountId, string sector, decimal cleanPrice, decimal couponValueRub, int maturityYears = 2, decimal quantity = 10m)
    {
        var instrumentRepo = new InstrumentRepository(_factory.Database.ConnectionString);
        var isin = $"RU{Guid.NewGuid():N}".Substring(0, 12);
        var instrumentId = await instrumentRepo.UpsertAsync(new Instrument
        {
            Isin = isin,
            Issuer = "Эмитент " + isin,
            Sector = sector,
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

    private static BondUniverseEntry HealthyEntry(string secid, string sector, decimal yieldFraction, decimal durationYears, decimal turnover = 1_000_000m) => new()
    {
        Secid = secid,
        Isin = $"ISIN{secid}",
        ShortName = $"BOND {secid}",
        SecName = $"Full name {secid}",
        FaceValue = 1000m,
        Sector = sector,
        YieldFraction = yieldFraction,
        DurationYears = durationYears,
        PricePercent = 99.5m,
        TurnoverRub = turnover,
        BidPercent = 99m,
        OfferPercent = 100m,
        NumTrades = 10,
        ListLevel = 1,
        GspreadApproxFraction = yieldFraction - 0.10m, // фиктивный спред для теста — не пересчитан по реальной кривой
        MaturityDate = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(3)),
        UpdatedAt = DateTime.UtcNow,
    };

    [Fact]
    public async Task GetRelativeValue_NoAccount_Returns200WithEmptyPositions()
    {
        var client = _factory.CreateClient();
        var userRepo = new UserRepository(_factory.Database.ConnectionString);
        var userId = await userRepo.CreateAsync(new User { TelegramId = Random.Shared.NextInt64(1, long.MaxValue) });
        var token = JwtTestHelper.GenerateToken(userId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync("/api/analytics/relative-value");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("positions").GetArrayLength().Should().Be(0);
        body.GetProperty("disclaimer").GetString().Should().Contain("кредитного качества");
    }

    [Fact]
    public async Task GetRelativeValue_NoToken_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/analytics/relative-value");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetRelativeValue_PositionWithSuppressedSpread_VerdictRich_WithCheapCandidatesFromSameBasket()
    {
        await SeedYieldCurveAsync();
        var (client, _, accountId) = await CreateAuthorizedClientAsync();
        var marker = Guid.NewGuid().ToString("N")[..6];
        var sector = $"Sector{marker}";

        // Позиция портфеля: короткая дюрация (маленький купон/близкое погашение) -> низкий G-спред
        // относительно рынка того же сектора (ниже заданных ниже рыночных бумаг с явно завышенным
        // приближённым спредом) -> должна получить verdict Rich.
        var (_, positionId) = await SeedYieldingPositionAsync(accountId, sector, cleanPrice: 1000m, couponValueRub: 20m, maturityYears: 2);

        // Рыночные кандидаты той же корзины (сектор × дюрация ~1-3 года, т.к. maturityYears=2)
        // с заведомо высоким приближённым G-спредом — должны стать Cheap-кандидатами для Rich-позиции.
        var universeRepo = new BondUniverseRepository(_factory.Database.ConnectionString);
        await universeRepo.UpsertSnapshotBatchAsync(new[]
        {
            HealthyEntry($"A{marker}", sector, 0.30m, 2m),
            HealthyEntry($"B{marker}", sector, 0.32m, 2.1m),
            HealthyEntry($"C{marker}", sector, 0.34m, 1.9m),
            HealthyEntry($"D{marker}", sector, 0.36m, 2.2m),
            HealthyEntry($"E{marker}", sector, 0.38m, 1.8m),
        });

        var response = await client.GetAsync("/api/analytics/relative-value");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var positions = body.GetProperty("positions").EnumerateArray().ToList();

        var position = positions.SingleOrDefault(p => p.GetProperty("positionId").GetUInt64() == positionId);
        position.ValueKind.Should().NotBe(JsonValueKind.Undefined, "позиция с посчитанным G-спредом должна попасть в ответ");

        position.GetProperty("verdict").GetString().Should().Be("Rich", "спред позиции намного ниже медианы её корзины (высокоспредовые рыночные бумаги)");
        position.GetProperty("basket").GetProperty("sector").GetString().Should().Be(sector);
        position.GetProperty("basedOnDays").GetInt32().Should().Be(0, "истории bond_universe_history в этом тесте нет — молодой банк");

        var candidates = position.GetProperty("cheapCandidates").EnumerateArray().ToList();
        candidates.Should().NotBeEmpty("Rich-позиция должна показать дешёвых кандидатов из своей корзины");
        candidates.Should().HaveCountLessOrEqualTo(3);
        foreach (var candidate in candidates)
        {
            candidate.GetProperty("secid").GetString().Should().NotBeNullOrEmpty();
            // Топ-3 по убыв. отклонения из 5-членной корзины — деviation >= 0 (третий кандидат может
            // совпасть с медианой самой корзины, deviation=0, если ранг ровно на медиане).
            candidate.GetProperty("deviationFraction").GetDecimal().Should().BeGreaterThanOrEqualTo(0m, "кандидаты — топ по дешевизне, не дороже медианы своей корзины");
            candidate.GetProperty("liquidityScore").ValueKind.Should().Be(JsonValueKind.String);
        }
        candidates[0].GetProperty("deviationFraction").GetDecimal().Should().BeGreaterThan(0m, "самый дешёвый кандидат обязан быть строго дешевле медианы");
    }

    [Fact]
    public async Task GetRelativeValue_PositionAlsoPresentInUniverse_NotItsOwnCheapCandidate()
    {
        // Ревью T-30 (MAJOR): банк — вся вселенная MOEX, позиция присутствует в нём под своим ISIN.
        // Её approx-спред из банка отличается от точного спреда движка, поэтому без self-exclusion
        // Rich-позиция могла бы возглавить список «дешёвых соседей» самой себе.
        await SeedYieldCurveAsync();
        var (client, _, accountId) = await CreateAuthorizedClientAsync();
        var marker = Guid.NewGuid().ToString("N")[..6];
        var sector = $"Sector{marker}";

        var (instrumentId, positionId) = await SeedYieldingPositionAsync(accountId, sector, cleanPrice: 1000m, couponValueRub: 20m, maturityYears: 2);
        var instrumentRepo = new InstrumentRepository(_factory.Database.ConnectionString);
        var positionIsin = (await instrumentRepo.GetByIdAsync(instrumentId))!.Isin;

        // Та же бумага в банке: тот же ISIN, САМЫЙ высокий approx-спред корзины — до фикса
        // она гарантированно попала бы первым «дешёвым» кандидатом.
        var selfEntry = HealthyEntry($"SELF{marker}", sector, 0.50m, 2m);
        selfEntry.Isin = positionIsin;

        var universeRepo = new BondUniverseRepository(_factory.Database.ConnectionString);
        await universeRepo.UpsertSnapshotBatchAsync(new[]
        {
            selfEntry,
            HealthyEntry($"A{marker}", sector, 0.30m, 2m),
            HealthyEntry($"B{marker}", sector, 0.32m, 2.1m),
            HealthyEntry($"C{marker}", sector, 0.34m, 1.9m),
            HealthyEntry($"D{marker}", sector, 0.36m, 2.2m),
            HealthyEntry($"E{marker}", sector, 0.38m, 1.8m),
        });

        var response = await client.GetAsync("/api/analytics/relative-value");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var position = body.GetProperty("positions").EnumerateArray()
            .Single(p => p.GetProperty("positionId").GetUInt64() == positionId);
        position.GetProperty("verdict").GetString().Should().Be("Rich");

        var candidateSecids = position.GetProperty("cheapCandidates").EnumerateArray()
            .Select(c => c.GetProperty("secid").GetString())
            .ToList();
        candidateSecids.Should().NotBeEmpty("дешёвые соседи по корзине должны найтись среди ЧУЖИХ бумаг");
        candidateSecids.Should().NotContain($"SELF{marker}", "Rich-позиция не должна рекомендовать саму себя как дешёвого соседа");
    }

    [Fact]
    public async Task GetRelativeValue_FloaterPosition_ExcludedFromResults()
    {
        await SeedYieldCurveAsync();
        var (client, _, accountId) = await CreateAuthorizedClientAsync();
        var marker = Guid.NewGuid().ToString("N")[..6];

        var instrumentRepo = new InstrumentRepository(_factory.Database.ConnectionString);
        var isin = $"RU{Guid.NewGuid():N}".Substring(0, 12);
        var instrumentId = await instrumentRepo.UpsertAsync(new Instrument
        {
            Isin = isin,
            Issuer = $"Флоатер {marker}",
            Sector = $"Sector{marker}",
            FaceValue = 1000m,
            Currency = "RUB",
            CouponType = CouponType.Floating,
            MaturityDate = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(2)),
        });

        var quoteRepo = new MarketQuoteRepository(_factory.Database.ConnectionString);
        await quoteRepo.UpsertAsync(new MarketQuote
        {
            InstrumentId = instrumentId,
            AsOf = DateOnly.FromDateTime(DateTime.UtcNow),
            CleanPrice = 1000m,
            DirtyPrice = 1000m,
            Accrued = 0m,
            Source = MarketQuoteSource.Moex,
        });

        var positionRepo = new PositionRepository(_factory.Database.ConnectionString);
        var positionId = await positionRepo.UpsertAsync(new Position
        {
            AccountId = accountId,
            InstrumentId = instrumentId,
            Quantity = 10,
            AvgPurchasePrice = 1000m,
        });

        var response = await client.GetAsync("/api/analytics/relative-value");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var positions = body.GetProperty("positions").EnumerateArray().ToList();

        positions.Should().NotContain(p => p.GetProperty("positionId").GetUInt64() == positionId, "флоатер вне сравнения (не floater/indexed/dataIncomplete), как и в comparison/replacement-matrix");
    }
}

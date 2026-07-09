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
/// Задача 33 часть C.4 — критерии приёмки GET /api/analytics/replacement-candidates: mode=market
/// отдаёт фикс-купонную гигиенически-чистую вселенную банка отсортированной по доходности убыв.
/// (без флоатеров, без самой позиции), mode=rv отдаёт дешёвых соседей корзины позиции (тот же путь,
/// что GET /api/analytics/relative-value), оба режима несут риск-сигналы на каждом кандидате.
/// Кэш RelativeValueSnapshotBuilder отключён для тестов (BondUniverse:RelativeValueCacheDuration=0,
/// см. TestWebApplicationFactory). Сеть не используется — портфель/банк засеяны напрямую через репозитории.
/// </summary>
[Collection("Integration")]
public class ReplacementCandidatesEndpointTests
{
    private readonly TestWebApplicationFactory _factory;

    public ReplacementCandidatesEndpointTests(TestWebApplicationFactory factory)
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
    /// нужны для mode=rv — иначе GSpread=null и mode=rv честно вернёт пустой список).</summary>
    private async Task<(ulong InstrumentId, ulong PositionId, string Isin)> SeedYieldingPositionAsync(
        ulong accountId, string sector, decimal cleanPrice, decimal couponValueRub, int maturityYears = 2, decimal quantity = 10m,
        CouponType couponType = CouponType.Fixed)
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
            CouponType = couponType,
            MaturityDate = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(maturityYears)),
        });

        if (couponType == CouponType.Fixed)
        {
            var couponRepo = new CouponScheduleRepository(_factory.Database.ConnectionString);
            await couponRepo.ReplaceForInstrumentAsync(instrumentId, new[]
            {
                new CouponSchedule { InstrumentId = instrumentId, CouponDate = DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(-1)), ValueRub = couponValueRub, PeriodDays = 182, IsKnown = true },
                new CouponSchedule { InstrumentId = instrumentId, CouponDate = DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(5)), ValueRub = couponValueRub, PeriodDays = 182, IsKnown = true },
            });
        }

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

        return (instrumentId, positionId, isin);
    }

    /// <summary>Дефолты (оборот 1 млн, спред bid/offer ~0.5%) дают <c>LiquidityScore.Medium</c>
    /// (см. <c>LiquidityScoreCalculator</c>: High требует оборот &gt; 5 млн И спред &lt; 0.3%) —
    /// для сценариев, которым нужен именно High/Low, передавайте turnover/bidPercent/offerPercent явно.</summary>
    private static BondUniverseEntry HealthyEntry(
        string secid, string sector, decimal yieldFraction, decimal durationYears,
        decimal turnover = 1_000_000m, bool? isFloater = null, int listLevel = 1,
        decimal bidPercent = 99m, decimal offerPercent = 100m) => new()
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
        BidPercent = bidPercent,
        OfferPercent = offerPercent,
        NumTrades = 10,
        ListLevel = listLevel,
        GspreadApproxFraction = yieldFraction - 0.10m, // фиктивный спред для теста — не пересчитан по реальной кривой
        IsFloater = isFloater,
        MaturityDate = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(3)),
        UpdatedAt = DateTime.UtcNow,
    };

    // ─── Auth / validation ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetReplacementCandidates_NoToken_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/analytics/replacement-candidates?positionId=1&mode=market");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetReplacementCandidates_InvalidMode_Returns422()
    {
        var (client, _, _) = await CreateAuthorizedClientAsync();

        var response = await client.GetAsync("/api/analytics/replacement-candidates?positionId=1&mode=bogus");

        response.StatusCode.Should().Be((HttpStatusCode)422);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("type").GetString().Should().Be("ValidationException");
    }

    [Fact]
    public async Task GetReplacementCandidates_UnknownPositionId_Returns404()
    {
        var (client, _, _) = await CreateAuthorizedClientAsync();

        var response = await client.GetAsync("/api/analytics/replacement-candidates?positionId=999999&mode=market");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetReplacementCandidates_NoAccount_Returns404()
    {
        var client = _factory.CreateClient();
        var userRepo = new UserRepository(_factory.Database.ConnectionString);
        var userId = await userRepo.CreateAsync(new User { TelegramId = Random.Shared.NextInt64(1, long.MaxValue) });
        var token = JwtTestHelper.GenerateToken(userId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync("/api/analytics/replacement-candidates?positionId=1&mode=market");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ─── mode=market ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetReplacementCandidates_Market_SortsByYieldDescending_ExcludesFloatersAndSelfIsin_WithRiskSignals()
    {
        var (client, _, accountId) = await CreateAuthorizedClientAsync();
        var marker = Guid.NewGuid().ToString("N")[..6];
        var sector = $"Sector{marker}";

        var (_, positionId, positionIsin) = await SeedYieldingPositionAsync(accountId, sector, cleanPrice: 1000m, couponValueRub: 20m);

        // mode=market ранжирует ВСЮ вселенную банка (не только сектор позиции, план часть B.2) —
        // тест делит один общий MySQL-контейнер с остальными тестами коллекции "Integration"
        // (см. doc-comment класса), поэтому доходности берутся у самой границы гигиенического
        // порога (MaxSaneYieldFraction=0.45, см. UniverseHygieneFilter) — достаточно высоко, чтобы
        // не потеряться среди обычных (0.10-0.40) доходностей других тестов при сортировке по убыв.
        var universeRepo = new BondUniverseRepository(_factory.Database.ConnectionString);
        var selfEntry = HealthyEntry($"SF{marker}", sector, 0.45m, 2m); // самый высокий yield в наборе — должен быть исключён по ISIN
        selfEntry.Isin = positionIsin;

        // ListLevel=3 сюда намеренно НЕ заводим — UniverseHygieneFilter.HideListLevelThree=true
        // прячет такие бумаги из mode=market целиком (задача 26 часть C.4), матрица
        // ликвидность×листинг уже покрыта юнит-тестами CandidateRiskSignalServiceTests.
        await universeRepo.UpsertSnapshotBatchAsync(new[]
        {
            selfEntry,
            HealthyEntry($"HI{marker}", sector, 0.44m, 2m, listLevel: 1, turnover: 10_000_000m, bidPercent: 99.9m, offerPercent: 100m),
            HealthyEntry($"MD{marker}", sector, 0.43m, 2m, listLevel: 2),
            HealthyEntry($"LW{marker}", sector, 0.42m, 2m, listLevel: 2),
            HealthyEntry($"FL{marker}", sector, 0.441m, 2m, isFloater: true), // между HI и MD по yield, но флоатер
        });

        var response = await client.GetAsync($"/api/analytics/replacement-candidates?positionId={positionId}&mode=market&limit=50");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("mode").GetString().Should().Be("market");
        body.GetProperty("positionIsin").GetString().Should().Be(positionIsin);
        body.GetProperty("disclaimer").GetString().Should().Contain("не рейтинг");

        var candidates = body.GetProperty("candidates").EnumerateArray().ToList();
        var secids = candidates.Select(c => c.GetProperty("secid").GetString()).ToList();

        secids.Should().NotContain($"SF{marker}", "сама позиция должна быть исключена по ISIN");
        secids.Should().NotContain($"FL{marker}", "флоатеры не должны предлагаться в mode=market");

        // Собственные кандидаты теста (без "чужих" из общего контейнера) — точный порядок по yield убыв.
        var ownSecids = secids.Where(s => s!.EndsWith(marker, StringComparison.Ordinal)).ToList();
        ownSecids.Should().Equal($"HI{marker}", $"MD{marker}", $"LW{marker}");

        foreach (var candidate in candidates)
        {
            var signals = candidate.GetProperty("riskSignals");
            signals.GetProperty("liquidity").ValueKind.Should().Be(JsonValueKind.String);
            signals.GetProperty("liquidityLabel").GetString().Should().NotBeNullOrEmpty();
            signals.GetProperty("spread").ValueKind.Should().Be(JsonValueKind.String);
        }

        var high = candidates.Single(c => c.GetProperty("secid").GetString() == $"HI{marker}");
        high.GetProperty("riskSignals").GetProperty("liquidity").GetString().Should().Be("Good", "оборот/спред HealthyEntry попадают в порог High, листинг 1 не понижает уровень");
        high.GetProperty("riskSignals").GetProperty("liquidityLabel").GetString().Should().Be("Высокая ликвидность, листинг 1");
    }

    [Fact]
    public async Task GetReplacementCandidates_Market_RespectsLimit()
    {
        var (client, _, accountId) = await CreateAuthorizedClientAsync();
        var marker = Guid.NewGuid().ToString("N")[..6];
        var sector = $"Sector{marker}";

        var (_, positionId, _) = await SeedYieldingPositionAsync(accountId, sector, cleanPrice: 1000m, couponValueRub: 20m);

        var universeRepo = new BondUniverseRepository(_factory.Database.ConnectionString);
        await universeRepo.UpsertSnapshotBatchAsync(new[]
        {
            HealthyEntry($"A{marker}", sector, 0.30m, 2m),
            HealthyEntry($"B{marker}", sector, 0.28m, 2m),
            HealthyEntry($"C{marker}", sector, 0.26m, 2m),
        });

        var response = await client.GetAsync($"/api/analytics/replacement-candidates?positionId={positionId}&mode=market&limit=2");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("candidates").EnumerateArray().Should().HaveCount(2);
    }

    // ─── mode=rv ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetReplacementCandidates_Rv_ReturnsCheapNeighborsFromPositionBasket_ExcludesFloaters()
    {
        await SeedYieldCurveAsync();
        var (client, _, accountId) = await CreateAuthorizedClientAsync();
        var marker = Guid.NewGuid().ToString("N")[..6];
        var sector = $"Sector{marker}";

        // Позиция с низким G-спредом (короткая дюрация/маленький купон) -> дешёвые соседи из
        // корзины будут с заведомо более высоким приближённым спредом, тот же принцип, что
        // RelativeValueEndpointTests.GetRelativeValue_PositionWithSuppressedSpread...
        var (_, positionId, _) = await SeedYieldingPositionAsync(accountId, sector, cleanPrice: 1000m, couponValueRub: 20m, maturityYears: 2);

        var universeRepo = new BondUniverseRepository(_factory.Database.ConnectionString);
        await universeRepo.UpsertSnapshotBatchAsync(new[]
        {
            HealthyEntry($"A{marker}", sector, 0.30m, 2m),
            HealthyEntry($"B{marker}", sector, 0.32m, 2.1m),
            HealthyEntry($"C{marker}", sector, 0.34m, 1.9m),
            HealthyEntry($"D{marker}", sector, 0.36m, 2.2m),
            HealthyEntry($"E{marker}", sector, 0.38m, 1.8m),
            HealthyEntry($"FL{marker}", sector, 0.60m, 2m, isFloater: true), // самый высокий спред корзины, но флоатер
        });

        var response = await client.GetAsync($"/api/analytics/replacement-candidates?positionId={positionId}&mode=rv&limit=3");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("mode").GetString().Should().Be("rv");
        var candidates = body.GetProperty("candidates").EnumerateArray().ToList();
        candidates.Should().NotBeEmpty("позиция с посчитанным G-спредом должна получить дешёвых соседей своей корзины");
        candidates.Should().HaveCountLessOrEqualTo(3);

        var secids = candidates.Select(c => c.GetProperty("secid").GetString()).ToList();
        secids.Should().NotContain($"FL{marker}", "флоатер не должен предлагаться как дешёвый сосед в mode=rv");

        foreach (var candidate in candidates)
        {
            var signals = candidate.GetProperty("riskSignals");
            signals.GetProperty("spread").ValueKind.Should().Be(JsonValueKind.String);
            candidate.GetProperty("gSpreadFraction").ValueKind.Should().Be(JsonValueKind.Number);
        }
    }

    [Fact]
    public async Task GetReplacementCandidates_Rv_PositionWithoutValidBasket_ReturnsEmptyListNot500()
    {
        // Флоатер-позиция: GSpread/ModifiedDuration не считаются движком (см. doc-comment
        // PortfolioHolding) -> mode=rv не может резолвить корзину -> честный пустой список, не 500
        // (план часть B.3: "вернуть пустой список + понятный признак (не 500)").
        var (client, _, accountId) = await CreateAuthorizedClientAsync();
        var marker = Guid.NewGuid().ToString("N")[..6];
        var sector = $"Sector{marker}";

        var (_, positionId, _) = await SeedYieldingPositionAsync(
            accountId, sector, cleanPrice: 1000m, couponValueRub: 0m, couponType: CouponType.Floating);

        var response = await client.GetAsync($"/api/analytics/replacement-candidates?positionId={positionId}&mode=rv");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("candidates").GetArrayLength().Should().Be(0);
    }
}

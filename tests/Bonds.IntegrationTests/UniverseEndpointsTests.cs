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
/// Задача 26 часть D — критерии приёмки GET /api/universe/GET /api/universe/status: поиск,
/// сортировка, фильтры (duration/yield/sector), includeHidden, флаги inPortfolio/inWatchlist
/// (join по ISIN), 401 без токена. Сеть не используется — данные сеются напрямую через
/// <see cref="BondUniverseRepository"/> (тот же паттерн, что MarketDataRepositoriesTests).
/// </summary>
[Collection("Integration")]
public class UniverseEndpointsTests
{
    private readonly TestWebApplicationFactory _factory;

    public UniverseEndpointsTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private async Task<HttpClient> CreateAuthorizedClientAsync()
    {
        var (client, _) = await CreateAuthorizedClientWithUserIdAsync();
        return client;
    }

    private async Task<(HttpClient Client, ulong UserId)> CreateAuthorizedClientWithUserIdAsync()
    {
        var userRepo = new UserRepository(_factory.Database.ConnectionString);
        var userId = await userRepo.CreateAsync(new User { TelegramId = Random.Shared.NextInt64(1, long.MaxValue) });

        var client = _factory.CreateClient();
        var token = JwtTestHelper.GenerateToken(userId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (client, userId);
    }

    private static BondUniverseEntry HealthyEntry(string secid, string isin, decimal yieldFraction, decimal durationYears, string sector = "Корпоративные", decimal turnover = 1_000_000m, bool? isFloater = null) => new()
    {
        Secid = secid,
        Isin = isin,
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
        IsFloater = isFloater,
        MaturityDate = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(3)),
        UpdatedAt = DateTime.UtcNow,
    };

    [Fact]
    public async Task GetUniverse_NoToken_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/universe");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetUniverse_SearchByShortName_ReturnsMatchingRow()
    {
        var repo = new BondUniverseRepository(_factory.Database.ConnectionString);
        var marker = Guid.NewGuid().ToString("N")[..6];
        var searchable = HealthyEntry($"SRCH{marker}", $"ISIN{marker}A", 0.10m, 2m);
        searchable.ShortName = $"UniqueName{marker}";
        var other = HealthyEntry($"OTHR{marker}", $"ISIN{marker}B", 0.11m, 3m);
        other.ShortName = "SomethingElse";
        await repo.UpsertSnapshotBatchAsync([searchable, other]);

        var client = await CreateAuthorizedClientAsync();
        var response = await client.GetAsync($"/api/universe?search=UniqueName{marker}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var rows = body.GetProperty("rows");
        rows.GetArrayLength().Should().Be(1);
        rows[0].GetProperty("secid").GetString().Should().Be($"SRCH{marker}");
    }

    [Fact]
    public async Task GetUniverse_ReturnsIsFloaterFlag_ForFloaterAndFixedRows_WithoutHidingEither()
    {
        // Задача 31 часть A/D.6 — банк отдаёт признак флоатера наружу (для скринера задачи 32), но
        // НЕ скрывает и не фильтрует флоатеры из выдачи (владелец выбрал "пометка + фильтр на
        // фронте", не hygiene-hide) — обе строки должны присутствовать в ответе.
        var repo = new BondUniverseRepository(_factory.Database.ConnectionString);
        var marker = Guid.NewGuid().ToString("N")[..6];
        await repo.UpsertSnapshotBatchAsync(
        [
            HealthyEntry($"FLOAT{marker}", $"ISF{marker}A", 0.10m, 2m, isFloater: true),
            HealthyEntry($"FIX{marker}", $"ISF{marker}B", 0.10m, 2m, isFloater: false),
            HealthyEntry($"UNK{marker}", $"ISF{marker}C", 0.10m, 2m, isFloater: null),
        ]);

        var client = await CreateAuthorizedClientAsync();
        var response = await client.GetAsync($"/api/universe?search={marker}&limit=10");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var rows = body.GetProperty("rows").EnumerateArray().ToList();
        rows.Should().HaveCount(3, "флоатер не должен быть скрыт/отфильтрован из GET /api/universe");

        var floaterRow = rows.Single(r => r.GetProperty("secid").GetString() == $"FLOAT{marker}");
        floaterRow.GetProperty("isFloater").GetBoolean().Should().BeTrue();

        var fixedRow = rows.Single(r => r.GetProperty("secid").GetString() == $"FIX{marker}");
        fixedRow.GetProperty("isFloater").GetBoolean().Should().BeFalse();

        var unknownRow = rows.Single(r => r.GetProperty("secid").GetString() == $"UNK{marker}");
        unknownRow.GetProperty("isFloater").ValueKind.Should().Be(JsonValueKind.Null, "BONDTYPE не пришёл от MOEX — null, не false");
    }

    [Fact]
    public async Task GetUniverse_FilterByDurationAndYield_ExcludesOutOfRange()
    {
        var repo = new BondUniverseRepository(_factory.Database.ConnectionString);
        var marker = Guid.NewGuid().ToString("N")[..6];
        await repo.UpsertSnapshotBatchAsync(
        [
            HealthyEntry($"IN{marker}", $"ISN{marker}A", 0.12m, 2m),
            HealthyEntry($"OUTDUR{marker}", $"ISN{marker}B", 0.12m, 10m),
            HealthyEntry($"OUTYIELD{marker}", $"ISN{marker}C", 0.30m, 2m),
        ]);

        var client = await CreateAuthorizedClientAsync();
        var response = await client.GetAsync($"/api/universe?search={marker}&minDurationYears=1&maxDurationYears=3&minYield=0.05&maxYield=0.15");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var rows = body.GetProperty("rows");
        rows.GetArrayLength().Should().Be(1);
        rows[0].GetProperty("secid").GetString().Should().Be($"IN{marker}");
    }

    [Fact]
    public async Task GetUniverse_FilterBySector_ReturnsOnlyMatchingSector()
    {
        var repo = new BondUniverseRepository(_factory.Database.ConnectionString);
        var marker = Guid.NewGuid().ToString("N")[..6];
        await repo.UpsertSnapshotBatchAsync(
        [
            HealthyEntry($"GOV{marker}", $"ISG{marker}A", 0.10m, 2m, sector: "Гособлигации"),
            HealthyEntry($"CORP{marker}", $"ISG{marker}B", 0.10m, 2m, sector: "Корпоративные"),
        ]);

        var client = await CreateAuthorizedClientAsync();
        var response = await client.GetAsync($"/api/universe?search={marker}&sector=Гособлигации");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var rows = body.GetProperty("rows");

        rows.GetArrayLength().Should().Be(1);
        rows[0].GetProperty("secid").GetString().Should().Be($"GOV{marker}");
    }

    [Fact]
    public async Task GetUniverse_SortByYieldDesc_OrdersDescending()
    {
        var repo = new BondUniverseRepository(_factory.Database.ConnectionString);
        var marker = Guid.NewGuid().ToString("N")[..6];
        await repo.UpsertSnapshotBatchAsync(
        [
            HealthyEntry($"LOW{marker}", $"ISY{marker}A", 0.05m, 2m),
            HealthyEntry($"HIGH{marker}", $"ISY{marker}B", 0.20m, 2m),
            HealthyEntry($"MID{marker}", $"ISY{marker}C", 0.12m, 2m),
        ]);

        var client = await CreateAuthorizedClientAsync();
        var response = await client.GetAsync($"/api/universe?search={marker}&sortBy=yield&sortDir=desc");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var rows = body.GetProperty("rows").EnumerateArray().ToList();

        rows.Should().HaveCount(3);
        rows[0].GetProperty("secid").GetString().Should().Be($"HIGH{marker}");
        rows[1].GetProperty("secid").GetString().Should().Be($"MID{marker}");
        rows[2].GetProperty("secid").GetString().Should().Be($"LOW{marker}");
    }

    [Fact]
    public async Task GetUniverse_HiddenLowTurnoverBond_ExcludedByDefault_IncludedWithIncludeHidden()
    {
        var repo = new BondUniverseRepository(_factory.Database.ConnectionString);
        var marker = Guid.NewGuid().ToString("N")[..6];
        await repo.UpsertSnapshotBatchAsync(
        [
            HealthyEntry($"VISIBLE{marker}", $"ISH{marker}A", 0.10m, 2m),
            HealthyEntry($"HIDDEN{marker}", $"ISH{marker}B", 0.10m, 2m, turnover: 1000m), // < 100k порог
        ]);

        var client = await CreateAuthorizedClientAsync();

        var defaultResponse = await client.GetAsync($"/api/universe?search={marker}");
        var defaultBody = await defaultResponse.Content.ReadFromJsonAsync<JsonElement>();
        var defaultRows = defaultBody.GetProperty("rows").EnumerateArray().ToList();
        defaultRows.Should().ContainSingle(r => r.GetProperty("secid").GetString() == $"VISIBLE{marker}");
        defaultRows.Should().NotContain(r => r.GetProperty("secid").GetString() == $"HIDDEN{marker}");
        defaultBody.GetProperty("hiddenCount").GetInt32().Should().BeGreaterThanOrEqualTo(1);

        var includeHiddenResponse = await client.GetAsync($"/api/universe?search={marker}&includeHidden=true");
        var includeHiddenBody = await includeHiddenResponse.Content.ReadFromJsonAsync<JsonElement>();
        var includeHiddenRows = includeHiddenBody.GetProperty("rows").EnumerateArray().ToList();
        includeHiddenRows.Should().HaveCount(2);

        var hiddenRow = includeHiddenRows.Single(r => r.GetProperty("secid").GetString() == $"HIDDEN{marker}");
        hiddenRow.GetProperty("isHidden").GetBoolean().Should().BeTrue();
        hiddenRow.GetProperty("hiddenReason").GetString().Should().Be("LowTurnover");
    }

    [Fact]
    public async Task GetUniverse_InWatchlist_FlagIsTrueForWatchlistedIsin()
    {
        var repo = new BondUniverseRepository(_factory.Database.ConnectionString);
        var marker = Guid.NewGuid().ToString("N")[..6];
        var isin = $"ISW{marker}A";
        await repo.UpsertSnapshotBatchAsync([HealthyEntry($"WL{marker}", isin, 0.10m, 2m)]);

        var (client, userId) = await CreateAuthorizedClientWithUserIdAsync();
        var watchlistRepo = new WatchlistItemRepository(_factory.Database.ConnectionString);
        await watchlistRepo.CreateAsync(new WatchlistItem { UserId = userId, Isin = isin });

        var response = await client.GetAsync($"/api/universe?search={marker}");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var row = body.GetProperty("rows").EnumerateArray().Single();

        row.GetProperty("inWatchlist").GetBoolean().Should().BeTrue();
        row.GetProperty("inPortfolio").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task GetUniverse_InPortfolio_FlagIsTrueForPrimaryAccountPosition()
    {
        var repo = new BondUniverseRepository(_factory.Database.ConnectionString);
        var marker = Guid.NewGuid().ToString("N")[..6];
        var isin = $"ISP{marker}A";
        await repo.UpsertSnapshotBatchAsync([HealthyEntry($"PF{marker}", isin, 0.10m, 2m)]);

        var (client, userId) = await CreateAuthorizedClientWithUserIdAsync();

        // GetPrimaryAccountIdAsync резолвит САМЫЙ старый Account во всей таблице (single-user
        // продукт, plan/00 §2) — не привязан к конкретному пользователю теста. Чтобы тест был
        // детерминирован независимо от порядка выполнения остальных интеграционных тестов (общий
        // контейнер БД на коллекцию), явно узнаём текущий primary account и заводим позицию именно
        // на него, а не полагаемся, что это будет только что созданный аккаунт.
        var accountRepo = new AccountRepository(_factory.Database.ConnectionString);
        var primaryAccountId = await accountRepo.GetPrimaryAccountIdAsync();
        if (primaryAccountId is null)
        {
            primaryAccountId = await accountRepo.CreateAsync(new Account { UserId = userId, Name = "Primary" });
        }

        var instrumentRepo = new InstrumentRepository(_factory.Database.ConnectionString);
        var instrumentId = await instrumentRepo.UpsertAsync(new Instrument
        {
            Isin = isin,
            FaceValue = 1000m,
            Currency = "RUB",
            CouponType = CouponType.Fixed,
            MaturityDate = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(3)),
        });

        var positionRepo = new PositionRepository(_factory.Database.ConnectionString);
        await positionRepo.UpsertAsync(new Position
        {
            AccountId = primaryAccountId.Value,
            InstrumentId = instrumentId,
            Quantity = 10,
            AvgPurchasePrice = 995m,
        });

        var response = await client.GetAsync($"/api/universe?search={marker}");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var row = body.GetProperty("rows").EnumerateArray().Single();

        row.GetProperty("inPortfolio").GetBoolean().Should().BeTrue();
    }

    // ─── reliability (задача 38 часть B.2) ──────────────────────────────────────────────────────

    /// <summary>Три записи по одной на уровень светофора — тот же приём, что
    /// ReplacementCandidatesEndpointTests.ReliabilityProbeEntries (см. её doc-comment для того, почему
    /// None-ликвидность здесь не используется: гигиенический фильтр прячет null-оборот). GspreadApproxFraction
    /// не задаётся (остаётся null по умолчанию record-инициализатора) — спред-сигнал автоматически Neutral.</summary>
    private static BondUniverseEntry[] ReliabilityProbeEntries(string marker, string sector, decimal baseYield) =>
    [
        new BondUniverseEntry
        {
            Secid = $"GRN{marker}", Isin = $"IGRN{marker}", ShortName = $"BOND GRN{marker}", SecName = $"Full name GRN{marker}",
            FaceValue = 1000m, Sector = sector, YieldFraction = baseYield, DurationYears = 2m, PricePercent = 99.5m,
            TurnoverRub = 10_000_000m, BidPercent = 99.9m, OfferPercent = 100m, NumTrades = 50, ListLevel = 1,
            MaturityDate = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(3)), UpdatedAt = DateTime.UtcNow,
        },
        new BondUniverseEntry
        {
            Secid = $"YLW{marker}", Isin = $"IYLW{marker}", ShortName = $"BOND YLW{marker}", SecName = $"Full name YLW{marker}",
            FaceValue = 1000m, Sector = sector, YieldFraction = baseYield - 0.001m, DurationYears = 2m, PricePercent = 99.5m,
            TurnoverRub = 10_000_000m, BidPercent = 99.9m, OfferPercent = 100m, NumTrades = 50, ListLevel = null,
            MaturityDate = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(3)), UpdatedAt = DateTime.UtcNow,
        },
        new BondUniverseEntry
        {
            Secid = $"RED{marker}", Isin = $"IRED{marker}", ShortName = $"BOND RED{marker}", SecName = $"Full name RED{marker}",
            FaceValue = 1000m, Sector = sector, YieldFraction = baseYield - 0.002m, DurationYears = 2m, PricePercent = 99.5m,
            TurnoverRub = 100_000m, BidPercent = 99m, OfferPercent = 100m, NumTrades = 5, ListLevel = 1,
            MaturityDate = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(3)), UpdatedAt = DateTime.UtcNow,
        },
    ];

    [Fact]
    public async Task GetUniverse_RowsCarryReliabilityAndReason_MatchingEachLevel()
    {
        var repo = new BondUniverseRepository(_factory.Database.ConnectionString);
        var marker = Guid.NewGuid().ToString("N")[..6];
        var sector = $"Sector{marker}";
        await repo.UpsertSnapshotBatchAsync(ReliabilityProbeEntries(marker, sector, baseYield: 0.30m));

        var client = await CreateAuthorizedClientAsync();
        var response = await client.GetAsync($"/api/universe?search={marker}&limit=10");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var rows = body.GetProperty("rows").EnumerateArray().ToList();

        rows.Should().HaveCount(3);
        foreach (var row in rows)
        {
            row.GetProperty("reliability").ValueKind.Should().Be(JsonValueKind.String);
            row.GetProperty("reliabilityReason").GetString().Should().NotBeNullOrWhiteSpace();
        }

        rows.Single(r => r.GetProperty("secid").GetString() == $"GRN{marker}").GetProperty("reliability").GetString().Should().Be("Green");
        rows.Single(r => r.GetProperty("secid").GetString() == $"YLW{marker}").GetProperty("reliability").GetString().Should().Be("Yellow");
        rows.Single(r => r.GetProperty("secid").GetString() == $"RED{marker}").GetProperty("reliability").GetString().Should().Be("Red");
    }

    [Fact]
    public async Task GetUniverse_ReliabilityFilter_FiltersNotWorseThanLevel()
    {
        var repo = new BondUniverseRepository(_factory.Database.ConnectionString);
        var marker = Guid.NewGuid().ToString("N")[..6];
        var sector = $"Sector{marker}";
        await repo.UpsertSnapshotBatchAsync(ReliabilityProbeEntries(marker, sector, baseYield: 0.30m));

        var client = await CreateAuthorizedClientAsync();

        async Task<List<string>> SecidsAsync(string? reliability)
        {
            var query = $"/api/universe?search={marker}&limit=10";
            if (reliability is not null) query += $"&reliability={reliability}";
            var response = await client.GetAsync(query);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            return body.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("secid").GetString()!).ToList();
        }

        (await SecidsAsync(null)).Should().BeEquivalentTo([$"GRN{marker}", $"YLW{marker}", $"RED{marker}"], "без фильтра — все уровни");
        (await SecidsAsync("green")).Should().BeEquivalentTo([$"GRN{marker}"], "green — только Green");
        (await SecidsAsync("yellow")).Should().BeEquivalentTo([$"GRN{marker}", $"YLW{marker}"], "yellow — не хуже жёлтого (Green+Yellow)");
        (await SecidsAsync("red")).Should().BeEquivalentTo([$"GRN{marker}", $"YLW{marker}", $"RED{marker}"], "red — не хуже красного = все уровни");
    }

    [Fact]
    public async Task GetUniverse_InvalidReliability_Returns422()
    {
        var client = await CreateAuthorizedClientAsync();

        var response = await client.GetAsync("/api/universe?reliability=bogus");

        response.StatusCode.Should().Be((HttpStatusCode)422);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("type").GetString().Should().Be("ValidationException");
    }

    [Fact]
    public async Task GetUniverseStatus_ReturnsTotalsAndLastRefresh()
    {
        var repo = new BondUniverseRepository(_factory.Database.ConnectionString);
        var marker = Guid.NewGuid().ToString("N")[..6];
        await repo.UpsertSnapshotBatchAsync([HealthyEntry($"ST{marker}", $"ISS{marker}AAA", 0.10m, 2m)]);

        var client = await CreateAuthorizedClientAsync();
        var response = await client.GetAsync("/api/universe/status");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("totalBonds").GetInt32().Should().BeGreaterThanOrEqualTo(1);
        body.GetProperty("lastRefreshUtc").ValueKind.Should().NotBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task GetUniverseStatus_NoToken_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/universe/status");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}

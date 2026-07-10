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
/// Задача 34 часть D.5-D.7 — критерии приёмки GET /api/analytics/allocation?source=market|recommended:
/// кандидаты строятся из ВСЕЙ гигиенически-чистой фикс-купонной вселенной банка (не из портфеля),
/// флоатеры исключены, строки несут риск-сигналы (задача 33), recommended дополнительно отфильтровывает
/// Caution-кандидатов и применяет секторный лимит. source=portfolio (дефолт) — регресс, поведение не
/// менялось (см. также Stage08EndpointsTests — 3 существующих теста без изменений).
/// <para>
/// Тест делит один общий MySQL-контейнер с остальными тестами коллекции "Integration" (bond_universe
/// не очищается между тестами) — тот же паттерн, что ReplacementCandidatesEndpointTests: уникальный
/// маркер в SECID/секторе + доходности у самой границы гигиенического порога (MaxSaneYieldFraction=0.45)
/// и достаточно большой amountRub, чтобы КАЖДЫЙ прошедший фильтр кандидат из пула (maxLotsPerCandidate=1)
/// гарантированно получил докупку независимо от того, что ещё есть в общем банке.
/// </para>
/// </summary>
[Collection("Integration")]
public class AllocationMarketEndpointTests
{
    private readonly TestWebApplicationFactory _factory;

    public AllocationMarketEndpointTests(TestWebApplicationFactory factory)
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

    /// <summary>Дефолты (оборот 1 млн, спред bid/offer ~1%) дают <c>LiquidityScore.Medium</c>
    /// (см. doc-comment ReplacementCandidatesEndpointTests.HealthyEntry — тот же helper).</summary>
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
        GspreadApproxFraction = yieldFraction - 0.10m,
        IsFloater = isFloater,
        MaturityDate = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(3)),
        UpdatedAt = DateTime.UtcNow,
    };

    // ─── Валидация source ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAllocation_InvalidSource_Returns422()
    {
        var (client, _, _) = await CreateAuthorizedClientAsync();

        var response = await client.GetAsync("/api/analytics/allocation?amountRub=1000&source=bogus");

        response.StatusCode.Should().Be((HttpStatusCode)422);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("type").GetString().Should().Be("ValidationException");
    }

    // ─── source=market (план часть D.5) ─────────────────────────────────────────────────────

    [Fact]
    public async Task GetAllocation_Market_UsesBankUniverse_NotPortfolio_ExcludesFloaters_WithRiskSignals()
    {
        var (client, _, accountId) = await CreateAuthorizedClientAsync();
        var marker = Guid.NewGuid().ToString("N")[..6];
        var sector = $"Sector{marker}";

        // Портфель непустой (позиция засеяна), но НЕ фикс-купонная бумага портфеля должна попасть в
        // ответ market — кандидаты строятся из банка, не из holdings (план часть B "НЕ из портфеля").
        await SeedPortfolioPositionAsync(accountId);

        var universeRepo = new BondUniverseRepository(_factory.Database.ConnectionString);
        var healthySecid = $"HI{marker}";
        var floaterSecid = $"FL{marker}";
        await universeRepo.UpsertSnapshotBatchAsync(new[]
        {
            HealthyEntry(healthySecid, sector, 0.44m, 2m, turnover: 10_000_000m, bidPercent: 99.9m, offerPercent: 100m),
            HealthyEntry(floaterSecid, sector, 0.441m, 2m, isFloater: true), // выше по yield, но флоатер — не должен попасть вовсе
        });

        // Огромный бюджет — при maxLotsPerCandidate=1 (задача 34 часть B.3) КАЖДЫЙ прошедший гигиену
        // кандидат из пула получает ровно 1 лот, независимо от порядка/состава общего банка.
        var response = await client.GetAsync("/api/analytics/allocation?amountRub=100000000&source=market");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("source").GetString().Should().Be("market");
        body.GetProperty("disclaimer").GetString().Should().Contain("вселенной", "дисклеймер market должен явно называть источник — весь банк, не портфель");
        body.GetProperty("candidatePoolLimit").GetInt32().Should().Be(200);
        body.GetProperty("candidatePoolAvailable").GetInt32().Should().BeGreaterThan(0);

        var allocations = body.GetProperty("allocations").EnumerateArray().ToList();
        var skipped = body.GetProperty("skipped").EnumerateArray().ToList();
        var allSecids = allocations.Select(a => a.GetProperty("secid").GetString())
            .Concat(skipped.Select(s => s.GetProperty("secid").GetString()))
            .ToList();

        allSecids.Should().NotContain(floaterSecid, "флоатеры исключены из рыночной аллокации (задача 31/34)");

        var healthyLine = allocations.Should().ContainSingle(a => a.GetProperty("secid").GetString() == healthySecid).Subject;
        healthyLine.GetProperty("instrumentId").ValueKind.Should().Be(JsonValueKind.Null, "рыночный кандидат не связан с таблицей Instrument — идентификатор это secid");
        healthyLine.GetProperty("quantity").GetDecimal().Should().Be(1m, "maxLotsPerCandidate=1 для рыночных источников");
        healthyLine.GetProperty("sector").GetString().Should().Be(sector);

        var signals = healthyLine.GetProperty("riskSignals");
        signals.ValueKind.Should().NotBe(JsonValueKind.Null);
        signals.GetProperty("liquidity").GetString().Should().Be("Good");
    }

    [Fact]
    public async Task GetAllocation_Market_NonPositiveAmount_Returns422()
    {
        var (client, _, _) = await CreateAuthorizedClientAsync();

        var response = await client.GetAsync("/api/analytics/allocation?amountRub=0&source=market");

        response.StatusCode.Should().Be((HttpStatusCode)422);
    }

    // ─── source=recommended (план часть D.6) ────────────────────────────────────────────────

    [Fact]
    public async Task GetAllocation_Recommended_FiltersCautionLiquidity_MarketStillIncludesIt()
    {
        var (client, _, _) = await CreateAuthorizedClientAsync();
        var marker = Guid.NewGuid().ToString("N")[..6];
        var sector = $"Sector{marker}";

        var universeRepo = new BondUniverseRepository(_factory.Database.ConnectionString);
        var goodSecid = $"GD{marker}";
        var cautionSecid = $"CN{marker}"; // оборот 150к — проходит гигиену (порог 100к), но LiquidityScore.Low -> Caution
        var goodEntry = HealthyEntry(goodSecid, sector, 0.44m, 2m, turnover: 10_000_000m, bidPercent: 99.9m, offerPercent: 100m);
        var cautionEntry = HealthyEntry(cautionSecid, sector, 0.43m, 2m, turnover: 150_000m, bidPercent: 90m, offerPercent: 110m);
        // Изолируем сигнал СПРЕДА от сигнала ЛИКВИДНОСТИ: HealthyEntry по умолчанию вычисляет
        // GspreadApproxFraction = yieldFraction - 0.10 — при разных yieldFraction два кандидата дают
        // разные спреды, и в корзине из ДВУХ участников (уникальный маркерный сектор — больше членов
        // в корзине нет) любое небольшое расхождение уводит отклонение от медианы выше порога 0.0020
        // и обоих (не только "плохого" по ликвидности) кандидатов помечает Caution по спреду — тест
        // должен различать источники исключения, поэтому фиксируем ОДИНАКОВЫЙ спред у обоих записей
        // (медиана корзины = этот же спред, отклонение = 0 у обоих, Spread=Neutral у обоих).
        goodEntry.GspreadApproxFraction = 0.30m;
        cautionEntry.GspreadApproxFraction = 0.30m;
        await universeRepo.UpsertSnapshotBatchAsync(new[] { goodEntry, cautionEntry });

        var recommendedResponse = await client.GetAsync("/api/analytics/allocation?amountRub=100000000&source=recommended");
        recommendedResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var recommendedBody = await recommendedResponse.Content.ReadFromJsonAsync<JsonElement>();

        recommendedBody.GetProperty("source").GetString().Should().Be("recommended");
        recommendedBody.GetProperty("disclaimer").GetString().Should().Contain("риск-сигнал");

        var recommendedSecids = recommendedBody.GetProperty("allocations").EnumerateArray()
            .Select(a => a.GetProperty("secid").GetString())
            .Concat(recommendedBody.GetProperty("skipped").EnumerateArray().Select(s => s.GetProperty("secid").GetString()))
            .ToList();

        recommendedSecids.Should().Contain(goodSecid, "кандидат с хорошей ликвидностью должен пройти фильтр recommended");
        recommendedSecids.Should().NotContain(cautionSecid, "Caution-ликвидность отфильтрована ДО жадного прохода — кандидат не должен появиться ни в allocations, ни в skipped");

        // Контраст: тот же Caution-кандидат ПОЯВЛЯЕТСЯ в market (там риск-фильтра нет вообще).
        var marketResponse = await client.GetAsync("/api/analytics/allocation?amountRub=100000000&source=market");
        var marketBody = await marketResponse.Content.ReadFromJsonAsync<JsonElement>();
        var marketAllocations = marketBody.GetProperty("allocations").EnumerateArray().ToList();
        var marketSecids = marketAllocations
            .Select(a => a.GetProperty("secid").GetString())
            .Concat(marketBody.GetProperty("skipped").EnumerateArray().Select(s => s.GetProperty("secid").GetString()))
            .ToList();

        marketSecids.Should().Contain(cautionSecid, "source=market не фильтрует по риск-сигналам — Caution-кандидат всё равно попадает в ответ");

        // Задача 38 часть A.3: светофор надёжности доходит до RiskSignals строки аллокации market —
        // спред нейтрализован (одинаковый GspreadApproxFraction выше), поэтому reliability детерминирован
        // только ликвидностью: goodEntry (High, листинг 1) -> Green, cautionEntry (Low) -> Red.
        var goodLine = marketAllocations.SingleOrDefault(a => a.GetProperty("secid").GetString() == goodSecid);
        if (goodLine.ValueKind != JsonValueKind.Undefined)
        {
            goodLine.GetProperty("riskSignals").GetProperty("reliability").GetString().Should().Be("Green");
        }
        var cautionLine = marketAllocations.SingleOrDefault(a => a.GetProperty("secid").GetString() == cautionSecid);
        if (cautionLine.ValueKind != JsonValueKind.Undefined)
        {
            cautionLine.GetProperty("riskSignals").GetProperty("reliability").GetString().Should().Be("Red");
        }
    }

    // ─── source=portfolio (план часть D.7 — регресс дефолта) ────────────────────────────────

    [Fact]
    public async Task GetAllocation_ExplicitSourcePortfolio_SameShapeAsDefault()
    {
        var (client, _, _) = await CreateAuthorizedClientAsync();

        var defaultResponse = await client.GetAsync("/api/analytics/allocation?amountRub=15000");
        var explicitResponse = await client.GetAsync("/api/analytics/allocation?amountRub=15000&source=portfolio");

        defaultResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        explicitResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var defaultBody = await defaultResponse.Content.ReadFromJsonAsync<JsonElement>();
        var explicitBody = await explicitResponse.Content.ReadFromJsonAsync<JsonElement>();

        defaultBody.GetProperty("source").GetString().Should().Be("portfolio");
        explicitBody.GetProperty("source").GetString().Should().Be("portfolio");
        explicitBody.GetProperty("leftoverRub").GetDecimal().Should().Be(defaultBody.GetProperty("leftoverRub").GetDecimal());
        explicitBody.GetProperty("disclaimer").GetString().Should().Be(defaultBody.GetProperty("disclaimer").GetString());
        // Задача 34 часть B.4: пул-поля осмысленны только для market/recommended.
        explicitBody.GetProperty("candidatePoolLimit").ValueKind.Should().Be(JsonValueKind.Null);
        explicitBody.GetProperty("candidatePoolAvailable").ValueKind.Should().Be(JsonValueKind.Null);
        explicitBody.GetProperty("candidatePoolTruncated").ValueKind.Should().Be(JsonValueKind.Null);
    }

    private async Task SeedPortfolioPositionAsync(ulong accountId)
    {
        var instrumentRepo = new InstrumentRepository(_factory.Database.ConnectionString);
        var isin = $"RU{Guid.NewGuid():N}".Substring(0, 12);
        var instrumentId = await instrumentRepo.UpsertAsync(new Instrument
        {
            Isin = isin,
            Issuer = "Портфельный эмитент",
            FaceValue = 1000m,
            Currency = "RUB",
            CouponType = CouponType.Fixed,
            MaturityDate = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(3)),
        });

        var couponRepo = new CouponScheduleRepository(_factory.Database.ConnectionString);
        await couponRepo.ReplaceForInstrumentAsync(instrumentId, new[]
        {
            new CouponSchedule { InstrumentId = instrumentId, CouponDate = DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(-1)), ValueRub = 20m, PeriodDays = 182, IsKnown = true },
            new CouponSchedule { InstrumentId = instrumentId, CouponDate = DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(5)), ValueRub = 20m, PeriodDays = 182, IsKnown = true },
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
        await positionRepo.UpsertAsync(new Position
        {
            AccountId = accountId,
            InstrumentId = instrumentId,
            Quantity = 10m,
            AvgPurchasePrice = 1000m,
        });
    }
}

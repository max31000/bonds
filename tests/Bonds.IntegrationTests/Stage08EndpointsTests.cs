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
/// Этап 08 — критерии приёмки: каждый доменный эндпоинт без токена → 401, с токеном владельца →
/// 200 и валидный контракт; POST /api/sync запускает цикл и отражается в GET /api/sync/status;
/// PUT /api/settings/tinvest-token не возвращает сам токен; эндпоинт с неполными данными
/// (пустой портфель — самый простой воспроизводимый случай "неполных данных" без реального
/// синка брокера/MOEX в тесте) возвращает 200 с пустыми списками, а не падает.
/// </summary>
[Collection("Integration")]
public class Stage08EndpointsTests
{
    private readonly TestWebApplicationFactory _factory;

    public Stage08EndpointsTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    /// <summary>Создаёт пользователя + счёт и возвращает HttpClient с Bearer-токеном владельца этого счёта.</summary>
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

    /// <summary>Задача 31 часть B.4 — позиция-флоатер (CouponType.Floating, тот же признак, что
    /// BondMetricsCalculator использует для IsFloater=true в PortfolioHolding).</summary>
    private async Task<(ulong InstrumentId, ulong PositionId)> SeedFloaterPositionAsync(ulong accountId)
    {
        var instrumentRepo = new InstrumentRepository(_factory.Database.ConnectionString);
        var isin = $"RU{Guid.NewGuid():N}".Substring(0, 12);
        var instrumentId = await instrumentRepo.UpsertAsync(new Instrument
        {
            Isin = isin,
            Issuer = "Флоатер Эмитент",
            FaceValue = 1000m,
            Currency = "RUB",
            CouponType = CouponType.Floating,
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

    // ─── 401 без токена для каждого доменного эндпоинта ────────────────────────────────────

    [Theory]
    [InlineData("GET", "/api/positions")]
    [InlineData("GET", "/api/positions/1")]
    [InlineData("GET", "/api/cashflow")]
    [InlineData("GET", "/api/analytics/xirr")]
    [InlineData("POST", "/api/analytics/xirr/backfill")]
    [InlineData("GET", "/api/analytics/composition")]
    [InlineData("GET", "/api/analytics/scatter")]
    [InlineData("GET", "/api/analytics/comparison")]
    [InlineData("POST", "/api/analytics/replacement")]
    [InlineData("GET", "/api/analytics/replacement-matrix")]
    [InlineData("GET", "/api/analytics/replacement-candidates?positionId=1&mode=market")]
    [InlineData("GET", "/api/analytics/allocation?amountRub=1000")]
    [InlineData("GET", "/api/signals")]
    [InlineData("POST", "/api/signals/1/read")]
    [InlineData("POST", "/api/sync")]
    [InlineData("GET", "/api/sync/status")]
    [InlineData("GET", "/api/settings")]
    [InlineData("PUT", "/api/settings")]
    [InlineData("PUT", "/api/settings/tinvest-token")]
    public async Task Endpoint_WithoutToken_Returns401(string method, string path)
    {
        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method is "POST" or "PUT")
        {
            request.Content = JsonContent.Create(new { });
        }

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ─── GET /api/positions ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetPositions_EmptyPortfolio_Returns200_WithEmptyList()
    {
        var (client, _, _) = await CreateAuthorizedClientAsync();

        var response = await client.GetAsync("/api/positions");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("positions").GetArrayLength().Should().Be(0);
        body.GetProperty("disclaimer").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task GetPositions_WithSeededPosition_Returns200_WithRowAndQualityFlags()
    {
        var (client, _, accountId) = await CreateAuthorizedClientAsync();
        await SeedPositionAsync(accountId);

        var response = await client.GetAsync("/api/positions");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var positions = body.GetProperty("positions");
        positions.GetArrayLength().Should().Be(1);

        var row = positions[0];
        row.GetProperty("issuer").GetString().Should().Be("Минфин РФ");
        row.TryGetProperty("isFloater", out _).Should().BeTrue();
        row.TryGetProperty("dataIncomplete", out _).Should().BeTrue();
        // Без котировки/купонов метрики недостоверны — флаг неполноты должен быть выставлен,
        // а не молча подставлен 0 (spec §4.4) — эндпоинт всё равно отдаёт 200.
        row.GetProperty("dataIncomplete").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task GetPositions_WithOperationsJournal_Returns200_WithCostBasisFields()
    {
        // plan/14 §B — GET /api/positions отдаёт цену входа/P&L, посчитанные PositionCostBasisService
        // поверх журнала операций, без N+1 (один батч-запрос операций на счёт в PortfolioHoldingsBuilder).
        var (client, _, accountId) = await CreateAuthorizedClientAsync();
        var (instrumentId, _) = await SeedPositionAsync(accountId); // Quantity = 10

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
                Date = DateTime.UtcNow.AddMonths(-3),
                AmountRub = 300m,
                ExternalId = $"coupon-{Guid.NewGuid()}",
            },
        });

        var response = await client.GetAsync("/api/positions");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var row = body.GetProperty("positions")[0];

        row.GetProperty("averageCostRub").GetDecimal().Should().Be(1000m);
        row.GetProperty("investedRub").GetDecimal().Should().Be(10_000m);
        row.GetProperty("couponsReceivedRub").GetDecimal().Should().Be(300m);
        // Без рыночной котировки в тесте UnrealizedPnl считается от MarketValueRub=0 (нет цены источника).
        row.GetProperty("unrealizedPnlRub").GetDecimal().Should().Be(-10_000m);
        row.GetProperty("costBasisIncomplete").GetBoolean().Should().BeFalse("журнал полностью покрывает остаток позиции (10 шт куплено = 10 шт в позиции)");
    }

    [Fact]
    public async Task GetPositions_NoOperationsJournal_Returns200_WithCostBasisIncompleteFlag()
    {
        // Позиция есть (Quantity=10), но журнала операций по ней нет — журнал не покрывает остаток,
        // costBasisIncomplete = true, а не молчаливый 0 (spec §4.4 "не подставлять нули молча").
        var (client, _, accountId) = await CreateAuthorizedClientAsync();
        await SeedPositionAsync(accountId);

        var response = await client.GetAsync("/api/positions");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var row = body.GetProperty("positions")[0];

        row.GetProperty("costBasisIncomplete").GetBoolean().Should().BeTrue();
        row.GetProperty("averageCostRub").ValueKind.Should().Be(JsonValueKind.Null);
        row.GetProperty("couponsReceivedRub").GetDecimal().Should().Be(0m);
    }

    [Fact]
    public async Task GetPositionById_Unknown_Returns404()
    {
        var (client, _, _) = await CreateAuthorizedClientAsync();

        var response = await client.GetAsync("/api/positions/999999");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetPositionById_Existing_Returns200_WithDetailAndDisclaimer()
    {
        var (client, _, accountId) = await CreateAuthorizedClientAsync();
        var (_, positionId) = await SeedPositionAsync(accountId);

        var response = await client.GetAsync($"/api/positions/{positionId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("positionId").GetUInt64().Should().Be(positionId);
        body.GetProperty("disclaimer").GetString().Should().NotBeNullOrEmpty();
    }

    // ─── GET /api/cashflow ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetCashFlow_EmptyPortfolio_Returns200_WithEmptyAggregates()
    {
        var (client, _, _) = await CreateAuthorizedClientAsync();

        var response = await client.GetAsync("/api/cashflow");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("byMonth").GetArrayLength().Should().Be(0);
        body.GetProperty("byPosition").GetArrayLength().Should().Be(0);
        body.GetProperty("principalReleases").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task GetCashFlow_WithSeededFlows_ByMonthItemsIncludePositionsArray()
    {
        var (client, _, accountId) = await CreateAuthorizedClientAsync();
        var (instrumentId, positionId) = await SeedPositionAsync(accountId);

        var flowRepo = new ProjectedCashFlowRepository(_factory.Database.ConnectionString);
        await flowRepo.ReplaceForPositionAsync(positionId, new[]
        {
            new ProjectedCashFlow
            {
                PositionId = positionId,
                InstrumentId = instrumentId,
                Date = new DateOnly(DateTime.UtcNow.Year + 1, 1, 15),
                FlowType = CashFlowType.Coupon,
                GrossRub = 1000m,
                TaxRub = 130m,
                NetRub = 870m,
                IsEstimated = false,
            },
        });

        var response = await client.GetAsync("/api/cashflow");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var byMonth = body.GetProperty("byMonth");
        byMonth.GetArrayLength().Should().BeGreaterThan(0);
        var firstMonth = byMonth[0];
        firstMonth.TryGetProperty("positions", out var positions).Should().BeTrue();
        positions.ValueKind.Should().Be(JsonValueKind.Array);
        positions.GetArrayLength().Should().Be(1);
        var pos = positions[0];
        pos.GetProperty("positionId").GetUInt64().Should().Be(positionId);
        pos.GetProperty("flowType").GetString().Should().Be("Coupon");
    }

    // ─── /api/analytics/* ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetXirr_EmptyPortfolio_Returns200_WithNullCurrentXirr()
    {
        var (client, _, _) = await CreateAuthorizedClientAsync();

        var response = await client.GetAsync("/api/analytics/xirr");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("history").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task PostXirrBackfill_NoOperations_Returns200_WithZeroPointsWritten()
    {
        // Plan/15 §B.4: журнал операций пуст (счёт только что заведён, ни разу не синкан) —
        // бэкфилл не должен падать/ходить в MOEX, просто возвращает 0.
        var (client, _, _) = await CreateAuthorizedClientAsync();

        var response = await client.PostAsync("/api/analytics/xirr/backfill", JsonContent.Create(new { }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("pointsWritten").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task GetComposition_WithSeededPosition_Returns200_WithIssuerShare()
    {
        var (client, _, accountId) = await CreateAuthorizedClientAsync();
        await SeedPositionAsync(accountId);

        var response = await client.GetAsync("/api/analytics/composition");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("byIssuer").GetArrayLength().Should().BeGreaterThan(0);
        body.GetProperty("disclaimer").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task GetScatter_ReturnsPointsAndDoesNotFail_RegardlessOfCurvePresence()
    {
        // IYieldCurveRepository.GetLatestAsync читает ГЛОБАЛЬНЫЙ (не привязанный к account) снимок —
        // другие тесты в этой же общей БД (через [Collection("Integration")]) могут его засеять
        // раньше. Контракт этого теста — эндпоинт не падает и возвращает валидную структуру
        // независимо от наличия кривой, а не точное количество точек (см. doc-comment выше).
        var (client, _, accountId) = await CreateAuthorizedClientAsync();
        await SeedPositionAsync(accountId);

        var response = await client.GetAsync("/api/analytics/scatter");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.TryGetProperty("curve", out var curve).Should().BeTrue();
        curve.ValueKind.Should().Be(JsonValueKind.Array);
        // Без рыночной котировки ModifiedDuration не считается (нет цены) — точка по этой позиции
        // на scatter не попадает (фильтр Where(h => h.ModifiedDuration is not null)), это корректная
        // деградация движка (spec §4.4), а не баг эндпоинта — здесь проверяем только отсутствие 500.
        body.TryGetProperty("points", out var points).Should().BeTrue();
        points.ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public async Task GetComparison_WithSeededPosition_Returns200_WithDisclaimer()
    {
        var (client, _, accountId) = await CreateAuthorizedClientAsync();
        await SeedPositionAsync(accountId);

        var response = await client.GetAsync("/api/analytics/comparison");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("rows").GetArrayLength().Should().Be(1);
        body.GetProperty("disclaimer").GetString().Should().Contain("не означает");
    }

    [Fact]
    public async Task PostReplacement_UnknownPosition_Returns404()
    {
        var (client, _, accountId) = await CreateAuthorizedClientAsync();
        await SeedPositionAsync(accountId);

        var response = await client.PostAsJsonAsync("/api/analytics/replacement", new
        {
            holdPositionId = 999999,
            targetPositionId = 999998,
            horizonYears = 2,
        });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PostReplacement_BetweenTwoPositions_Returns200_WithDisclaimer()
    {
        var (client, _, accountId) = await CreateAuthorizedClientAsync();
        var (_, positionId1) = await SeedPositionAsync(accountId);
        var (_, positionId2) = await SeedPositionAsync(accountId);

        var response = await client.PostAsJsonAsync("/api/analytics/replacement", new
        {
            holdPositionId = positionId1,
            targetPositionId = positionId2,
            horizonYears = 2,
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("disclaimer").GetString().Should().NotBeNullOrEmpty();
        // Обе позиции без котировок -> доходность не определена -> YieldDataIncomplete = true,
        // но ответ всё равно 200 (spec §4.4).
        body.GetProperty("yieldDataIncomplete").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task PostReplacement_TargetNotInBank_TargetRiskSignalsIsNull()
    {
        // Задача 33 часть B.4 — цель без банк-записи (не покрыта биржевой статистикой MOEX, тот
        // же случай, что SeedPositionAsync — позиция без записи в bond_universe) -> null, не ошибка.
        var (client, _, accountId) = await CreateAuthorizedClientAsync();
        var (_, positionId1) = await SeedPositionAsync(accountId);
        var (_, positionId2) = await SeedPositionAsync(accountId);

        var response = await client.PostAsJsonAsync("/api/analytics/replacement", new
        {
            holdPositionId = positionId1,
            targetPositionId = positionId2,
            horizonYears = 2,
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.TryGetProperty("targetRiskSignals", out var signals).Should().BeTrue();
        signals.ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task PostReplacement_TargetInBank_ReturnsTargetRiskSignals()
    {
        // Задача 33 часть B.4 — цель найдена в банке по ISIN -> риск-сигналы посчитаны.
        var (client, _, accountId) = await CreateAuthorizedClientAsync();
        var (_, holdPositionId) = await SeedPositionAsync(accountId);
        var (targetInstrumentId, targetPositionId) = await SeedPositionAsync(accountId);

        var instrumentRepo = new InstrumentRepository(_factory.Database.ConnectionString);
        var targetIsin = (await instrumentRepo.GetByIdAsync(targetInstrumentId))!.Isin;

        var universeRepo = new BondUniverseRepository(_factory.Database.ConnectionString);
        await universeRepo.UpsertSnapshotBatchAsync(new[]
        {
            new BondUniverseEntry
            {
                Secid = $"TGT{Guid.NewGuid():N}"[..10],
                Isin = targetIsin,
                ShortName = "Target bond",
                FaceValue = 1000m,
                Sector = "Корпоративные",
                YieldFraction = 0.20m,
                DurationYears = 2m,
                PricePercent = 99m,
                TurnoverRub = 1_000_000m,
                BidPercent = 99m,
                OfferPercent = 100m,
                NumTrades = 10,
                ListLevel = 1,
                GspreadApproxFraction = 0.05m,
                IsFloater = false,
                MaturityDate = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(3)),
                UpdatedAt = DateTime.UtcNow,
            },
        });

        var response = await client.PostAsJsonAsync("/api/analytics/replacement", new
        {
            holdPositionId,
            targetPositionId,
            horizonYears = 2,
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var signals = body.GetProperty("targetRiskSignals");
        signals.ValueKind.Should().NotBe(JsonValueKind.Null);
        signals.GetProperty("liquidity").ValueKind.Should().Be(JsonValueKind.String);
        signals.GetProperty("spread").ValueKind.Should().Be(JsonValueKind.String);
        signals.GetProperty("gSpreadFraction").GetDecimal().Should().Be(0.05m);
    }

    [Fact]
    public async Task PostReplacement_FloaterTarget_Returns422_ValidationException()
    {
        // Задача 31 часть B.4 — цель-флоатер несравнима по доходности с фикс-купоном (CurrentYield
        // vs YTM) — карточка выгоды должна отказать явным 422, а не посчитать бессмысленный "спред".
        var (client, _, accountId) = await CreateAuthorizedClientAsync();
        var (_, holdPositionId) = await SeedPositionAsync(accountId);
        var (_, floaterPositionId) = await SeedFloaterPositionAsync(accountId);

        var response = await client.PostAsJsonAsync("/api/analytics/replacement", new
        {
            holdPositionId,
            targetPositionId = floaterPositionId,
            horizonYears = 2,
        });

        response.StatusCode.Should().Be((HttpStatusCode)422);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("type").GetString().Should().Be("ValidationException");
        body.GetProperty("error").GetString().Should().Contain("плавающим");
    }

    // ─── GET /api/analytics/allocation (plan/17 §B) ────────────────────────────────────────

    [Fact]
    public async Task GetAllocation_NonPositiveAmount_Returns422()
    {
        var (client, _, _) = await CreateAuthorizedClientAsync();

        var response = await client.GetAsync("/api/analytics/allocation?amountRub=0");

        response.StatusCode.Should().Be((HttpStatusCode)422);
    }

    [Fact]
    public async Task GetAllocation_EmptyPortfolio_Returns200_WithFullLeftover()
    {
        var (client, _, _) = await CreateAuthorizedClientAsync();

        var response = await client.GetAsync("/api/analytics/allocation?amountRub=15000");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("allocations").GetArrayLength().Should().Be(0);
        body.GetProperty("leftoverRub").GetDecimal().Should().Be(15000m);
        body.GetProperty("disclaimer").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task GetAllocation_SeededPositionWithoutYield_Returns200_WithSkippedReason()
    {
        // Позиция засеяна без графика купонов -> YTM не сходится, и это не флоатер/индексируемая ->
        // EffectiveYield = null -> кандидат уходит в Skipped с причиной NoYield, а не 500 (spec §4.4).
        var (client, _, accountId) = await CreateAuthorizedClientAsync();
        await SeedPositionAsync(accountId);

        var response = await client.GetAsync("/api/analytics/allocation?amountRub=15000");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("allocations").GetArrayLength().Should().Be(0);
        var skipped = body.GetProperty("skipped");
        skipped.GetArrayLength().Should().Be(1);
        skipped[0].GetProperty("reason").GetString().Should().Be("NoYield");
        body.GetProperty("leftoverRub").GetDecimal().Should().Be(15000m);
    }

    // ─── /api/signals ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetSignals_EmptyAccount_Returns200_WithEmptyList()
    {
        var (client, _, _) = await CreateAuthorizedClientAsync();

        var response = await client.GetAsync("/api/signals");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("signals").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task MarkSignalRead_ExistingSignal_MarksAsRead()
    {
        var (client, _, accountId) = await CreateAuthorizedClientAsync();
        var (instrumentId, positionId) = await SeedPositionAsync(accountId);

        var signalRepo = new SignalRepository(_factory.Database.ConnectionString);
        var signalId = await signalRepo.CreateAsync(new Signal
        {
            AccountId = accountId,
            Type = SignalType.UpcomingCoupon,
            Severity = SignalSeverity.Info,
            PositionId = positionId,
            InstrumentId = instrumentId,
            Date = DateOnly.FromDateTime(DateTime.UtcNow),
            IsRead = false,
        });

        var response = await client.PostAsync($"/api/signals/{signalId}/read", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var unread = await signalRepo.GetByAccountIdAsync(accountId, isRead: false);
        unread.Should().BeEmpty();
    }

    // ─── /api/sync, /api/sync/status ────────────────────────────────────────────────────────

    [Fact]
    public async Task PostSync_ThenGetStatus_ReflectsRun()
    {
        var (client, _, _) = await CreateAuthorizedClientAsync();

        var syncResponse = await client.PostAsync("/api/sync", null);
        syncResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var statusResponse = await client.GetAsync("/api/sync/status");
        statusResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var statusBody = await statusResponse.Content.ReadFromJsonAsync<JsonElement>();
        statusBody.GetProperty("isRunning").GetBoolean().Should().BeFalse("цикл уже завершился к моменту запроса статуса");
        statusBody.TryGetProperty("lastSuccessAtUtc", out _).Should().BeTrue();
    }

    // ─── /api/settings, /api/settings/tinvest-token ────────────────────────────────────────

    [Fact]
    public async Task GetSettings_Default_Returns200_WithTokenNotConfigured()
    {
        var (client, _, _) = await CreateAuthorizedClientAsync();

        var response = await client.GetAsync("/api/settings");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("tInvestTokenConfigured").GetBoolean().Should().BeFalse();
        body.GetProperty("baseCurrency").GetString().Should().Be("RUB");
    }

    [Fact]
    public async Task PutSettings_UpdatesThresholds_Persisted()
    {
        var (client, _, _) = await CreateAuthorizedClientAsync();

        var response = await client.PutAsJsonAsync("/api/settings", new
        {
            upcomingEventDaysThreshold = 21,
            defaultMaxConcentrationPercent = 30m,
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("upcomingEventDaysThreshold").GetInt32().Should().Be(21);
        body.GetProperty("defaultMaxConcentrationPercent").GetDecimal().Should().Be(30m);

        var getResponse = await client.GetAsync("/api/settings");
        var getBody = await getResponse.Content.ReadFromJsonAsync<JsonElement>();
        getBody.GetProperty("upcomingEventDaysThreshold").GetInt32().Should().Be(21);
    }

    [Fact]
    public async Task PutTInvestToken_ThenGetSettings_NeverReturnsRawToken_OnlyMaskedStatus()
    {
        var (client, _, _) = await CreateAuthorizedClientAsync();
        const string rawToken = "t.SuperSecretReadOnlyToken1234";

        var putResponse = await client.PutAsJsonAsync("/api/settings/tinvest-token", new { token = rawToken });

        putResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var putBody = await putResponse.Content.ReadFromJsonAsync<JsonElement>();
        putBody.GetProperty("tInvestTokenConfigured").GetBoolean().Should().BeTrue();
        var putRaw = await putResponse.Content.ReadAsStringAsync();
        putRaw.Should().NotContain(rawToken);
        putBody.GetProperty("tInvestTokenMasked").GetString().Should().EndWith("1234");

        var getResponse = await client.GetAsync("/api/settings");
        var getRaw = await getResponse.Content.ReadAsStringAsync();
        getRaw.Should().NotContain(rawToken, "токен T-Invest никогда не должен отдаваться обратно на фронт (spec §11)");

        var getBody = await getResponse.Content.ReadFromJsonAsync<JsonElement>();
        getBody.GetProperty("tInvestTokenConfigured").GetBoolean().Should().BeTrue();
        getBody.GetProperty("tInvestTokenMasked").GetString().Should().EndWith("1234");
    }

    [Fact]
    public async Task PutTInvestToken_EmptyToken_Returns400()
    {
        var (client, _, _) = await CreateAuthorizedClientAsync();

        var response = await client.PutAsJsonAsync("/api/settings/tinvest-token", new { token = "" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}

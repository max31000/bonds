using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bonds.Core.Models;
using Bonds.Core.Time;
using Bonds.Infrastructure.Repositories;
using Bonds.IntegrationTests.Infrastructure;
using FluentAssertions;
using Xunit;

namespace Bonds.IntegrationTests;

/// <summary>
/// Задача 48 — математика замены v2 на уровне HTTP: горизонт из настроек пользователя (миграция 021),
/// усечение горизонта сроком цели, слиппедж по bid/offer банка, валидация настройки горизонта.
/// Сама арифметика покрыта юнит-тестами <c>SwitchAnalysisServiceTests</c> (эталонные числа руками).
/// </summary>
[Collection("Integration")]
public class ReplacementMathEndpointTests
{
    private readonly TestWebApplicationFactory _factory;

    public ReplacementMathEndpointTests(TestWebApplicationFactory factory) => _factory = factory;

    private async Task<(HttpClient Client, ulong AccountId)> CreateAuthorizedClientAsync()
    {
        var userRepo = new UserRepository(_factory.Database.ConnectionString);
        var telegramId = Random.Shared.NextInt64(1, long.MaxValue);
        var userId = await userRepo.CreateAsync(new User { TelegramId = telegramId });
        var accountId = await new AccountRepository(_factory.Database.ConnectionString)
            .CreateAsync(new Account { UserId = userId, Name = "Основной счёт" });

        var client = _factory.CreateClient();
        Mvv42TestAuth.Authorize(client, telegramId);
        return (client, accountId);
    }

    /// <summary>Позиция: 10 бумаг номиналом 1000, фикс-купон, чистая=грязная цена 1000 ₽ (MV = 10 000 ₽), погашение через <paramref name="daysToMaturity"/> дней.</summary>
    private async Task<(ulong InstrumentId, ulong PositionId, string Isin)> SeedPositionAsync(ulong accountId, int daysToMaturity)
    {
        var today = BusinessClock.MoscowToday();
        var isin = $"RU{Guid.NewGuid():N}"[..12];
        var instrumentId = await new InstrumentRepository(_factory.Database.ConnectionString).UpsertAsync(new Instrument
        {
            Isin = isin,
            Issuer = "Эмитент",
            FaceValue = 1000m,
            Currency = "RUB",
            CouponType = CouponType.Fixed,
            MaturityDate = today.AddDays(daysToMaturity),
        });

        await new CouponScheduleRepository(_factory.Database.ConnectionString).ReplaceForInstrumentAsync(instrumentId, new[]
        {
            new CouponSchedule { InstrumentId = instrumentId, CouponDate = today.AddDays(-60), ValueRub = 50m, PeriodDays = 182, IsKnown = true },
            new CouponSchedule { InstrumentId = instrumentId, CouponDate = today.AddDays(120), ValueRub = 50m, PeriodDays = 182, IsKnown = true },
            new CouponSchedule { InstrumentId = instrumentId, CouponDate = today.AddDays(daysToMaturity), ValueRub = 50m, PeriodDays = 182, IsKnown = true },
        });

        await new MarketQuoteRepository(_factory.Database.ConnectionString).UpsertAsync(new MarketQuote
        {
            InstrumentId = instrumentId,
            AsOf = today,
            CleanPrice = 1000m,
            DirtyPrice = 1000m,
            Accrued = 0m,
            Source = MarketQuoteSource.Moex,
        });

        var positionId = await new PositionRepository(_factory.Database.ConnectionString).UpsertAsync(new Position
        {
            AccountId = accountId,
            InstrumentId = instrumentId,
            Quantity = 10,
            AvgPurchasePrice = 1000m,
        });

        return (instrumentId, positionId, isin);
    }

    private async Task SeedBankEntryAsync(string isin, decimal bid, decimal offer)
    {
        await new BondUniverseRepository(_factory.Database.ConnectionString).UpsertSnapshotBatchAsync(new[]
        {
            new BondUniverseEntry
            {
                Secid = $"S{Guid.NewGuid():N}"[..10],
                Isin = isin,
                ShortName = "Bank bond",
                FaceValue = 1000m,
                Sector = "Корпоративные",
                YieldFraction = 0.12m,
                DurationYears = 1m,
                PricePercent = 100m,
                TurnoverRub = 1_000_000m,
                BidPercent = bid,
                OfferPercent = offer,
                NumTrades = 10,
                ListLevel = 1,
                IsFloater = false,
                MaturityDate = BusinessClock.MoscowToday().AddYears(1),
                UpdatedAt = DateTime.UtcNow,
            },
        });
    }

    // ─── Настройка горизонта ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetSettings_Default_ReplacementHorizonIsTwoYears()
    {
        var (client, _) = await CreateAuthorizedClientAsync();

        var body = await (await client.GetAsync("/api/settings")).Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("replacementHorizonYears").GetDecimal().Should().Be(2.0m);
    }

    [Fact]
    public async Task PutSettings_ReplacementHorizon_PersistsAndOtherFieldsUntouched()
    {
        var (client, _) = await CreateAuthorizedClientAsync();
        await client.PutAsJsonAsync("/api/settings", new { upcomingEventDaysThreshold = 21 });

        var put = await client.PutAsJsonAsync("/api/settings", new { replacementHorizonYears = 3.5m, commissionRateOverride = 0.0005m });

        put.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await (await client.GetAsync("/api/settings")).Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("replacementHorizonYears").GetDecimal().Should().Be(3.5m);
        body.GetProperty("upcomingEventDaysThreshold").GetInt32().Should().Be(21);

        // Следующий PUT без поля горизонта (например, только токен/порог) НЕ сбрасывает горизонт.
        await client.PutAsJsonAsync("/api/settings", new { upcomingEventDaysThreshold = 14 });
        var after = await (await client.GetAsync("/api/settings")).Content.ReadFromJsonAsync<JsonElement>();
        after.GetProperty("replacementHorizonYears").GetDecimal().Should().Be(3.5m);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.4)]
    [InlineData(10.5)]
    [InlineData(2.25)]
    [InlineData(-1.0)]
    public async Task PutSettings_InvalidReplacementHorizon_Returns422(double years)
    {
        var (client, _) = await CreateAuthorizedClientAsync();

        var response = await client.PutAsJsonAsync("/api/settings", new { replacementHorizonYears = years });

        response.StatusCode.Should().Be((HttpStatusCode)422);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("type").GetString().Should().Be("ValidationException");
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(2.0)]
    [InlineData(10.0)]
    public async Task PutSettings_BoundaryReplacementHorizon_Accepted(double years)
    {
        var (client, _) = await CreateAuthorizedClientAsync();

        var response = await client.PutAsJsonAsync("/api/settings", new { replacementHorizonYears = years });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ─── POST /api/analytics/replacement ────────────────────────────────────────────────────

    [Fact]
    public async Task PostReplacement_NoHorizonInRequest_UsesUserSetting()
    {
        var (client, accountId) = await CreateAuthorizedClientAsync();
        var (_, holdId, _) = await SeedPositionAsync(accountId, daysToMaturity: 365 * 5);
        var (_, targetId, _) = await SeedPositionAsync(accountId, daysToMaturity: 365 * 6);
        await client.PutAsJsonAsync("/api/settings", new { replacementHorizonYears = 3.5m });

        var response = await client.PostAsJsonAsync("/api/analytics/replacement", new { holdPositionId = holdId, targetPositionId = targetId });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("horizonYears").GetDecimal().Should().Be(3.5m);
        body.GetProperty("horizonSource").GetString().Should().Be("Settings");
        body.GetProperty("effectiveHorizonYears").GetDecimal().Should().Be(3.5m);
        body.GetProperty("horizonLimitedByTarget").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task PostReplacement_NoHorizonNoSetting_DefaultsToTwoYears()
    {
        var (client, accountId) = await CreateAuthorizedClientAsync();
        var (_, holdId, _) = await SeedPositionAsync(accountId, 365 * 5);
        var (_, targetId, _) = await SeedPositionAsync(accountId, 365 * 6);

        var body = await (await client.PostAsJsonAsync("/api/analytics/replacement", new { holdPositionId = holdId, targetPositionId = targetId }))
            .Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("horizonYears").GetDecimal().Should().Be(2.0m);
        body.GetProperty("horizonSource").GetString().Should().Be("Settings");
    }

    [Fact]
    public async Task PostReplacement_ExplicitHorizon_WinsOverSetting()
    {
        var (client, accountId) = await CreateAuthorizedClientAsync();
        var (_, holdId, _) = await SeedPositionAsync(accountId, 365 * 5);
        var (_, targetId, _) = await SeedPositionAsync(accountId, 365 * 6);
        await client.PutAsJsonAsync("/api/settings", new { replacementHorizonYears = 3.5m });

        var body = await (await client.PostAsJsonAsync("/api/analytics/replacement",
                new { holdPositionId = holdId, targetPositionId = targetId, horizonYears = 1.5m }))
            .Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("horizonYears").GetDecimal().Should().Be(1.5m);
        body.GetProperty("horizonSource").GetString().Should().Be("Request");
    }

    [Fact]
    public async Task PostReplacement_NonPositiveExplicitHorizon_Returns400()
    {
        var (client, accountId) = await CreateAuthorizedClientAsync();
        var (_, holdId, _) = await SeedPositionAsync(accountId, 365 * 5);
        var (_, targetId, _) = await SeedPositionAsync(accountId, 365 * 6);

        var response = await client.PostAsJsonAsync("/api/analytics/replacement",
            new { holdPositionId = holdId, targetPositionId = targetId, horizonYears = 0 });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, "ValidationException -> 400 (существующее поведение)");
    }

    [Fact]
    public async Task PostReplacement_TargetMaturesIn292Days_HorizonCutAndFlagged()
    {
        var (client, accountId) = await CreateAuthorizedClientAsync();
        var (_, holdId, _) = await SeedPositionAsync(accountId, 365 * 5);
        var (_, targetId, _) = await SeedPositionAsync(accountId, daysToMaturity: 292);

        var response = await client.PostAsJsonAsync("/api/analytics/replacement",
            new { holdPositionId = holdId, targetPositionId = targetId, horizonYears = 2 });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("horizonYears").GetDecimal().Should().Be(2m);
        body.GetProperty("effectiveHorizonYears").GetDecimal().Should().BeApproximately(0.8m, 0.0001m);
        body.GetProperty("horizonLimitedByTarget").GetBoolean().Should().BeTrue();
        body.GetProperty("targetHorizonIsOffer").GetBoolean().Should().BeFalse("у цели нет оферты — горизонт обрезан погашением");
        body.GetProperty("targetHorizonDate").GetString().Should().Be(BusinessClock.MoscowToday().AddDays(292).ToString("yyyy-MM-dd"));
    }

    [Fact]
    public async Task PostReplacement_BankBookKnown_SlippageComputedFromHalfSpreads()
    {
        // hold: MV = 10 × 1000 = 10 000 ₽; bank bid/offer 99.8/100.2 → полуспред (100.2−99.8)/(100.2+99.8) = 0.002 → слиппедж продажи 20 ₽.
        // Комиссии явные 0.3%: продажа 30 ₽ → proceeds = 10 000 − 30 − 20 = 9 950 (журнала нет: налог не оценён → 0).
        // target: bid/offer 99/101 → полуспред 0.01 → слиппедж покупки = 9 950 × 0.01 = 99.5 ₽.
        var (client, accountId) = await CreateAuthorizedClientAsync();
        var (_, holdId, holdIsin) = await SeedPositionAsync(accountId, 365 * 5);
        var (_, targetId, targetIsin) = await SeedPositionAsync(accountId, 365 * 6);
        await SeedBankEntryAsync(holdIsin, 99.8m, 100.2m);
        await SeedBankEntryAsync(targetIsin, 99m, 101m);

        var response = await client.PostAsJsonAsync("/api/analytics/replacement", new
        {
            holdPositionId = holdId, targetPositionId = targetId, horizonYears = 2,
            sellCommissionRate = 0.003m, buyCommissionRate = 0.003m,
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("slippageUnknown").GetBoolean().Should().BeFalse();
        body.GetProperty("sellSlippageRub").GetDecimal().Should().BeApproximately(20m, 0.001m);
        body.GetProperty("proceedsRub").GetDecimal().Should().BeApproximately(9_950m, 0.001m);
        body.GetProperty("buySlippageRub").GetDecimal().Should().BeApproximately(99.5m, 0.001m);
        body.GetProperty("buyCommissionRub").GetDecimal().Should().BeApproximately(29.85m, 0.001m);
        body.GetProperty("capitalRub").GetDecimal().Should().BeApproximately(9_950m - 29.85m - 99.5m, 0.001m);
        body.GetProperty("couponTaxRate").GetDecimal().Should().Be(0.13m);
    }

    [Fact]
    public async Task PostReplacement_BankBookUnknown_FlagsSlippageUnknown_AndChargesNothing()
    {
        var (client, accountId) = await CreateAuthorizedClientAsync();
        var (_, holdId, _) = await SeedPositionAsync(accountId, 365 * 5);
        var (_, targetId, _) = await SeedPositionAsync(accountId, 365 * 6);

        var body = await (await client.PostAsJsonAsync("/api/analytics/replacement",
                new { holdPositionId = holdId, targetPositionId = targetId, horizonYears = 2 }))
            .Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("slippageUnknown").GetBoolean().Should().BeTrue();
        body.GetProperty("sellSlippageRub").GetDecimal().Should().Be(0m);
        body.GetProperty("buySlippageRub").GetDecimal().Should().Be(0m);
    }
}

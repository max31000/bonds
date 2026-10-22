using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bonds.Core.Models;
using Bonds.Infrastructure.Repositories;
using Bonds.IntegrationTests.Infrastructure;
using FluentAssertions;
using Xunit;

namespace Bonds.IntegrationTests;

/// <summary>
/// Задача 57: фонды в композиции и сценариях ставок. Счёт: облигация (стоимость 10 150), фонд OFZ-направленности «с индексом»
/// (50 паёв × 200 = 10 000, явный benchmark — уникальный код индекса теста, Маколей 4.0 г., доходность 0.16) и фонд без индекса/дюрации
/// (100 паёв × 101 = 10 100). Модифицированная дюрация 1-го = 4 / 1.08 = 3.7037 г.
/// </summary>
[Collection("Integration")]
public class FundsAnalyticsEndpointTests
{
    private readonly TestWebApplicationFactory _factory;

    public FundsAnalyticsEndpointTests(TestWebApplicationFactory factory) => _factory = factory;

    private string Conn => _factory.Database.ConnectionString;

    private const decimal BondsValue = 10_150m;
    private const decimal IndexedFundValue = 10_000m;
    private const decimal PlainFundValue = 10_100m;
    private const decimal ExpectedModDuration = 4m / 1.08m;

    private record Seeded(HttpClient Client, ulong IndexedFundId, ulong PlainFundId, string IndexCode);

    private async Task<Seeded> SeedAsync()
    {
        var telegramId = Random.Shared.NextInt64(1, long.MaxValue);
        var userId = await new UserRepository(Conn).CreateAsync(new User { TelegramId = telegramId });
        var accountId = await new AccountRepository(Conn).CreateAsync(new Account { UserId = userId, Name = "Основной счёт" });
        var client = _factory.CreateClient();
        Mvv42TestAuth.Authorize(client, telegramId);

        var isin = $"RU{Guid.NewGuid():N}"[..12];
        var bondId = await new InstrumentRepository(Conn).UpsertAsync(new Instrument
        {
            Isin = isin, Secid = isin, Name = "Облигация", Issuer = "Тест", FaceValue = 1000m, Currency = "RUB",
            CouponType = CouponType.Fixed, MaturityDate = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(3)),
        });
        await new PositionRepository(Conn).UpsertAsync(new Position { AccountId = accountId, InstrumentId = bondId, Quantity = 10, AvgPurchasePrice = 1000m, Accrued = 5m });
        await new MarketQuoteRepository(Conn).UpsertAsync(new MarketQuote
        {
            InstrumentId = bondId, AsOf = DateOnly.FromDateTime(DateTime.UtcNow), CleanPrice = 1010m, DirtyPrice = 1015m, Accrued = 5m, Source = MarketQuoteSource.Moex,
        });

        // Уникальный код индекса: контейнер общий, RGBI могут править другие тесты.
        var indexCode = "IX" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        var funds = new FundRepository(Conn);
        string Suffix() => Guid.NewGuid().ToString("N")[..10];
        var s1 = Suffix();
        var indexedId = await funds.UpsertAsync(new Fund { Figi = $"FIGI{s1}", Isin = $"RU{s1}", Ticker = "TOFZ", Name = "Облигации ОФЗ", FocusType = "fixed_income" });
        await funds.UpdateSettingsAsync(indexedId, FundForecastMethod.Auto, indexCode);
        var s2 = Suffix();
        var plainId = await funds.UpsertAsync(new Fund { Figi = $"FIGI{s2}", Isin = $"RU{s2}", Ticker = "TPAY", Name = "Пассивный доход", FocusType = "fixed_income" });

        var positions = new FundPositionRepository(Conn);
        await positions.UpsertAsync(new FundPosition { AccountId = accountId, FundId = indexedId, Quantity = 50, AvgPurchasePriceRub = 190m, CurrentPriceRub = 200m, PriceAsOf = DateTime.UtcNow });
        await positions.UpsertAsync(new FundPosition { AccountId = accountId, FundId = plainId, Quantity = 100, AvgPurchasePriceRub = 100m, CurrentPriceRub = 101m, PriceAsOf = DateTime.UtcNow });

        await new FundMarketDataRepository(Conn).UpsertIndexHistoryAsync(indexCode,
            [new MarketIndexPoint(DateOnly.FromDateTime(DateTime.UtcNow), 100m, 0.16m, 4m)]);

        return new Seeded(client, indexedId, plainId, indexCode);
    }

    private static async Task<JsonElement> GetJson(HttpClient c, string url)
    {
        var r = await c.GetAsync(url);
        r.StatusCode.Should().Be(HttpStatusCode.OK);
        return await r.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task RateScenario_DefaultIncludesFunds_WithDurationSourceAndContribution()
    {
        var s = await SeedAsync();

        var body = await GetJson(s.Client, "/api/analytics/rate-scenario");

        body.GetProperty("fundsIncluded").GetBoolean().Should().BeTrue();
        body.GetProperty("hasFunds").GetBoolean().Should().BeTrue();
        body.GetProperty("currentValueRub").GetDecimal().Should().Be(BondsValue + IndexedFundValue + PlainFundValue);

        var funds = body.GetProperty("funds").EnumerateArray().ToList();
        funds.Should().HaveCount(2);
        var indexed = funds.Single(f => f.GetProperty("fundId").GetUInt64() == s.IndexedFundId);
        indexed.GetProperty("durationSource").GetString().Should().Be($"index:{s.IndexCode}");
        indexed.GetProperty("modifiedDurationYears").GetDecimal().Should().BeApproximately(ExpectedModDuration, 0.0001m);
        indexed.GetProperty("deltaAtPlus100bpRub").GetDecimal().Should().BeApproximately(-ExpectedModDuration * 0.01m * IndexedFundValue, 0.01m);

        var plain = funds.Single(f => f.GetProperty("fundId").GetUInt64() == s.PlainFundId);
        plain.GetProperty("durationSource").GetString().Should().Be("unknown");
        plain.GetProperty("modifiedDurationYears").ValueKind.Should().Be(JsonValueKind.Null);
        plain.GetProperty("deltaAtPlus100bpRub").ValueKind.Should().Be(JsonValueKind.Null);

        // Инвариант NewValue == CurrentValue + Δ для каждой точки; вклад фонда присутствует в Δ.
        var current = body.GetProperty("currentValueRub").GetDecimal();
        foreach (var p in body.GetProperty("scenarios").EnumerateArray())
            p.GetProperty("newValueRub").GetDecimal().Should().Be(current + p.GetProperty("deltaRub").GetDecimal());
        var plus100 = body.GetProperty("scenarios").EnumerateArray().Single(p => p.GetProperty("shiftBp").GetInt32() == 100);
        plus100.GetProperty("deltaRub").GetDecimal().Should().BeLessThanOrEqualTo(indexed.GetProperty("deltaAtPlus100bpRub").GetDecimal());
    }

    [Fact]
    public async Task RateScenario_IncludeFundsFalse_IsBondsOnly()
    {
        var s = await SeedAsync();

        var body = await GetJson(s.Client, "/api/analytics/rate-scenario?includeFunds=false");

        body.GetProperty("fundsIncluded").GetBoolean().Should().BeFalse();
        body.GetProperty("hasFunds").GetBoolean().Should().BeTrue("переключатель на фронте нужен и в режиме «только облигации»");
        body.GetProperty("funds").GetArrayLength().Should().Be(0);
        body.GetProperty("currentValueRub").GetDecimal().Should().Be(BondsValue);
    }

    [Fact]
    public async Task Composition_DefaultIncludesFunds_AssetClassAndLabels()
    {
        var s = await SeedAsync();

        var body = await GetJson(s.Client, "/api/analytics/composition");

        body.GetProperty("fundsIncluded").GetBoolean().Should().BeTrue();
        body.GetProperty("totalMarketValueRub").GetDecimal().Should().Be(BondsValue + IndexedFundValue + PlainFundValue);

        var assetClass = body.GetProperty("byAssetClass").EnumerateArray().ToDictionary(x => x.GetProperty("key").GetString()!, x => x.GetProperty("marketValueRub").GetDecimal());
        assetClass["Облигации"].Should().Be(BondsValue);
        assetClass["Фонды"].Should().Be(IndexedFundValue + PlainFundValue);

        foreach (var dim in new[] { "byIssuer", "bySector", "byCouponType", "byDurationBucket", "byAssetClass" })
            body.GetProperty(dim).EnumerateArray().Sum(x => x.GetProperty("sharePercent").GetDecimal()).Should().Be(100m, dim);

        body.GetProperty("byIssuer").EnumerateArray().Select(x => x.GetProperty("key").GetString())
            .Should().Contain(["Фонд: Облигации ОФЗ", "Фонд: Пассивный доход"]);
        // Дюрация 3.70 -> корзина «3–5 лет»; фонд без дюрации -> «Не определено».
        body.GetProperty("byDurationBucket").EnumerateArray().Select(x => x.GetProperty("key").GetString())
            .Should().Contain("3–5 лет");
    }

    [Fact]
    public async Task Composition_IncludeFundsFalse_IsBondsOnly_WithEmptyAssetClass()
    {
        var s = await SeedAsync();

        var body = await GetJson(s.Client, "/api/analytics/composition?includeFunds=false");

        body.GetProperty("fundsIncluded").GetBoolean().Should().BeFalse();
        body.GetProperty("byAssetClass").GetArrayLength().Should().Be(0);
        body.GetProperty("totalMarketValueRub").GetDecimal().Should().Be(BondsValue);
        body.GetProperty("byIssuer").EnumerateArray().Select(x => x.GetProperty("key").GetString())
            .Should().NotContain(k => k!.StartsWith("Фонд"));
        body.GetProperty("byIssuer").EnumerateArray().Sum(x => x.GetProperty("sharePercent").GetDecimal()).Should().Be(100m);
    }

    [Fact]
    public async Task PutSettings_DurationOverride_PersistsAndOverridesIndexEstimate_ResetFallsBack()
    {
        var s = await SeedAsync();
        var url = $"/api/funds/{s.IndexedFundId}/settings";

        var put = await s.Client.PutAsJsonAsync(url, new { forecastMethod = "auto", benchmarkIndex = s.IndexCode, durationOverrideYears = 1.5m });
        put.StatusCode.Should().Be(HttpStatusCode.OK);
        var putBody = await put.Content.ReadFromJsonAsync<JsonElement>();
        putBody.GetProperty("durationOverrideYears").GetDecimal().Should().Be(1.5m);
        putBody.GetProperty("durationEstimate").GetProperty("source").GetString().Should().Be("manual");

        var detail = await GetJson(s.Client, $"/api/funds/{s.IndexedFundId}");
        detail.GetProperty("settings").GetProperty("durationOverrideYears").GetDecimal().Should().Be(1.5m);
        detail.GetProperty("settings").GetProperty("durationEstimate").GetProperty("modifiedDurationYears").GetDecimal().Should().Be(1.5m);
        detail.GetProperty("fund").GetProperty("durationEstimate").GetProperty("source").GetString().Should().Be("manual");

        var scenario = await GetJson(s.Client, "/api/analytics/rate-scenario");
        scenario.GetProperty("funds").EnumerateArray().Single(f => f.GetProperty("fundId").GetUInt64() == s.IndexedFundId)
            .GetProperty("durationSource").GetString().Should().Be("manual");

        // Сброс (null) возвращает оценку по индексу.
        var reset = await s.Client.PutAsJsonAsync(url, new { forecastMethod = "auto", benchmarkIndex = s.IndexCode, durationOverrideYears = (decimal?)null });
        reset.StatusCode.Should().Be(HttpStatusCode.OK);
        var after = await GetJson(s.Client, $"/api/funds/{s.IndexedFundId}");
        after.GetProperty("settings").GetProperty("durationOverrideYears").ValueKind.Should().Be(JsonValueKind.Null);
        after.GetProperty("fund").GetProperty("durationEstimate").GetProperty("source").GetString().Should().Be($"index:{s.IndexCode}");
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(30.01)]
    [InlineData(100)]
    public async Task PutSettings_DurationOverrideOutOfRange_Returns422(double years)
    {
        var s = await SeedAsync();
        var response = await s.Client.PutAsJsonAsync($"/api/funds/{s.PlainFundId}/settings", new { forecastMethod = "auto", durationOverrideYears = years });
        response.StatusCode.Should().Be((HttpStatusCode)422);
    }
}

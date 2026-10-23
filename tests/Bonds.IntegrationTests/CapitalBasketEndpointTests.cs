using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bonds.Core.Models;
using Bonds.Infrastructure.Repositories;
using Bonds.IntegrationTests.Infrastructure;
using FluentAssertions;
using Xunit;
using BrokerIds = Bonds.Core.Models.Brokers;

namespace Bonds.IntegrationTests;

/// <summary>
/// Задача 62: корзины капитала на реальном MySQL — посев стартовых корзин при первом GET (идемпотентно), отклонения rub (фонды + валюта +
/// свободные рубли) и usd (Freedom в долларах), валидация (422: сумма целей, band &lt; hard_band, брокер/дубликат члена), пакетная смена целей,
/// сброс к умолчаниям, изоляция пользователей. Все веса в ответе — ДОЛИ.
/// </summary>
[Collection("Integration")]
public class CapitalBasketEndpointTests
{
    private const decimal UsdRate = 80m;
    private const decimal EurRate = 90m;

    private readonly TestWebApplicationFactory _factory;

    public CapitalBasketEndpointTests(TestWebApplicationFactory factory) => _factory = factory;

    private string Conn => _factory.Database.ConnectionString;

    private static DateOnly AgoDate(int days) => DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-days));

    private async Task<(HttpClient Client, ulong UserId)> NewUserAsync()
    {
        var telegramId = Random.Shared.NextInt64(1, long.MaxValue);
        var userId = await new UserRepository(Conn).CreateAsync(new User { TelegramId = telegramId });
        var client = _factory.CreateClient();
        Mvv42TestAuth.Authorize(client, telegramId);
        return (client, userId);
    }

    private async Task SeedRatesAsync()
    {
        var currencies = new CurrencyRepository(Conn);
        var market = new CurrencyMarketDataRepository(Conn);
        foreach (var (iso, rate) in new[] { ("USD", UsdRate), ("EUR", EurRate) })
        {
            var id = (await currencies.GetByIsoAsync(iso))?.Id
                     ?? await currencies.UpsertAsync(new Currency { Figi = "FX:" + iso, IsoCode = iso, Name = iso });
            await market.UpsertRatesAsync(id, [new CurrencyRatePoint(AgoDate(1), rate), new CurrencyRatePoint(AgoDate(0), rate)]);
        }
    }

    private static JsonElement Portfolio(JsonElement body, string portfolio) =>
        body.GetProperty("portfolios").EnumerateArray().Single(p => p.GetProperty("portfolio").GetString() == portfolio);

    private static JsonElement Basket(JsonElement portfolio, string code) =>
        portfolio.GetProperty("baskets").EnumerateArray().Single(b => b.GetProperty("code").GetString() == code);

    private static async Task<JsonElement> GetAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/baskets");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static object UpdateBody(
        string title, decimal? target, decimal band = 0.05m, decimal hard = 0.10m, string fillRule = "fixed", bool autoBuy = false) =>
        new { title, targetWeight = target, band, hardBand = hard, liquidityTier = 1, fillRule, autoBuy, autoSell = false, minTrade = 0m };

    // --- посев ---

    [Fact]
    public async Task Get_FirstCall_SeedsDefaultBaskets_AndSecondCallDoesNotDuplicate()
    {
        var (client, _) = await NewUserAsync();

        var first = await GetAsync(client);
        var second = await GetAsync(client);

        var rub = Portfolio(first, "rub");
        rub.GetProperty("currency").GetString().Should().Be("RUB");
        rub.GetProperty("hasAccount").GetBoolean().Should().BeFalse();
        rub.GetProperty("baskets").EnumerateArray().Where(b => !b.GetProperty("isSystem").GetBoolean())
            .Select(b => b.GetProperty("code").GetString()).Should().Equal("liquidity", "bonds", "ru_equity", "temporary");
        Basket(rub, "liquidity").GetProperty("targetWeight").GetDecimal().Should().Be(0.15m);
        Basket(rub, "liquidity").GetProperty("members").EnumerateArray().Select(m => m.GetProperty("instrumentKey").GetString())
            .Should().BeEquivalentTo("TMON@", "TSAV");
        Basket(rub, "temporary").GetProperty("targetWeight").ValueKind.Should().Be(JsonValueKind.Null);
        Portfolio(first, "usd").GetProperty("baskets").EnumerateArray().Where(b => !b.GetProperty("isSystem").GetBoolean())
            .Select(b => b.GetProperty("code").GetString()).Should().Equal("global_core", "satellites", "frozen");
        Portfolio(first, "usd").GetProperty("currency").GetString().Should().Be("USD");
        first.GetProperty("disclaimer").GetString().Should().Contain("не рекомендация");

        Portfolio(second, "rub").GetProperty("baskets").GetArrayLength().Should().Be(rub.GetProperty("baskets").GetArrayLength());
    }

    [Fact]
    public async Task Get_ConcurrentFirstCalls_SeedExactlyOnce()
    {
        var (client, userId) = await NewUserAsync();

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => client.GetAsync("/api/baskets")));

        (await new CapitalBasketRepository(Conn).GetByUserAsync(userId)).Should().HaveCount(7);
    }

    // --- отклонения ---

    [Fact]
    public async Task Get_RubDrift_FundsCurrencyAndFreeCash_BaseExcludesNoTargetBaskets()
    {
        var (client, userId) = await NewUserAsync();
        var accounts = new AccountRepository(Conn);
        var accountId = await accounts.CreateAsync(new Account { UserId = userId, Name = "Основной счёт", BrokerAccountId = "T-" + Guid.NewGuid().ToString("N")[..8] });
        // cash_rub включает валюту: 100 000 всего, из них юани 1000 × 12 = 12 000 → свободные рубли 88 000.
        await accounts.UpdateBalanceAsync(accountId, 100_000m, 300_000m, DateTime.UtcNow);

        var funds = new FundRepository(Conn);
        var sfx = Guid.NewGuid().ToString("N")[..8];
        var tmon = await funds.UpsertAsync(new Fund { Figi = $"FIGI1{sfx}", Isin = $"RU1{sfx}", Ticker = "TMON@", Name = "Т-Капитал Денежный рынок" });
        var tpay = await funds.UpsertAsync(new Fund { Figi = $"FIGI2{sfx}", Isin = $"RU2{sfx}", Ticker = "TPAY", Name = "Пассивный доход" });
        var positions = new FundPositionRepository(Conn);
        await positions.UpsertAsync(new FundPosition { AccountId = accountId, FundId = tmon, Quantity = 1000m, AvgPurchasePriceRub = 90m, CurrentPriceRub = 100m, PriceAsOf = DateTime.UtcNow });
        await positions.UpsertAsync(new FundPosition { AccountId = accountId, FundId = tpay, Quantity = 500m, AvgPurchasePriceRub = 190m, CurrentPriceRub = 200m, PriceAsOf = DateTime.UtcNow });

        var cnyId = await new CurrencyRepository(Conn).UpsertAsync(new Currency
        {
            Figi = $"FIGI3{sfx}", IsoCode = "T" + sfx[..6].ToUpperInvariant(), Ticker = "TEST_X_TOM", Name = "Юань", MoexSecid = "TEST_X_TOM",
        });
        await new CurrencyPositionRepository(Conn).UpsertAsync(new CurrencyPosition
        {
            AccountId = accountId, CurrencyId = cnyId, Quantity = 1000m, AvgPurchasePriceRub = 11m, CurrentPriceRub = 12m, PriceAsOf = DateTime.UtcNow,
        });

        var rub = Portfolio(await GetAsync(client), "rub");

        // Всего: TMON@ 100 000 + TPAY 100 000 + юани 12 000 + свободные рубли 88 000 = 300 000.
        // Вне целей: temporary (TPAY) 100 000 и fx 12 000 → база доли 188 000.
        rub.GetProperty("hasAccount").GetBoolean().Should().BeTrue();
        rub.GetProperty("totalValue").GetDecimal().Should().Be(300_000m);
        rub.GetProperty("baseValue").GetDecimal().Should().Be(188_000m);
        rub.GetProperty("freeCash").GetDecimal().Should().Be(88_000m);
        rub.GetProperty("targetsSum").GetDecimal().Should().Be(1m);

        var liquidity = Basket(rub, "liquidity");
        liquidity.GetProperty("value").GetDecimal().Should().Be(100_000m);
        liquidity.GetProperty("weight").GetDecimal().Should().BeApproximately(100_000m / 188_000m, 0.000001m);
        liquidity.GetProperty("drift").GetDecimal().Should().BeApproximately(100_000m / 188_000m - 0.15m, 0.000001m);
        liquidity.GetProperty("state").GetString().Should().Be("hardAbove");
        liquidity.GetProperty("deficitToTarget").GetDecimal().Should().BeApproximately(0.15m * 188_000m - 100_000m, 0.01m);
        liquidity.GetProperty("members").EnumerateArray().Single(m => m.GetProperty("instrumentKey").GetString() == "TMON@")
            .GetProperty("actualSubShare").GetDecimal().Should().Be(1m);

        var temporary = Basket(rub, "temporary");
        temporary.GetProperty("value").GetDecimal().Should().Be(100_000m);
        temporary.GetProperty("state").GetString().Should().Be("noTarget");
        temporary.GetProperty("drift").ValueKind.Should().Be(JsonValueKind.Null);

        var fx = Basket(rub, "fx");
        fx.GetProperty("isSystem").GetBoolean().Should().BeTrue();
        fx.GetProperty("value").GetDecimal().Should().Be(12_000m);
        Basket(rub, "cash").GetProperty("value").GetDecimal().Should().Be(88_000m);

        var sum = rub.GetProperty("baskets").EnumerateArray().Sum(b => b.GetProperty("value").GetDecimal());
        sum.Should().Be(300_000m, "каждая позиция и деньги — ровно в одной строке");
    }

    [Fact]
    public async Task Get_UsdPortfolio_FreedomValuedInDollars_ByBrokerRates()
    {
        await SeedRatesAsync();
        var (client, userId) = await NewUserAsync();
        var accountId = await new AccountRepository(Conn).CreateAsync(
            new Account { UserId = userId, Broker = BrokerIds.Freedom, BrokerAccountId = "F-" + Guid.NewGuid().ToString("N")[..8], Name = "Freedom" });
        var now = DateTime.UtcNow;
        var positions = new BrokerPositionRepository(Conn);
        await positions.UpsertAsync(new BrokerPosition { AccountId = accountId, Ticker = "VUAA.EU", Name = "Vanguard S&P 500 UCITS", AssetClass = "etf", Currency = "USD", Quantity = 10m, AvgPrice = 90m, LastPrice = 100m, MarketValue = 1000m, SyncedAt = now });
        await positions.UpsertAsync(new BrokerPosition { AccountId = accountId, Ticker = "LUV.US", Name = "Southwest", AssetClass = "stock", Currency = "USD", Quantity = 10m, AvgPrice = 40m, LastPrice = 50m, MarketValue = 500m, SyncedAt = now });
        await positions.UpsertAsync(new BrokerPosition { AccountId = accountId, Ticker = "ROBO.EU", Name = "L&G ROBO", AssetClass = "etf", Currency = "EUR", Quantity = 4m, AvgPrice = 20m, LastPrice = 25m, MarketValue = 100m, SyncedAt = now });
        var cash = new BrokerCashRepository(Conn);
        await cash.UpsertAsync(new BrokerCash { AccountId = accountId, Currency = "USD", Amount = 50m, SyncedAt = now });
        await cash.UpsertAsync(new BrokerCash { AccountId = accountId, Currency = "EUR", Amount = 10m, SyncedAt = now });

        var usd = Portfolio(await GetAsync(client), "usd");

        // ROBO.EU: 100 EUR × 90 ÷ 80 = 112.5 USD; деньги: 50 + 10 EUR × 90 ÷ 80 = 61.25 USD.
        // Всего: 1000 + 500 + 112.5 + 61.25 = 1673.75; спутники (LUV.US, ROBO.EU) вне целей: 612.5 → база 1061.25.
        usd.GetProperty("currency").GetString().Should().Be("USD");
        usd.GetProperty("totalValue").GetDecimal().Should().Be(1673.75m);
        usd.GetProperty("freeCash").GetDecimal().Should().Be(61.25m);
        usd.GetProperty("baseValue").GetDecimal().Should().Be(1061.25m);
        Basket(usd, "global_core").GetProperty("value").GetDecimal().Should().Be(1000m);
        Basket(usd, "satellites").GetProperty("value").GetDecimal().Should().Be(612.5m, "LUV → LUV.US, ROBO → ROBO.EU");
        Basket(usd, "global_core").GetProperty("weight").GetDecimal().Should().BeApproximately(1000m / 1061.25m, 0.000001m);
    }

    // --- редактирование и валидация ---

    private static async Task<(ulong Id, JsonElement Basket)> FindAsync(HttpClient client, string portfolio, string code)
    {
        var b = Basket(Portfolio(await GetAsync(client), portfolio), code);
        return (b.GetProperty("id").GetUInt64(), b);
    }

    [Fact]
    public async Task Put_TargetsNotSummingToOne_Returns422_AndNothingChanged()
    {
        var (client, _) = await NewUserAsync();
        var (id, _) = await FindAsync(client, "rub", "liquidity");

        var response = await client.PutAsJsonAsync($"/api/baskets/{id}", UpdateBody("Ликвидность", 0.30m)); // 0.30 + 0.85 = 1.15

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("type").GetString().Should().Be("ValidationException");
        Basket(Portfolio(await GetAsync(client), "rub"), "liquidity").GetProperty("targetWeight").GetDecimal().Should().Be(0.15m);
    }

    [Fact]
    public async Task PutBatch_RebalancesTargetsAtomically_AndChecksSumOnResultingState()
    {
        var (client, _) = await NewUserAsync();
        var (liquidity, _) = await FindAsync(client, "rub", "liquidity");
        var (bonds, _) = await FindAsync(client, "rub", "bonds");

        var bad = await client.PutAsJsonAsync("/api/baskets", new
        {
            items = new[] { new { id = liquidity, title = "Ликвидность", targetWeight = 0.20m, band = 0.05m, hardBand = 0.10m, liquidityTier = 0, fillRule = "fixed", autoBuy = true, autoSell = true, minTrade = 0m } },
        });
        bad.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, "0.20 + 0.85 ≠ 1");

        var ok = await client.PutAsJsonAsync("/api/baskets", new
        {
            items = new object[]
            {
                new { id = liquidity, title = "Ликвидность", targetWeight = 0.20m, band = 0.05m, hardBand = 0.10m, liquidityTier = 0, fillRule = "fixed", autoBuy = true, autoSell = true, minTrade = 1000m },
                new { id = bonds, title = "Облигации", targetWeight = 0.80m, band = 0.05m, hardBand = 0.10m, liquidityTier = 2, fillRule = "engine", autoBuy = false, autoSell = false, minTrade = 0m },
            },
        });
        ok.StatusCode.Should().Be(HttpStatusCode.OK);

        var rub = Portfolio(await ok.Content.ReadFromJsonAsync<JsonElement>(), "rub");
        Basket(rub, "liquidity").GetProperty("targetWeight").GetDecimal().Should().Be(0.20m);
        Basket(rub, "liquidity").GetProperty("minTrade").GetDecimal().Should().Be(1000m);
        Basket(rub, "bonds").GetProperty("targetWeight").GetDecimal().Should().Be(0.80m);
        rub.GetProperty("targetsSum").GetDecimal().Should().Be(1m);
    }

    [Fact]
    public async Task Put_FlagsOnly_DoesNotRequireTargetsToSumToOne_EvenWhenStartingUsdTargetsDoNot()
    {
        var (client, _) = await NewUserAsync();
        var (id, _) = await FindAsync(client, "usd", "global_core"); // стартовая цель usd 0.6, Σ = 0.6

        var response = await client.PutAsJsonAsync($"/api/baskets/{id}", UpdateBody("Глобальное ядро", 0.6m, autoBuy: true));

        response.StatusCode.Should().Be(HttpStatusCode.OK, "цель не менялась — сумма не проверяется");
        Basket(Portfolio(await response.Content.ReadFromJsonAsync<JsonElement>(), "usd"), "global_core").GetProperty("autoBuy").GetBoolean().Should().BeTrue();
    }

    [Theory]
    [InlineData(0.10, 0.10)]
    [InlineData(0.20, 0.10)]
    public async Task Put_BandNotBelowHardBand_Returns422(double band, double hard)
    {
        var (client, _) = await NewUserAsync();
        var (id, _) = await FindAsync(client, "rub", "liquidity");

        var response = await client.PutAsJsonAsync($"/api/baskets/{id}", UpdateBody("Ликвидность", 0.15m, (decimal)band, (decimal)hard));

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Put_ForeignUsersBasket_Returns404()
    {
        var (owner, _) = await NewUserAsync();
        var (id, _) = await FindAsync(owner, "rub", "liquidity");
        var (stranger, _) = await NewUserAsync();
        await GetAsync(stranger);

        var response = await stranger.PutAsJsonAsync($"/api/baskets/{id}", UpdateBody("Чужая", 0.15m));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        Basket(Portfolio(await GetAsync(owner), "rub"), "liquidity").GetProperty("title").GetString().Should().Be("Ликвидность");
    }

    // --- члены ---

    [Fact]
    public async Task Members_AddDuplicateAcrossBaskets_Returns422_AndDeleteWorks()
    {
        var (client, _) = await NewUserAsync();
        var (temporary, _) = await FindAsync(client, "rub", "temporary");
        var (liquidity, _) = await FindAsync(client, "rub", "liquidity");

        var added = await client.PostAsJsonAsync($"/api/baskets/{temporary}/members", new { broker = "tinvest", instrumentKey = "TBRU@", matchRule = "instrument" });
        added.StatusCode.Should().Be(HttpStatusCode.Created);
        var memberId = (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetUInt64();

        // тот же тикер в другой корзине (даже в другом написании) — нарушение «ровно в одной корзине»
        var dup = await client.PostAsJsonAsync($"/api/baskets/{liquidity}/members", new { broker = "tinvest", instrumentKey = "tbru", matchRule = "instrument" });
        dup.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        var wrongBroker = await client.PostAsJsonAsync($"/api/baskets/{liquidity}/members", new { broker = "freedom", instrumentKey = "LUV.US" });
        wrongBroker.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        var badWeight = await client.PostAsJsonAsync($"/api/baskets/{liquidity}/members", new { broker = "tinvest", instrumentKey = "LQDT", subWeight = 1.5m });
        badWeight.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        (await client.DeleteAsync($"/api/baskets/{temporary}/members/{memberId}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.DeleteAsync($"/api/baskets/{temporary}/members/{memberId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // освободившийся тикер можно положить в другую корзину
        (await client.PostAsJsonAsync($"/api/baskets/{liquidity}/members", new { broker = "tinvest", instrumentKey = "TBRU@", subWeight = 0.1m }))
            .StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Members_ForeignUsersBasket_Returns404()
    {
        var (owner, _) = await NewUserAsync();
        var (id, _) = await FindAsync(owner, "rub", "temporary");
        var (stranger, _) = await NewUserAsync();

        (await stranger.PostAsJsonAsync($"/api/baskets/{id}/members", new { broker = "tinvest", instrumentKey = "XXXX" })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await stranger.DeleteAsync($"/api/baskets/{id}/members/1")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // --- сброс ---

    [Fact]
    public async Task ResetDefaults_RestoresStartingBaskets_AndKeepsOtherPortfolioWhenScoped()
    {
        var (client, _) = await NewUserAsync();
        var (liquidity, _) = await FindAsync(client, "rub", "liquidity");
        var (bonds, _) = await FindAsync(client, "rub", "bonds");
        await client.PutAsJsonAsync("/api/baskets", new
        {
            items = new object[]
            {
                new { id = liquidity, title = "Моя", targetWeight = 0.30m, band = 0.05m, hardBand = 0.10m, liquidityTier = 0, fillRule = "fixed", autoBuy = true, autoSell = true, minTrade = 0m },
                new { id = bonds, title = "Облигации", targetWeight = 0.70m, band = 0.05m, hardBand = 0.10m, liquidityTier = 2, fillRule = "engine", autoBuy = false, autoSell = false, minTrade = 0m },
            },
        });
        var (core, _) = await FindAsync(client, "usd", "global_core");
        await client.PutAsJsonAsync($"/api/baskets/{core}", UpdateBody("Моё ядро", 0.6m));

        var reset = await client.PostAsync("/api/baskets/reset-defaults?portfolio=rub", null);

        reset.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await reset.Content.ReadFromJsonAsync<JsonElement>();
        Basket(Portfolio(body, "rub"), "liquidity").GetProperty("title").GetString().Should().Be("Ликвидность");
        Basket(Portfolio(body, "rub"), "liquidity").GetProperty("targetWeight").GetDecimal().Should().Be(0.15m);
        Basket(Portfolio(body, "usd"), "global_core").GetProperty("title").GetString().Should().Be("Моё ядро", "usd не сбрасывался");

        (await client.PostAsync("/api/baskets/reset-defaults", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        Basket(Portfolio(await GetAsync(client), "usd"), "global_core").GetProperty("title").GetString().Should().Be("Глобальное ядро");
        (await client.PostAsync("/api/baskets/reset-defaults?portfolio=eur", null)).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }
}

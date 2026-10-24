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
        string title, decimal? target, decimal band = 0.05m, decimal hard = 0.10m, string fillRule = "fixed", bool autoBuy = false, bool autoSell = false) =>
        new { title, targetWeight = target, band, hardBand = hard, liquidityTier = 1, fillRule, autoBuy, autoSell, minTrade = 0m };

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

    [Fact]
    public async Task SeedIfEmpty_RepeatedAndConcurrent_SeedsOnceAndReturnsFalseForLosers()
    {
        var (_, userId) = await NewUserAsync();
        var repo = new CapitalBasketRepository(Conn);
        var defaults = Bonds.Core.Capital.DefaultBaskets.All(userId);

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => repo.SeedIfEmptyAsync(userId, defaults)));

        results.Count(r => r).Should().Be(1, "посев выполняет ровно один вызов; остальные (дубль ключа/дедлок) откатываются и перечитывают");
        (await repo.GetByUserAsync(userId)).Should().HaveCount(7);
        (await repo.SeedIfEmptyAsync(userId, defaults)).Should().BeFalse();
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
        Basket(usd, "global_core").GetProperty("target").GetDecimal().Should().Be(1m);
        Basket(usd, "global_core").GetProperty("drift").GetDecimal().Should().BeApproximately(1000m / 1061.25m - 1m, 0.000001m);
        usd.GetProperty("targetsSum").GetDecimal().Should().Be(1m, "стартовые цели usd: ядро 1.0, остальные вне целей");

        // Голый член «LUV» получает значение и фактическую долю так же, как при назначении позиций (LUV → LUV.US).
        var luv = Basket(usd, "satellites").GetProperty("members").EnumerateArray().Single(m => m.GetProperty("instrumentKey").GetString() == "LUV");
        luv.GetProperty("value").GetDecimal().Should().Be(500m);
        luv.GetProperty("actualSubShare").GetDecimal().Should().BeApproximately(500m / 612.5m, 0.000001m);
    }

    [Fact]
    public async Task Get_UsdPortfolio_NoPositions_TargetBasketIsNoData()
    {
        var (client, userId) = await NewUserAsync();
        await new AccountRepository(Conn).CreateAsync(
            new Account { UserId = userId, Broker = BrokerIds.Freedom, BrokerAccountId = "F-" + Guid.NewGuid().ToString("N")[..8], Name = "Freedom" });

        var core = Basket(Portfolio(await GetAsync(client), "usd"), "global_core");

        core.GetProperty("state").GetString().Should().Be("noData");
        core.GetProperty("drift").ValueKind.Should().Be(JsonValueKind.Null);
        core.GetProperty("deficitToTarget").ValueKind.Should().Be(JsonValueKind.Null);
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
    public async Task Put_FlagsOnly_DoesNotRecheckTargetsSum()
    {
        var (client, _) = await NewUserAsync();
        var (id, _) = await FindAsync(client, "rub", "liquidity"); // стартовая цель 0.15, авто-флаги включены

        var response = await client.PutAsJsonAsync($"/api/baskets/{id}", UpdateBody("Ликвидность", 0.15m, autoBuy: false));

        response.StatusCode.Should().Be(HttpStatusCode.OK, "цель не менялась — сумма не проверяется");
        Basket(Portfolio(await response.Content.ReadFromJsonAsync<JsonElement>(), "rub"), "liquidity").GetProperty("autoBuy").GetBoolean().Should().BeFalse();
    }

    [Theory]
    [InlineData("usd", "global_core", 1.0, true, false)]
    [InlineData("rub", "ru_equity", 0.0, false, true)]
    [InlineData("rub", "bonds", 0.85, true, true)]
    public async Task T67_Put_AutoFlagsOnNonLiquidityBasket_Returns422_AndNothingChanged(string portfolio, string code, double target, bool autoBuy, bool autoSell)
    {
        var (client, _) = await NewUserAsync();
        var (id, _) = await FindAsync(client, portfolio, code);

        var response = await client.PutAsJsonAsync($"/api/baskets/{id}", UpdateBody(code, (decimal)target, autoBuy: autoBuy, autoSell: autoSell));
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, "авто-исполнение разрешено кодом только для корзины liquidity");

        var batch = await client.PutAsJsonAsync("/api/baskets", new
        {
            items = new[] { new { id, title = code, targetWeight = (decimal)target, band = 0.05m, hardBand = 0.10m, liquidityTier = 1, fillRule = "fixed", autoBuy, autoSell, minTrade = 0m } },
        });
        batch.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        var after = Basket(Portfolio(await GetAsync(client), portfolio), code);
        after.GetProperty("autoBuy").GetBoolean().Should().BeFalse();
        after.GetProperty("autoSell").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task T67A2_Put_LegacyAutoTrueOnNonLiquidity_PassesUnchanged_ButTurningOnIs422()
    {
        // Решение владельца (A2, c): 422 — только когда запрос ВКЛЮЧАЕТ авто-флаг у корзины не «liquidity». Унаследованное true (до задачи 67) проходит
        // как есть — правка заголовка/полосы не должна требовать сначала выключить флаг.
        var (client, userId) = await NewUserAsync();
        var (id, _) = await FindAsync(client, "usd", "global_core");
        await using (var conn = new MySqlConnector.MySqlConnection(Conn))
            await Dapper.SqlMapper.ExecuteAsync(conn, "UPDATE baskets SET auto_buy = 1, auto_sell = 0 WHERE id = @Id AND user_id = @UserId", new { Id = id, UserId = userId });

        var keep = await client.PutAsJsonAsync($"/api/baskets/{id}", UpdateBody("Ядро (переименовано)", 1.0m, autoBuy: true, autoSell: false));
        keep.StatusCode.Should().Be(HttpStatusCode.OK, "autoBuy = true не меняется — legacy-значение проходит");
        var kept = Basket(Portfolio(await keep.Content.ReadFromJsonAsync<JsonElement>(), "usd"), "global_core");
        kept.GetProperty("title").GetString().Should().Be("Ядро (переименовано)");
        kept.GetProperty("autoBuy").GetBoolean().Should().BeTrue();

        var turnOn = await client.PutAsJsonAsync($"/api/baskets/{id}", UpdateBody("Ядро", 1.0m, autoBuy: true, autoSell: true));
        turnOn.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, "autoSell false → true на корзине не liquidity");

        var off = await client.PutAsJsonAsync($"/api/baskets/{id}", UpdateBody("Ядро", 1.0m, autoBuy: false, autoSell: false));
        off.StatusCode.Should().Be(HttpStatusCode.OK, "выключить флаг можно всегда");
    }

    [Fact]
    public async Task Put_UsdPortfolio_EditBand_Succeeds_AndChangingTargetAwayFromOneIs422()
    {
        var (client, _) = await NewUserAsync();
        var (id, _) = await FindAsync(client, "usd", "global_core");

        var ok = await client.PutAsJsonAsync($"/api/baskets/{id}", UpdateBody("Глобальное ядро", 1.0m, band: 0.03m, hard: 0.08m));
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        var core = Basket(Portfolio(await ok.Content.ReadFromJsonAsync<JsonElement>(), "usd"), "global_core");
        core.GetProperty("band").GetDecimal().Should().Be(0.03m);
        core.GetProperty("hardBand").GetDecimal().Should().Be(0.08m);

        // единственная корзина с целью: 0.5 даёт Σ = 0.5 ≠ 1
        (await client.PutAsJsonAsync($"/api/baskets/{id}", UpdateBody("Глобальное ядро", 0.5m))).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        Basket(Portfolio(await GetAsync(client), "usd"), "global_core").GetProperty("targetWeight").GetDecimal().Should().Be(1.0m);
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

    [Theory]
    [InlineData("rub", "tinvest", "temporary", "TMON")]      // TMON@ уже в liquidity
    [InlineData("usd", "freedom", "frozen", "UFO.US")]       // голый «UFO» уже в satellites
    public async Task Members_BareKeyConflictsWithSuffixedForm_Returns422(string portfolio, string broker, string basketCode, string key)
    {
        var (client, _) = await NewUserAsync();
        var (id, _) = await FindAsync(client, portfolio, basketCode);

        var response = await client.PostAsJsonAsync($"/api/baskets/{id}/members", new { broker, instrumentKey = key });

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
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
        await client.PutAsJsonAsync($"/api/baskets/{core}", UpdateBody("Моё ядро", 1.0m));

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

    [Fact]
    public async Task T67_UpdateLocked_SelectForUpdate_SecondWriterWaitsAndValidatesOnCommittedState()
    {
        // Задача 67 (#13): PUT /api/baskets читает, проверяет и пишет одной транзакцией с SELECT … FOR UPDATE. Второй запрос ждёт коммита первого и
        // видит его цели (а не старое состояние), поэтому два «по отдельности правильных» PUT не дают вместе сумму целей ≠ 1.
        var (client, userId) = await NewUserAsync();
        await GetAsync(client); // посев стартовых корзин
        var repo = new CapitalBasketRepository(Conn);

        var firstInside = new TaskCompletionSource();
        var releaseFirst = new TaskCompletionSource();
        var first = repo.UpdateLockedAsync<int>(userId, async all =>
        {
            var liquidity = all.Single(b => b.Code == "liquidity");
            liquidity.TargetWeight = 0.10m;
            firstInside.SetResult();
            await releaseFirst.Task;
            return ([liquidity], 1);
        });
        await firstInside.Task;

        decimal? seenBySecond = null;
        var second = Task.Run(() => repo.UpdateLockedAsync<int>(userId, all =>
        {
            seenBySecond = all.Single(b => b.Code == "liquidity").TargetWeight;
            return Task.FromResult<(IReadOnlyList<Bonds.Core.Capital.CapitalBasket>?, int)>((null, 2));
        }));

        await Task.Delay(500);
        second.IsCompleted.Should().BeFalse("строки корзин заблокированы первым запросом до его коммита");
        releaseFirst.SetResult();
        (await first).Should().Be(1);
        (await second).Should().Be(2);
        seenBySecond.Should().Be(0.10m, "второй проверяет уже итоговое состояние первого");
    }

    [Fact]
    public async Task T67_ConcurrentPuts_EachValidAlone_DoNotLeaveTargetsSumBroken()
    {
        // Сценарий аудита: A меняет liquidity 0.15 → 0.10 и ru_equity → 0.05, B — bonds 0.85 → 0.80 и ru_equity → 0.05. Каждый по отдельности держит сумму 1;
        // без блокировки оба прошли бы проверку на старом состоянии и записали бы вместе 0.95. С FOR UPDATE второй получает 422.
        var (client, _) = await NewUserAsync();
        var (liquidity, _) = await FindAsync(client, "rub", "liquidity");
        var (bonds, _) = await FindAsync(client, "rub", "bonds");
        var (equity, _) = await FindAsync(client, "rub", "ru_equity");
        object Item(ulong id, string title, decimal target, string fill) =>
            new { id, title, targetWeight = target, band = 0.05m, hardBand = 0.10m, liquidityTier = 1, fillRule = fill, autoBuy = false, autoSell = false, minTrade = 0m };

        var a = client.PutAsJsonAsync("/api/baskets", new { items = new[] { Item(liquidity, "Ликвидность", 0.10m, "fixed"), Item(equity, "Акции РФ", 0.05m, "fixed") } });
        var b = client.PutAsJsonAsync("/api/baskets", new { items = new[] { Item(bonds, "Облигации", 0.80m, "engine"), Item(equity, "Акции РФ", 0.05m, "fixed") } });
        var codes = (await Task.WhenAll(a, b)).Select(r => r.StatusCode).ToList();

        codes.Should().Contain(HttpStatusCode.OK);
        var rub = Portfolio(await GetAsync(client), "rub");
        rub.GetProperty("targetsSum").GetDecimal().Should().Be(1.0m, "итоговое состояние всегда проходит проверку суммы целей");
    }
}

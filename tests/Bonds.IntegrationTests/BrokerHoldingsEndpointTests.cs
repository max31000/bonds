using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bonds.Core.Credentials;
using Bonds.Core.Models;
using Bonds.Infrastructure.Connectors.Freedom;
using Bonds.Infrastructure.Connectors.Freedom.Models;
using Bonds.Infrastructure.Repositories;
using Bonds.Infrastructure.Sync;
using Bonds.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Bonds.IntegrationTests;

/// <summary>
/// Задача 60: счёт Freedom на реальном MySQL — синк с подменённым <see cref="IFreedomApiClient"/>, идемпотентность, GET /api/brokers/freedom/*,
/// рубли по засеянным курсам, 404 без счёта, изоляция пользователей, <c>/api/portfolio/summary</c> (otherBrokersValueRub/capitalTotalRub),
/// регрессия «основной счёт T-Invest не перехватывается счётом Freedom». Курсы USD=80, EUR=90, GBP=110 одни и те же во всех тестах класса
/// (общая БД, коллекция идёт последовательно — перезапись тех же дат безвредна).
/// </summary>
[Collection("Integration")]
public class BrokerHoldingsEndpointTests
{
    private const decimal UsdRate = 80m;
    private const decimal EurRate = 90m;
    private const decimal GbpRate = 110m;

    private readonly TestWebApplicationFactory _factory;

    public BrokerHoldingsEndpointTests(TestWebApplicationFactory factory) => _factory = factory;

    private string Conn => _factory.Database.ConnectionString;

    private static DateOnly AgoDate(int days) => DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-days));

    private async Task SeedRatesAsync()
    {
        var currencies = new CurrencyRepository(Conn);
        var market = new CurrencyMarketDataRepository(Conn);
        foreach (var (iso, rate) in new[] { ("USD", UsdRate), ("EUR", EurRate), ("GBP", GbpRate) })
        {
            var id = (await currencies.GetByIsoAsync(iso))?.Id
                     ?? await currencies.UpsertAsync(new Currency { Figi = "FX:" + iso, IsoCode = iso, Name = iso });
            await market.UpsertRatesAsync(id, [new CurrencyRatePoint(AgoDate(1), rate), new CurrencyRatePoint(AgoDate(0), rate)]);
        }
    }

    private async Task<(HttpClient Client, ulong UserId)> NewUserAsync()
    {
        var telegramId = Random.Shared.NextInt64(1, long.MaxValue);
        var userId = await new UserRepository(Conn).CreateAsync(new User { TelegramId = telegramId });
        var client = _factory.CreateClient();
        Mvv42TestAuth.Authorize(client, telegramId);
        return (client, userId);
    }

    private async Task<ulong> SeedFreedomAccountAsync(ulong userId)
    {
        var accountId = await new AccountRepository(Conn).CreateAsync(
            new Account { UserId = userId, Broker = Brokers.Freedom, BrokerAccountId = "F-" + Guid.NewGuid().ToString("N")[..8], Name = "Freedom" });
        var now = DateTime.UtcNow;
        var positions = new BrokerPositionRepository(Conn);
        await positions.UpsertAsync(new BrokerPosition { AccountId = accountId, Ticker = "LUV.US", Name = "Southwest Airlines", AssetClass = "stock", Currency = "USD", Quantity = 13m, AvgPrice = 40m, LastPrice = 44m, MarketValue = 572m, SyncedAt = now });
        await positions.UpsertAsync(new BrokerPosition { AccountId = accountId, Ticker = "ROBO.EU", Name = "L&G ROBO UCITS ETF", AssetClass = "etf", Currency = "EUR", Quantity = 3m, AvgPrice = 24m, LastPrice = 27m, MarketValue = 81m, SyncedAt = now });
        await positions.UpsertAsync(new BrokerPosition { AccountId = accountId, Ticker = "VUKE.UK", Name = "Vanguard FTSE 100 UCITS ETF", AssetClass = "etf", Currency = "GBP", Quantity = 2m, AvgPrice = 30m, LastPrice = 36m, MarketValue = 72m, SyncedAt = now });
        var cash = new BrokerCashRepository(Conn);
        await cash.UpsertAsync(new BrokerCash { AccountId = accountId, Currency = "USD", Amount = 5m, SyncedAt = now });
        await cash.UpsertAsync(new BrokerCash { AccountId = accountId, Currency = "EUR", Amount = 10m, SyncedAt = now });

        await new BrokerOperationRepository(Conn).UpsertManyAsync(
        [
            new BrokerOperation { AccountId = accountId, ExternalId = "cf:fee1", Date = DateTime.UtcNow.AddDays(-5), Type = BrokerOperationType.Fee, Amount = -0.4m, Currency = "USD", RawType = "commission_for_trades", Ticker = "LUV.US" },
            new BrokerOperation { AccountId = accountId, ExternalId = "cf:fee2", Date = DateTime.UtcNow.AddDays(-3), Type = BrokerOperationType.Fee, Amount = -0.8m, Currency = "EUR", RawType = "commission_for_trades" },
            new BrokerOperation { AccountId = accountId, ExternalId = "cf:dep", Date = DateTime.UtcNow.AddDays(-30), Type = BrokerOperationType.Deposit, Amount = 700m, Currency = "USD", RawType = "bank" },
            new BrokerOperation { AccountId = accountId, ExternalId = "trade:1", Date = DateTime.UtcNow.AddDays(-20), Type = BrokerOperationType.Buy, Amount = -520m, Currency = "USD", RawType = "trade", Ticker = "LUV.US", Quantity = 13m, Price = 40m },
        ]);
        return accountId;
    }

    // --- GET /api/brokers/freedom/holdings ---

    [Fact]
    public async Task Holdings_ValuesUsdEurGbpInRubles_BySeededRates_WithTotalsAndFees()
    {
        await SeedRatesAsync();
        var (client, userId) = await NewUserAsync();
        await SeedFreedomAccountAsync(userId);

        var response = await client.GetAsync("/api/brokers/freedom/holdings");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var positions = body.GetProperty("positions").EnumerateArray().ToDictionary(p => p.GetProperty("ticker").GetString()!);

        var luv = positions["LUV.US"];
        luv.GetProperty("currency").GetString().Should().Be("USD");
        luv.GetProperty("marketValue").GetDecimal().Should().Be(572m, "в валюте бумаги, не в рублях");
        luv.GetProperty("marketValueRub").GetDecimal().Should().Be(572m * UsdRate);
        luv.GetProperty("unrealizedPnl").GetDecimal().Should().Be(52m);
        luv.GetProperty("unrealizedPnlFraction").GetDecimal().Should().BeApproximately(52m / 520m, 0.000001m, "ДОЛЯ");
        positions["ROBO.EU"].GetProperty("marketValueRub").GetDecimal().Should().Be(81m * EurRate);
        positions["VUKE.UK"].GetProperty("marketValueRub").GetDecimal().Should().Be(72m * GbpRate);
        positions["ROBO.EU"].GetProperty("assetClass").GetString().Should().Be("etf");

        var cash = body.GetProperty("cash").EnumerateArray().ToDictionary(c => c.GetProperty("currency").GetString()!);
        cash["USD"].GetProperty("amountRub").GetDecimal().Should().Be(5m * UsdRate);
        cash["EUR"].GetProperty("amount").GetDecimal().Should().Be(10m);

        var totalRub = 572m * UsdRate + 81m * EurRate + 72m * GbpRate + 5m * UsdRate + 10m * EurRate;
        body.GetProperty("totalValueRub").GetDecimal().Should().Be(totalRub);
        body.GetProperty("totalValueUsd").GetDecimal().Should().BeApproximately(totalRub / UsdRate, 0.0001m);
        body.GetProperty("dataIncomplete").GetBoolean().Should().BeFalse();
        body.GetProperty("syncedAt").ValueKind.Should().Be(JsonValueKind.String);
        body.GetProperty("fees").GetProperty("feesPaidUsd").GetDecimal().Should().BeApproximately(0.4m + 0.8m * EurRate / UsdRate, 0.0001m);
        body.GetProperty("disclaimer").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Holdings_CurrencyWithoutRate_GivesNullRubAndDataIncomplete()
    {
        await SeedRatesAsync();
        var (client, userId) = await NewUserAsync();
        var accountId = await SeedFreedomAccountAsync(userId);
        await new BrokerPositionRepository(Conn).UpsertAsync(new BrokerPosition
        {
            AccountId = accountId, Ticker = "NORATE.XX", Name = "No rate", AssetClass = "stock", Currency = "ZZZ", Quantity = 1m, AvgPrice = 1m, LastPrice = 2m, MarketValue = 2m, SyncedAt = DateTime.UtcNow,
        });

        var body = await (await client.GetAsync("/api/brokers/freedom/holdings")).Content.ReadFromJsonAsync<JsonElement>();

        var row = body.GetProperty("positions").EnumerateArray().Single(p => p.GetProperty("ticker").GetString() == "NORATE.XX");
        row.GetProperty("marketValueRub").ValueKind.Should().Be(JsonValueKind.Null);
        row.GetProperty("marketValue").GetDecimal().Should().Be(2m, "значение в валюте остаётся");
        body.GetProperty("dataIncomplete").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Holdings_WithoutFreedomAccount_Is404()
    {
        var (client, _) = await NewUserAsync();

        (await client.GetAsync("/api/brokers/freedom/holdings")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetAsync("/api/brokers/freedom/operations")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Holdings_UsersAreIsolated_AndTInvestAccountIsNotFreedom()
    {
        await SeedRatesAsync();
        var (clientA, userA) = await NewUserAsync();
        var (clientB, userB) = await NewUserAsync();
        await SeedFreedomAccountAsync(userA);
        // У B только счёт T-Invest: чужой Freedom ему не виден, свой T-Invest — не Freedom.
        await new AccountRepository(Conn).CreateAsync(new Account { UserId = userB, Name = "Основной счёт" });

        (await clientA.GetAsync("/api/brokers/freedom/holdings")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await clientB.GetAsync("/api/brokers/freedom/holdings")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var otherFreedomUser = await NewUserAsync();
        var ownAccount = await new AccountRepository(Conn).CreateAsync(
            new Account { UserId = otherFreedomUser.UserId, Broker = Brokers.Freedom, BrokerAccountId = "F-other", Name = "Freedom" });
        await new BrokerPositionRepository(Conn).UpsertAsync(new BrokerPosition
        {
            AccountId = ownAccount, Ticker = "ONLY.B", Name = "Only B", AssetClass = "stock", Currency = "USD", Quantity = 1m, AvgPrice = 1m, LastPrice = 1m, MarketValue = 1m, SyncedAt = DateTime.UtcNow,
        });
        var bodyB = await (await otherFreedomUser.Client.GetAsync("/api/brokers/freedom/holdings")).Content.ReadFromJsonAsync<JsonElement>();
        bodyB.GetProperty("positions").EnumerateArray().Select(p => p.GetProperty("ticker").GetString()).Should().Equal("ONLY.B");
    }

    // --- GET /api/brokers/freedom/operations ---

    [Fact]
    public async Task Operations_ReturnsJournalNewestFirst_AndFiltersByDate()
    {
        var (client, userId) = await NewUserAsync();
        await SeedFreedomAccountAsync(userId);

        var all = await (await client.GetAsync("/api/brokers/freedom/operations")).Content.ReadFromJsonAsync<JsonElement>();
        var ops = all.GetProperty("operations").EnumerateArray().ToList();
        ops.Should().HaveCount(4);
        ops.Select(o => o.GetProperty("externalId").GetString()).Should().Equal("cf:fee2", "cf:fee1", "trade:1", "cf:dep");
        var fee = ops[1];
        fee.GetProperty("type").GetString().Should().Be("fee");
        fee.GetProperty("amount").GetDecimal().Should().Be(-0.4m, "знак брокера");
        fee.GetProperty("currency").GetString().Should().Be("USD");
        fee.GetProperty("rawType").GetString().Should().Be("commission_for_trades");
        fee.GetProperty("ticker").GetString().Should().Be("LUV.US");

        var from = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-6)).ToString("yyyy-MM-dd");
        var filtered = await (await client.GetAsync($"/api/brokers/freedom/operations?from={from}")).Content.ReadFromJsonAsync<JsonElement>();
        filtered.GetProperty("operations").EnumerateArray().Select(o => o.GetProperty("externalId").GetString())
            .Should().Equal("cf:fee2", "cf:fee1");

        var to = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-25)).ToString("yyyy-MM-dd");
        var old = await (await client.GetAsync($"/api/brokers/freedom/operations?to={to}")).Content.ReadFromJsonAsync<JsonElement>();
        old.GetProperty("operations").EnumerateArray().Select(o => o.GetProperty("externalId").GetString()).Should().Equal("cf:dep");

        (await client.GetAsync("/api/brokers/freedom/operations?from=2026-10-10&to=2026-10-01")).StatusCode
            .Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    // --- /api/portfolio/summary ---

    [Fact]
    public async Task PortfolioSummary_AddsOtherBrokers_WithoutChangingTotalRub()
    {
        await SeedRatesAsync();
        var (client, userId) = await NewUserAsync();
        var accounts = new AccountRepository(Conn);
        var tinvest = await accounts.CreateAsync(new Account { UserId = userId, Name = "Основной счёт" });
        await accounts.UpdateBalanceAsync(tinvest, 1_000m, 1_000m, DateTime.UtcNow);
        await SeedFreedomAccountAsync(userId);

        var body = await (await client.GetAsync("/api/portfolio/summary")).Content.ReadFromJsonAsync<JsonElement>();

        var other = 572m * UsdRate + 81m * EurRate + 72m * GbpRate + 5m * UsdRate + 10m * EurRate;
        body.GetProperty("totalRub").GetDecimal().Should().Be(1_000m, "смысл totalRub (T-Invest) не меняется");
        body.GetProperty("otherBrokersValueRub").GetDecimal().Should().Be(other);
        body.GetProperty("capitalTotalRub").GetDecimal().Should().Be(1_000m + other);
        body.GetProperty("otherBrokersDataIncomplete").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task PortfolioSummary_WithoutOtherBrokers_OtherIsZero_CapitalEqualsTotal()
    {
        var (client, userId) = await NewUserAsync();
        var accounts = new AccountRepository(Conn);
        var tinvest = await accounts.CreateAsync(new Account { UserId = userId, Name = "Основной счёт" });
        await accounts.UpdateBalanceAsync(tinvest, 2_500m, 2_500m, DateTime.UtcNow);

        var body = await (await client.GetAsync("/api/portfolio/summary")).Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("otherBrokersValueRub").GetDecimal().Should().Be(0m);
        body.GetProperty("capitalTotalRub").GetDecimal().Should().Be(body.GetProperty("totalRub").GetDecimal());
        body.GetProperty("totalRub").GetDecimal().Should().Be(2_500m);
    }

    // --- Регрессия: основной счёт T-Invest не перехватывается Freedom ---

    [Fact]
    public async Task PrimaryAccount_IgnoresFreedomAccounts()
    {
        var accounts = new AccountRepository(Conn);
        var before = await accounts.GetPrimaryAccountIdAsync();
        var (_, userId) = await NewUserAsync();

        var freedomId = await accounts.CreateAsync(new Account { UserId = userId, Broker = Brokers.Freedom, BrokerAccountId = "F-primary", Name = "Freedom" });

        var after = await accounts.GetPrimaryAccountIdAsync();
        after.Should().Be(before, "счёт Freedom не должен становиться основным");
        after.Should().NotBe(freedomId);
    }

    [Fact]
    public async Task ResolveAccount_UserWithFreedomAccountCreatedFirst_StillUsesTInvestAccount()
    {
        var (client, userId) = await NewUserAsync();
        var accounts = new AccountRepository(Conn);
        // Freedom с МЕНЬШИМ id, чем T-Invest: прежний запрос «первый счёт пользователя» взял бы его.
        var freedomId = await accounts.CreateAsync(new Account { UserId = userId, Broker = Brokers.Freedom, BrokerAccountId = "F-first", Name = "Freedom" });
        var tinvestId = await accounts.CreateAsync(new Account { UserId = userId, Name = "Основной счёт" });
        freedomId.Should().BeLessThan(tinvestId);

        var currencyId = await new CurrencyRepository(Conn).UpsertAsync(new Currency
        {
            Figi = $"FIGI{Guid.NewGuid():N}"[..20], IsoCode = "T" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant(), Ticker = "TEST_X_TOM", Name = "Тест", MoexSecid = "TEST_X_TOM",
        });
        await new CurrencyPositionRepository(Conn).UpsertAsync(new CurrencyPosition
        {
            AccountId = tinvestId, CurrencyId = currencyId, Quantity = 10m, AvgPurchasePriceRub = 1m, CurrentPriceRub = 2m, PriceAsOf = DateTime.UtcNow,
        });

        var body = await (await client.GetAsync("/api/currencies")).Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("currencies").EnumerateArray().Should().ContainSingle(c => c.GetProperty("currencyId").GetUInt64() == currencyId,
            "данные счёта T-Invest видны, хотя счёт Freedom создан раньше");
    }

    // --- Синк с подменённым IFreedomApiClient на данных в духе фикстур ---

    private sealed class StubFreedomApi : IFreedomApiClient
    {
        public int PortfolioCalls;

        public Task<FreedomUserInfo> GetUserInfoAsync(FreedomCredentials c, CancellationToken ct = default) =>
            Task.FromResult(new FreedomUserInfo("100001", 1431, "Standard"));

        public Task<FreedomPortfolio> GetPortfolioAsync(FreedomCredentials c, CancellationToken ct = default)
        {
            PortfolioCalls++;
            return Task.FromResult(new FreedomPortfolio(
                [new FreedomCashBalance("USD", 4.77m), new FreedomCashBalance("EUR", 88.57m), new FreedomCashBalance("RUR", 0m)],
                [
                    new FreedomPosition("LUV.US", "Southwest Airlines", 13m, "USD", 40.3m, 41.56m, 540.28m),
                    new FreedomPosition("ROBO.EU", "L&G ROBO Global Robotics and Automation UCITS ETF", 3m, "EUR", 24.4075m, 27.07m, 81.21m),
                ]));
        }

        public Task<IReadOnlyList<FreedomQuote>> GetQuotesAsync(FreedomCredentials c, IReadOnlyCollection<string> tickers, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<FreedomQuote>>([]);

        public Task<IReadOnlyList<FreedomTrade>> GetTradesAsync(FreedomCredentials c, DateOnly from, DateOnly to, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<FreedomTrade>>(from.Year == 2026
                ? [new FreedomTrade("900001", "o1", new DateTime(2026, 3, 1, 10, 0, 0), "LUV.US", FreedomTradeSide.Buy, 13m, 40.3m, 523.9m, "USD", 1.27m, "USD")]
                : []);

        public Task<IReadOnlyList<FreedomCashFlow>> GetCashFlowsAsync(FreedomCredentials c, int take, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<FreedomCashFlow>>(
            [
                new FreedomCashFlow("1", "bank", new DateTime(2026, 2, 1, 9, 0, 0), "USD", 700m, "Перевод"),
                new FreedomCashFlow("2", "commission_for_trades", new DateTime(2026, 3, 1, 10, 0, 1), "USD", -1.27m, "(Trade 900001 buy LUV.US ) комиссия"),
                new FreedomCashFlow("3", "dividend", new DateTime(2026, 4, 1, 10, 0, 0), "USD", 0.01m, "Дивиденды (Procure Space ETF (UFO.US))"),
            ]);

        public Task<IReadOnlyList<FreedomInstrument>> FindAsync(FreedomCredentials c, string text, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<FreedomInstrument>>([]);
    }

    [Fact]
    public async Task Sync_WithStubbedClient_StoresRows_IsIdempotent_AndFeedsHoldingsEndpoint()
    {
        await SeedRatesAsync();
        var stub = new StubFreedomApi();
        var factory = _factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.RemoveAll<IFreedomApiClient>();
            s.AddSingleton<IFreedomApiClient>(stub);
        }));

        var telegramId = Random.Shared.NextInt64(1, long.MaxValue);
        var userId = await new UserRepository(Conn).CreateAsync(new User { TelegramId = telegramId });
        var client = factory.CreateClient();
        Mvv42TestAuth.Authorize(client, telegramId);

        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IUserCredentialStore>().SaveAsync(
                userId, CredentialProviders.Freedom,
                new Dictionary<string, string> { ["publicKey"] = "PUBLIC-KEY-12345678", ["privateKey"] = "PRIVATE-KEY-SECRET" }, "5678", null);
        }

        async Task SyncAsync()
        {
            using var scope = factory.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<FreedomSyncService>().SyncAllAsync();
        }

        await SyncAsync();
        var accountsRepo = new AccountRepository(Conn);
        var account = (await accountsRepo.GetByUserIdAndBrokerAsync(userId, Brokers.Freedom)).Single();
        account.BrokerAccountId.Should().Be("100001");

        async Task<(int Positions, int Cash, int Ops)> CountsAsync() => (
            (await new BrokerPositionRepository(Conn).GetByAccountAsync(account.Id)).Count,
            (await new BrokerCashRepository(Conn).GetByAccountAsync(account.Id)).Count,
            (await new BrokerOperationRepository(Conn).GetByAccountAsync(account.Id, null, null)).Count);

        var first = await CountsAsync();
        first.Should().Be((2, 2, 4), "2 позиции, USD+EUR (RUR=0 не хранится), сделка + 3 движения денег");

        await SyncAsync();
        (await CountsAsync()).Should().Be(first, "повторный синк не дублирует строки");
        (await accountsRepo.GetByUserIdAndBrokerAsync(userId, Brokers.Freedom)).Should().HaveCount(1);

        // Журнал: ключи идемпотентности и типы.
        var ops = await new BrokerOperationRepository(Conn).GetByAccountAsync(account.Id, null, null);
        ops.Should().Contain(o => o.ExternalId == "trade:900001" && o.Type == BrokerOperationType.Buy && o.Amount == -523.9m);
        ops.Should().Contain(o => o.Type == BrokerOperationType.Fee && o.Ticker == "LUV.US" && o.Amount == -1.27m);
        ops.Should().Contain(o => o.Type == BrokerOperationType.Deposit && o.Amount == 700m);
        ops.Should().Contain(o => o.Type == BrokerOperationType.Dividend && o.Ticker == "UFO.US");

        // Основной счёт T-Invest не затронут: у пользователя нет T-Invest счёта, синк Freedom его не создаёт.
        (await accountsRepo.GetByUserIdAsync(userId)).Should().OnlyContain(a => a.Broker == Brokers.Freedom);

        var body = await (await client.GetAsync("/api/brokers/freedom/holdings")).Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("positions").GetArrayLength().Should().Be(2);
        body.GetProperty("totalValueRub").GetDecimal().Should()
            .Be(540.28m * UsdRate + 81.21m * EurRate + 4.77m * UsdRate + 88.57m * EurRate);
        body.GetProperty("fees").GetProperty("feesPaidUsd").GetDecimal().Should().Be(1.27m);
        JsonSerializer.Serialize(body).Should().NotContain("PRIVATE-KEY-SECRET").And.NotContain("PUBLIC-KEY-12345678", "ключи на фронт не уходят");
    }
}

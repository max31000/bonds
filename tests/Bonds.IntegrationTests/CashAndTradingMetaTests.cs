using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bonds.Core.Models;
using Bonds.Infrastructure.Repositories;
using Bonds.IntegrationTests.Infrastructure;
using Dapper;
using FluentAssertions;
using MySqlConnector;
using Xunit;

namespace Bonds.IntegrationTests;

/// <summary>
/// Задача 61 на реальном MySQL: идемпотентность <c>account_cash_movements</c> (UNIQUE account_id+external_id), <c>GET /api/cash</c>
/// (cashRubOnly = cashRub − валюта, последние 10 движений, изоляция пользователей, Freedom), <c>instrument_trading_meta</c> upsert и
/// <c>GET /api/trading-meta</c> (валидация, ключи без учёта регистра).
/// </summary>
[Collection("Integration")]
public class CashAndTradingMetaTests
{
    private readonly TestWebApplicationFactory _factory;

    public CashAndTradingMetaTests(TestWebApplicationFactory factory) => _factory = factory;

    private string Conn => _factory.Database.ConnectionString;

    private static DateTime Ago(int days) => DateTime.UtcNow.Date.AddDays(-days);

    private async Task<(HttpClient Client, ulong UserId)> NewUserAsync()
    {
        var telegramId = Random.Shared.NextInt64(1, long.MaxValue);
        var userId = await new UserRepository(Conn).CreateAsync(new User { TelegramId = telegramId });
        var client = _factory.CreateClient();
        Mvv42TestAuth.Authorize(client, telegramId);
        return (client, userId);
    }

    // --- репозиторий движений ---

    [Fact]
    public async Task CashMovements_UpsertTwice_NoDuplicates_AndUpdatesAmount()
    {
        var (_, userId) = await NewUserAsync();
        var accountId = await new AccountRepository(Conn).CreateAsync(new Account { UserId = userId, Name = "Основной счёт" });
        var repo = new AccountCashMovementRepository(Conn);
        var ext = "op-" + Guid.NewGuid().ToString("N");

        await repo.UpsertManyAsync([new AccountCashMovement { AccountId = accountId, ExternalId = ext, Date = Ago(2), Type = CashMovementType.Deposit, Amount = 1000m, AmountRub = 1000m, Currency = "RUB" }]);
        await repo.UpsertManyAsync([new AccountCashMovement { AccountId = accountId, ExternalId = ext, Date = Ago(2), Type = CashMovementType.Deposit, Amount = 1500m, AmountRub = 1500m, Currency = "RUB" }]);

        var rows = await repo.GetRecentAsync(accountId, 10);
        rows.Should().ContainSingle();
        rows[0].Amount.Should().Be(1500m);
        rows[0].Type.Should().Be(CashMovementType.Deposit);
    }

    [Fact]
    public async Task CashMovements_SameExternalIdOnDifferentAccounts_AreSeparate_AndNullAmountRubSurvives()
    {
        var (_, userId) = await NewUserAsync();
        var accounts = new AccountRepository(Conn);
        var a = await accounts.CreateAsync(new Account { UserId = userId, Name = "A", BrokerAccountId = "A-" + Guid.NewGuid().ToString("N")[..8] });
        var b = await accounts.CreateAsync(new Account { UserId = userId, Name = "B", BrokerAccountId = "B-" + Guid.NewGuid().ToString("N")[..8] });
        var repo = new AccountCashMovementRepository(Conn);

        await repo.UpsertManyAsync([
            new AccountCashMovement { AccountId = a, ExternalId = "same", Date = Ago(1), Type = CashMovementType.Withdrawal, Amount = -5m, AmountRub = null, Currency = "USD" },
            new AccountCashMovement { AccountId = b, ExternalId = "same", Date = Ago(1), Type = CashMovementType.Deposit, Amount = 7m, AmountRub = 7m, Currency = "RUB" },
        ]);

        var forA = await repo.GetRecentAsync(a, 10);
        forA.Should().ContainSingle();
        forA[0].AmountRub.Should().BeNull();
        forA[0].Type.Should().Be(CashMovementType.Withdrawal);
        (await repo.GetRecentAsync(b, 10)).Should().ContainSingle(m => m.Amount == 7m);
    }

    // --- GET /api/cash ---

    private async Task<ulong> SeedTInvestAsync(ulong userId, decimal cashRub, decimal cnyUnits, decimal cnyPrice, int movements = 3)
    {
        var accounts = new AccountRepository(Conn);
        var accountId = await accounts.CreateAsync(new Account { UserId = userId, Name = "Основной счёт", BrokerAccountId = "T-" + Guid.NewGuid().ToString("N")[..8] });
        await accounts.UpdateBalanceAsync(accountId, cashRub, cashRub + 5_000m, DateTime.UtcNow);

        var currencyId = await new CurrencyRepository(Conn).UpsertAsync(new Currency
        {
            Figi = $"FIGI{Guid.NewGuid():N}"[..20], IsoCode = "T" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant(), Ticker = "TEST_X_TOM", Name = "Юань", MoexSecid = "TEST_X_TOM",
        });
        await new CurrencyPositionRepository(Conn).UpsertAsync(new CurrencyPosition
        {
            AccountId = accountId, CurrencyId = currencyId, Quantity = cnyUnits, AvgPurchasePriceRub = cnyPrice, CurrentPriceRub = cnyPrice, PriceAsOf = DateTime.UtcNow,
        });

        var list = new List<AccountCashMovement>();
        for (var i = 0; i < movements; i++)
        {
            list.Add(new AccountCashMovement
            {
                AccountId = accountId, ExternalId = $"m{i}-{Guid.NewGuid():N}", Date = Ago(20 - i), Type = i % 2 == 0 ? CashMovementType.Deposit : CashMovementType.Withdrawal,
                Amount = i % 2 == 0 ? 10_000m + i : -(1_000m + i), AmountRub = i % 2 == 0 ? 10_000m + i : -(1_000m + i), Currency = "RUB",
            });
        }

        await new AccountCashMovementRepository(Conn).UpsertManyAsync(list);
        return accountId;
    }

    [Fact]
    public async Task GetCash_TInvest_CashRubOnlyExcludesCurrency_AndCurrencyListedSeparately()
    {
        var (client, userId) = await NewUserAsync();
        await SeedTInvestAsync(userId, cashRub: 120_000m, cnyUnits: 1_000m, cnyPrice: 11m);

        var body = await (await client.GetAsync("/api/cash")).Content.ReadFromJsonAsync<JsonElement>();

        var broker = body.GetProperty("brokers").EnumerateArray().Should().ContainSingle().Subject;
        broker.GetProperty("broker").GetString().Should().Be("tinvest");
        broker.GetProperty("cashRubOnly").GetDecimal().Should().Be(109_000m, "120 000 − 1000 юаней × 11 ₽");
        broker.GetProperty("totalRub").GetDecimal().Should().Be(120_000m, "брокерский итог по деньгам включает валюту");
        var balances = broker.GetProperty("balances").EnumerateArray().ToList();
        balances.Should().HaveCount(2);
        balances[0].GetProperty("currency").GetString().Should().Be("RUB");
        balances[0].GetProperty("amount").GetDecimal().Should().Be(109_000m);
        balances[1].GetProperty("amount").GetDecimal().Should().Be(1_000m, "юани - в единицах валюты");
        balances[1].GetProperty("amountRub").GetDecimal().Should().Be(11_000m);
        broker.GetProperty("dataIncomplete").GetBoolean().Should().BeFalse();
        body.GetProperty("disclaimer").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task GetCash_RecentMovements_Last10_NewestFirst_TypeCamelCase()
    {
        var (client, userId) = await NewUserAsync();
        await SeedTInvestAsync(userId, cashRub: 50_000m, cnyUnits: 0m, cnyPrice: 1m, movements: 12);

        var body = await (await client.GetAsync("/api/cash")).Content.ReadFromJsonAsync<JsonElement>();

        var movements = body.GetProperty("brokers")[0].GetProperty("recentMovements").EnumerateArray().ToList();
        movements.Should().HaveCount(10);
        var dates = movements.Select(m => m.GetProperty("date").GetDateTime()).ToList();
        dates.Should().BeInDescendingOrder();
        movements.Select(m => m.GetProperty("type").GetString()).Distinct().Should().BeEquivalentTo("deposit", "withdrawal");
        movements.Where(m => m.GetProperty("type").GetString() == "withdrawal").Should().OnlyContain(m => m.GetProperty("amount").GetDecimal() < 0m);
    }

    [Fact]
    public async Task GetCash_UserIsolation_OnlyOwnAccounts()
    {
        var (clientA, userA) = await NewUserAsync();
        var (clientB, _) = await NewUserAsync();
        await SeedTInvestAsync(userA, cashRub: 77_000m, cnyUnits: 0m, cnyPrice: 1m);

        var bodyA = await (await clientA.GetAsync("/api/cash")).Content.ReadFromJsonAsync<JsonElement>();
        var bodyB = await (await clientB.GetAsync("/api/cash")).Content.ReadFromJsonAsync<JsonElement>();

        bodyA.GetProperty("brokers").GetArrayLength().Should().Be(1);
        bodyB.GetProperty("brokers").GetArrayLength().Should().Be(0, "у второго пользователя нет счетов - чужие деньги не видны");
    }

    [Fact]
    public async Task GetCash_Freedom_BalancesRublesByRate_AndMovementsFromBrokerOperations()
    {
        var (client, userId) = await NewUserAsync();
        var accounts = new AccountRepository(Conn);
        var freedomId = await accounts.CreateAsync(new Account { UserId = userId, Broker = Brokers.Freedom, BrokerAccountId = "F-" + Guid.NewGuid().ToString("N")[..8], Name = "Freedom" });
        var now = DateTime.UtcNow;
        await new BrokerCashRepository(Conn).UpsertAsync(new BrokerCash { AccountId = freedomId, Currency = "RUB", Amount = 2_500m, SyncedAt = now });
        await new BrokerCashRepository(Conn).UpsertAsync(new BrokerCash { AccountId = freedomId, Currency = "USD", Amount = 10m, SyncedAt = now });
        await new BrokerOperationRepository(Conn).UpsertManyAsync(
        [
            new BrokerOperation { AccountId = freedomId, ExternalId = "cf:d1", Date = Ago(9), Type = BrokerOperationType.Deposit, Amount = 300m, Currency = "RUB", RawType = "bank" },
            new BrokerOperation { AccountId = freedomId, ExternalId = "cf:w1", Date = Ago(2), Type = BrokerOperationType.Withdrawal, Amount = -100m, Currency = "RUB", RawType = "bank" },
            new BrokerOperation { AccountId = freedomId, ExternalId = "cf:fee", Date = Ago(1), Type = BrokerOperationType.Fee, Amount = -1m, Currency = "RUB", RawType = "commission_for_trades" },
        ]);

        var body = await (await client.GetAsync("/api/cash")).Content.ReadFromJsonAsync<JsonElement>();

        var broker = body.GetProperty("brokers").EnumerateArray().Should().ContainSingle().Subject;
        broker.GetProperty("broker").GetString().Should().Be("freedom");
        broker.GetProperty("cashRubOnly").GetDecimal().Should().Be(2_500m);
        var movements = broker.GetProperty("recentMovements").EnumerateArray().ToList();
        movements.Should().HaveCount(2, "комиссия - не пополнение и не вывод");
        movements[0].GetProperty("type").GetString().Should().Be("withdrawal");
        movements[1].GetProperty("amountRub").GetDecimal().Should().Be(300m);
        var usd = broker.GetProperty("balances").EnumerateArray().Single(b => b.GetProperty("currency").GetString() == "USD");
        usd.GetProperty("amount").GetDecimal().Should().Be(10m);
    }

    // --- instrument_trading_meta + GET /api/trading-meta ---

    private static InstrumentTradingMeta Meta(string broker, string key, string ticker, decimal step = 0.01m) => new()
    {
        Broker = broker, InstrumentKey = key, Ticker = ticker, Uid = "uid-" + key, Figi = "figi-" + key, ClassCode = "SPBRU", Lot = 1m,
        MinPriceIncrement = step, MinPriceIncrementPoints = null, Currency = "RUB", ApiTradeAvailable = true, BuyAvailable = true, SellAvailable = true,
        PaysDistributions = false, ItsTwin = null, UpdatedAt = DateTime.UtcNow,
    };

    [Fact]
    public async Task TradingMetaRepository_Upsert_UpdatesInPlace_PreservesNullsAndUnits()
    {
        var repo = new InstrumentTradingMetaRepository(Conn);
        var key = "T61" + Guid.NewGuid().ToString("N")[..9].ToUpperInvariant();
        var meta = Meta("tinvest", key, "TMON@");
        meta.MinPriceIncrement = 0.1m;
        meta.MinPriceIncrementPoints = 0.01m;
        meta.PaysDistributions = null;
        await repo.UpsertAsync(meta);
        meta.Lot = 10m;
        meta.PaysDistributions = true;
        await repo.UpsertAsync(meta);

        var rows = await repo.GetByKeysAsync("tinvest", [key.ToLowerInvariant()]);
        rows.Should().ContainSingle();
        rows[0].Lot.Should().Be(10m);
        rows[0].MinPriceIncrement.Should().Be(0.1m);
        rows[0].MinPriceIncrementPoints.Should().Be(0.01m);
        rows[0].PaysDistributions.Should().BeTrue();
        rows[0].ItsTwin.Should().BeNull();
        rows[0].ApiTradeAvailable.Should().BeTrue();

        await using var conn = new MySqlConnection(Conn);
        (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM instrument_trading_meta WHERE broker='tinvest' AND instrument_key=@Key", new { Key = key })).Should().Be(1);
    }

    [Fact]
    public async Task GetTradingMeta_ByKeys_ReturnsOnlyRequested_AndValidates()
    {
        var (client, _) = await NewUserAsync();
        var repo = new InstrumentTradingMetaRepository(Conn);
        var k1 = "M61A" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var k2 = "M61B" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        await repo.UpsertAsync(Meta("tinvest", k1, "AAA"));
        await repo.UpsertAsync(Meta("tinvest", k2, "BBB"));
        await repo.UpsertAsync(Meta("freedom", k1, "AAA.US"));

        var body = await (await client.GetAsync($"/api/trading-meta?broker=tinvest&keys={k1.ToLowerInvariant()}")).Content.ReadFromJsonAsync<JsonElement>();

        var item = body.GetProperty("items").EnumerateArray().Should().ContainSingle().Subject;
        item.GetProperty("instrumentKey").GetString().Should().Be(k1);
        item.GetProperty("broker").GetString().Should().Be("tinvest");
        item.GetProperty("lot").GetDecimal().Should().Be(1m);
        item.GetProperty("minPriceIncrement").GetDecimal().Should().Be(0.01m);
        item.GetProperty("classCode").GetString().Should().Be("SPBRU");
        item.GetProperty("apiTradeAvailable").GetBoolean().Should().BeTrue();

        (await client.GetAsync("/api/trading-meta")).StatusCode.Should().Be((HttpStatusCode)422);
        (await client.GetAsync("/api/trading-meta?broker=unknown")).StatusCode.Should().Be((HttpStatusCode)422);
        var tooMany = string.Join(",", Enumerable.Range(0, 201).Select(i => "K" + i));
        (await client.GetAsync($"/api/trading-meta?broker=tinvest&keys={tooMany}")).StatusCode.Should().Be((HttpStatusCode)422);

        var all = await (await client.GetAsync("/api/trading-meta?broker=freedom")).Content.ReadFromJsonAsync<JsonElement>();
        all.GetProperty("items").EnumerateArray().Should().Contain(i => i.GetProperty("instrumentKey").GetString() == k1);
    }
}

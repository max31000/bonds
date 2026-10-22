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
/// Задача 55: GET /api/currencies, /api/income?assetKind=currency|all, /api/income/currency/{id}/ledger и /api/portfolio/summary на реальном MySQL.
/// Засеяно: CNY - купил 1 000 за 13 000 (13.00), проценты +30, продал 400 за 5 000 (12.50), остаток 600, курс брокера 12.20
/// (реализованный -200, бумажный 600 x (12.20 - 13.00) = -480, итог -650); проданная валюта (куплено 100 за 1 000, продано за 1 100, realized +100);
/// деньги на счёте 100 000 (cashRub брокера УЖЕ включает валюту). Каждый тест - свой счёт.
/// </summary>
[Collection("Integration")]
public class CurrencyEndpointTests
{
    private readonly TestWebApplicationFactory _factory;

    public CurrencyEndpointTests(TestWebApplicationFactory factory) => _factory = factory;

    private string Conn => _factory.Database.ConnectionString;

    private record Seeded(HttpClient Client, ulong AccountId, ulong CnyId, ulong SoldId, string CnyIso);

    private static DateTime Ago(int days) => DateTime.UtcNow.Date.AddDays(-days).AddHours(10);
    private static DateOnly AgoDate(int days) => DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-days));

    private async Task<Seeded> SeedAsync(decimal? cash = 100_000m, decimal? currentPrice = 12.20m, bool withRates = true)
    {
        var telegramId = Random.Shared.NextInt64(1, long.MaxValue);
        var userId = await new UserRepository(Conn).CreateAsync(new User { TelegramId = telegramId });
        var accounts = new AccountRepository(Conn);
        var accountId = await accounts.CreateAsync(new Account { UserId = userId, Name = "Основной счёт" });
        if (cash is not null) await accounts.UpdateBalanceAsync(accountId, cash, 101_000m, DateTime.UtcNow);

        var client = _factory.CreateClient();
        Mvv42TestAuth.Authorize(client, telegramId);

        var currencyRepo = new CurrencyRepository(Conn);
        string Suffix() => Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var iso1 = "T" + Suffix();
        var cnyId = await currencyRepo.UpsertAsync(new Currency
        {
            Figi = $"FIGI{Guid.NewGuid():N}"[..20], IsoCode = iso1, Ticker = "CNYRUB_TOM", Name = "Китайский юань", MoexSecid = "CNYRUB_TOM", Lot = 1000m,
        });
        var iso2 = "T" + Suffix();
        var soldId = await currencyRepo.UpsertAsync(new Currency
        {
            Figi = $"FIGI{Guid.NewGuid():N}"[..20], IsoCode = iso2, Ticker = "HKDRUB_TOM", Name = "Гонконгский доллар", MoexSecid = "HKDRUB_TOM",
        });

        await new CurrencyPositionRepository(Conn).UpsertAsync(new CurrencyPosition
        {
            AccountId = accountId, CurrencyId = cnyId, Quantity = 600m, AvgPurchasePriceRub = 13m, CurrentPriceRub = currentPrice, PriceAsOf = DateTime.UtcNow,
        });

        string Ext() => $"cur-{Guid.NewGuid():N}";
        await new CurrencyOperationRepository(Conn).UpsertManyByExternalIdAsync([
            new CurrencyOperation { AccountId = accountId, CurrencyId = cnyId, Type = CurrencyOperationType.Buy, Date = Ago(120), AmountRub = -13_000m, Quantity = 1000m, ExternalId = Ext() },
            new CurrencyOperation { AccountId = accountId, CurrencyId = cnyId, Type = CurrencyOperationType.Income, Date = Ago(70), AmountRub = 30m, ExternalId = Ext() },
            new CurrencyOperation { AccountId = accountId, CurrencyId = cnyId, Type = CurrencyOperationType.Sell, Date = Ago(40), AmountRub = 5_000m, Quantity = 400m, ExternalId = Ext() },
            new CurrencyOperation { AccountId = accountId, CurrencyId = soldId, Type = CurrencyOperationType.Buy, Date = Ago(100), AmountRub = -1_000m, Quantity = 100m, ExternalId = Ext() },
            new CurrencyOperation { AccountId = accountId, CurrencyId = soldId, Type = CurrencyOperationType.Sell, Date = Ago(10), AmountRub = 1_100m, Quantity = 100m, ExternalId = Ext() },
        ]);

        if (withRates)
        {
            // Курс за год: окна 1/3/12 мес. находят старт forward fill'ом (точка не старше 10 дней до начала окна).
            await new CurrencyMarketDataRepository(Conn).UpsertRatesAsync(cnyId, [
                new CurrencyRatePoint(AgoDate(400), 9.0m),
                new CurrencyRatePoint(AgoDate(366), 10.0m),
                new CurrencyRatePoint(AgoDate(95), 11.5m),
                new CurrencyRatePoint(AgoDate(35), 12.0m),
                new CurrencyRatePoint(AgoDate(2), 12.1m),
            ]);
        }

        return new Seeded(client, accountId, cnyId, soldId, iso1);
    }

    private static JsonElement Row(JsonElement body, string name, ulong id) =>
        body.GetProperty(name).EnumerateArray().Single(p => p.GetProperty(name == "currencies" ? "currencyId" : "assetId").GetUInt64() == id);

    [Fact]
    public async Task Currencies_ReturnsOpenAndSoldCurrency_WithFxPnlRateChangeAndHistory()
    {
        var s = await SeedAsync();

        var response = await s.Client.GetAsync("/api/currencies");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("currencies").GetArrayLength().Should().Be(2, "проданная валюта остаётся, если по ней есть операции");

        var cny = Row(body, "currencies", s.CnyId);
        cny.GetProperty("isoCode").GetString().Should().Be(s.CnyIso);
        cny.GetProperty("ticker").GetString().Should().Be("CNYRUB_TOM");
        cny.GetProperty("quantity").GetDecimal().Should().Be(600m, "единицы валюты, не лоты");
        cny.GetProperty("currentPriceRub").GetDecimal().Should().Be(12.20m);
        cny.GetProperty("marketValueRub").GetDecimal().Should().Be(7_320m);
        cny.GetProperty("avgCostRub").GetDecimal().Should().Be(13m);
        cny.GetProperty("investedRub").GetDecimal().Should().Be(7_800m);
        cny.GetProperty("unrealizedPnlRub").GetDecimal().Should().Be(-480m);
        cny.GetProperty("unrealizedPnlFraction").GetDecimal().Should().BeApproximately(-480m / 7_800m, 0.000001m);
        cny.GetProperty("realizedPnlRub").GetDecimal().Should().Be(-200m);
        cny.GetProperty("totalResultRub").GetDecimal().Should().Be(-650m);
        cny.GetProperty("hasUnknownLots").GetBoolean().Should().BeFalse();
        cny.GetProperty("dataIncomplete").GetBoolean().Should().BeFalse();

        // Изменение курса - ДОЛИ от того же курса 12.20: 1м -> старт 12.0, 3м -> 11.5, 12м -> 10.0.
        var change = cny.GetProperty("rateChange");
        change.GetProperty("m1").GetDecimal().Should().BeApproximately(12.20m / 12.0m - 1m, 0.0001m);
        change.GetProperty("m3").GetDecimal().Should().BeApproximately(12.20m / 11.5m - 1m, 0.0001m);
        change.GetProperty("m12").GetDecimal().Should().BeApproximately(12.20m / 10.0m - 1m, 0.0001m);

        var history = cny.GetProperty("rateHistory").EnumerateArray().ToList();
        history.Should().HaveCount(3, "в графике только последний год");
        history.Select(h => h.GetProperty("date").GetString()).Should().BeInAscendingOrder();
        history[^1].GetProperty("closeRub").GetDecimal().Should().Be(12.1m);

        var sold = Row(body, "currencies", s.SoldId);
        sold.GetProperty("quantity").GetDecimal().Should().Be(0m);
        sold.GetProperty("marketValueRub").GetDecimal().Should().Be(0m);
        sold.GetProperty("realizedPnlRub").GetDecimal().Should().Be(100m);
        sold.GetProperty("totalResultRub").GetDecimal().Should().Be(100m);
        sold.GetProperty("rateChange").GetProperty("m1").ValueKind.Should().Be(JsonValueKind.Null, "нет истории курса - окна null");
        sold.GetProperty("rateHistory").GetArrayLength().Should().Be(0);

        var totals = body.GetProperty("totals");
        totals.GetProperty("marketValueRub").GetDecimal().Should().Be(7_320m);
        totals.GetProperty("unrealizedPnlRub").GetDecimal().Should().Be(-480m);
        totals.GetProperty("realizedPnlRub").GetDecimal().Should().Be(-100m);
        totals.GetProperty("totalResultRub").GetDecimal().Should().Be(-550m);
        body.GetProperty("disclaimer").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Currencies_PositionWithoutBrokerPrice_FallsBackToLastHistoryClose_ConsistentWithIncome()
    {
        var s = await SeedAsync(currentPrice: null);

        var body = await (await s.Client.GetAsync("/api/currencies")).Content.ReadFromJsonAsync<JsonElement>();
        var cny = Row(body, "currencies", s.CnyId);
        // current_price_rub нет - берём последнее закрытие истории 12.1.
        cny.GetProperty("currentPriceRub").GetDecimal().Should().Be(12.1m);
        cny.GetProperty("marketValueRub").GetDecimal().Should().Be(600m * 12.1m);

        var income = await (await s.Client.GetAsync("/api/income?assetKind=currency")).Content.ReadFromJsonAsync<JsonElement>();
        Row(income, "positions", s.CnyId).GetProperty("marketValueCleanRub").GetDecimal().Should().Be(600m * 12.1m, "одна точка курса");

        var summary = await (await s.Client.GetAsync("/api/portfolio/summary")).Content.ReadFromJsonAsync<JsonElement>();
        summary.GetProperty("currencyValueRub").GetDecimal().Should().Be(600m * 12.1m);
    }

    [Fact]
    public async Task Currencies_NoPriceAnywhere_MarkedDataIncomplete_WithZeroValue()
    {
        var s = await SeedAsync(currentPrice: null, withRates: false);

        var body = await (await s.Client.GetAsync("/api/currencies")).Content.ReadFromJsonAsync<JsonElement>();
        var cny = Row(body, "currencies", s.CnyId);
        cny.GetProperty("currentPriceRub").ValueKind.Should().Be(JsonValueKind.Null);
        cny.GetProperty("marketValueRub").GetDecimal().Should().Be(0m);
        cny.GetProperty("dataIncomplete").GetBoolean().Should().BeTrue();
        body.GetProperty("totals").GetProperty("dataIncomplete").GetBoolean().Should().BeTrue();

        var summary = await (await s.Client.GetAsync("/api/portfolio/summary")).Content.ReadFromJsonAsync<JsonElement>();
        summary.GetProperty("currencyDataIncomplete").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Currencies_AccountWithoutAnything_ReturnsEmptyList()
    {
        var telegramId = Random.Shared.NextInt64(1, long.MaxValue);
        var userId = await new UserRepository(Conn).CreateAsync(new User { TelegramId = telegramId });
        await new AccountRepository(Conn).CreateAsync(new Account { UserId = userId, Name = "Пустой" });
        var client = _factory.CreateClient();
        Mvv42TestAuth.Authorize(client, telegramId);

        var body = await (await client.GetAsync("/api/currencies")).Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("currencies").GetArrayLength().Should().Be(0);
        body.GetProperty("totals").GetProperty("totalResultRub").GetDecimal().Should().Be(0m);
    }

    [Fact]
    public async Task Income_AssetKindCurrency_ShowsOpenAndSold_AllIncludesThem_AndPeriodCarriesCurrencyIncome()
    {
        var s = await SeedAsync();

        var response = await s.Client.GetAsync("/api/income?assetKind=currency");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("assetKind").GetString().Should().Be("currency");
        body.GetProperty("positions").GetArrayLength().Should().Be(2);
        var open = Row(body, "positions", s.CnyId);
        open.GetProperty("assetKind").GetString().Should().Be("currency");
        open.GetProperty("currencyId").GetUInt64().Should().Be(s.CnyId);
        open.GetProperty("status").GetString().Should().Be("open");
        open.GetProperty("unrealizedPnlRub").GetDecimal().Should().Be(-480m);
        open.GetProperty("realizedPnlRub").GetDecimal().Should().Be(-200m);
        open.GetProperty("incomeGrossRub").GetDecimal().Should().Be(30m);
        open.GetProperty("totalResultRub").GetDecimal().Should().Be(-650m);
        Row(body, "positions", s.SoldId).GetProperty("status").GetString().Should().Be("closed");

        // Курсовая разница - в существующем realizedPnlRub периода, проценты на остаток - в новом поле, итоги сходятся.
        var period = body.GetProperty("period");
        period.GetProperty("currencyIncomeGrossRub").GetDecimal().Should().Be(30m);
        period.GetProperty("realizedPnlRub").GetDecimal().Should().Be(-100m, "-200 по юаням + 100 по проданной валюте");
        period.GetProperty("couponsGrossRub").GetDecimal().Should().Be(0m);
        period.GetProperty("distributionsGrossRub").GetDecimal().Should().Be(0m);
        period.GetProperty("netRub").GetDecimal().Should().Be(30m - 100m);
        body.GetProperty("monthly").EnumerateArray().Sum(m => m.GetProperty("currencyIncomeGrossRub").GetDecimal()).Should().Be(30m);
        body.GetProperty("totals").GetProperty("totalResultRub").GetDecimal().Should().Be(-550m);

        var all = await (await s.Client.GetAsync("/api/income")).Content.ReadFromJsonAsync<JsonElement>();
        all.GetProperty("assetKind").GetString().Should().Be("all");
        Row(all, "positions", s.CnyId).GetProperty("assetKind").GetString().Should().Be("currency");

        // Другие фильтры валюту не показывают.
        var bonds = await (await s.Client.GetAsync("/api/income?assetKind=bond")).Content.ReadFromJsonAsync<JsonElement>();
        bonds.GetProperty("positions").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Income_PortfolioXirr_IncludesCurrencyFlows()
    {
        var s = await SeedAsync();

        var body = await (await s.Client.GetAsync("/api/income?assetKind=currency")).Content.ReadFromJsonAsync<JsonElement>();

        // Потоки: -13 000 (120 дн.), +30, +5 000, терминал 7 320; сумма = -650 -> XIRR отрицательный и посчитан.
        body.GetProperty("portfolioXirrFraction").ValueKind.Should().Be(JsonValueKind.Number);
        body.GetProperty("portfolioXirrFraction").GetDecimal().Should().BeLessThan(0m);
    }

    [Fact]
    public async Task IncomeLedger_Currency_ReturnsChronologyWithRunningAverageCost()
    {
        var s = await SeedAsync();

        var response = await s.Client.GetAsync($"/api/income/currency/{s.CnyId}/ledger");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("assetKind").GetString().Should().Be("currency");
        body.GetProperty("assetId").GetUInt64().Should().Be(s.CnyId);
        var entries = body.GetProperty("entries").EnumerateArray().ToList();
        entries.Select(e => e.GetProperty("kind").GetString()).Should().Equal("buy", "income", "sell");
        entries[0].GetProperty("amountRub").GetDecimal().Should().Be(-13_000m, "знак брокера");
        entries[2].GetProperty("quantityAfter").GetDecimal().Should().Be(600m);
        entries[2].GetProperty("avgCostAfterRub").GetDecimal().Should().Be(13m);
        entries[2].GetProperty("realizedPnlRub").GetDecimal().Should().Be(-200m);

        (await s.Client.GetAsync("/api/income/currency/999999999/ledger")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await s.Client.GetAsync("/api/income/gold/1/ledger")).StatusCode.Should().Be((HttpStatusCode)422);
    }

    [Fact]
    public async Task Income_UnknownAssetKind_Returns422_MentioningCurrency()
    {
        var s = await SeedAsync();
        var response = await s.Client.GetAsync("/api/income?assetKind=crypto");
        response.StatusCode.Should().Be((HttpStatusCode)422);
        (await response.Content.ReadAsStringAsync()).Should().Contain("currency");
    }

    [Fact]
    public async Task PortfolioSummary_SplitsCashIntoRubOnlyAndCurrency_TotalNotDoubleCounted()
    {
        var s = await SeedAsync(cash: 100_000m);

        var body = await (await s.Client.GetAsync("/api/portfolio/summary")).Content.ReadFromJsonAsync<JsonElement>();

        var cash = body.GetProperty("cashRub").GetDecimal();
        var currency = body.GetProperty("currencyValueRub").GetDecimal();
        var rubOnly = body.GetProperty("cashRubOnly").GetDecimal();
        cash.Should().Be(100_000m);
        currency.Should().Be(7_320m, "600 юаней x 12.20");
        rubOnly.Should().Be(92_680m);
        (rubOnly + currency).Should().Be(cash, "cashRubOnly + currencyValueRub = cashRub");
        // Итог = облигации (0) + фонды (0) + cashRub - валюта уже внутри cashRub и второй раз не прибавляется.
        body.GetProperty("totalRub").GetDecimal().Should().Be(100_000m);
        body.GetProperty("currencyDataIncomplete").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task PortfolioSummary_TotalUnchangedByHavingCurrency_OnlyBreakdownChanges()
    {
        var withCurrency = await SeedAsync(cash: 50_000m);
        var plain = await SeedAsync(cash: 50_000m);
        // У второго счёта валютной позиции нет вовсе - итог тот же при одинаковом cashRub.
        await new CurrencyPositionRepository(Conn).DeleteAllExceptAsync(plain.AccountId, Array.Empty<ulong>());

        var a = await (await withCurrency.Client.GetAsync("/api/portfolio/summary")).Content.ReadFromJsonAsync<JsonElement>();
        var b = await (await plain.Client.GetAsync("/api/portfolio/summary")).Content.ReadFromJsonAsync<JsonElement>();

        a.GetProperty("totalRub").GetDecimal().Should().Be(b.GetProperty("totalRub").GetDecimal());
        b.GetProperty("currencyValueRub").GetDecimal().Should().Be(0m);
        b.GetProperty("cashRubOnly").GetDecimal().Should().Be(50_000m);
        a.GetProperty("cashRubOnly").GetDecimal().Should().Be(50_000m - 7_320m);
    }

    [Fact]
    public async Task PortfolioSummary_CurrencyLargerThanCash_CashRubOnlyFlooredAtZero()
    {
        var s = await SeedAsync(cash: 5_000m); // брокерский cash меньше оценки валюты (7 320)

        var body = await (await s.Client.GetAsync("/api/portfolio/summary")).Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("cashRubOnly").GetDecimal().Should().Be(0m);
        body.GetProperty("totalRub").GetDecimal().Should().Be(5_000m);
    }

    [Fact]
    public async Task PortfolioSummary_WithoutBalance_HasNullCashRubOnly_ButCurrencyValue()
    {
        var s = await SeedAsync(cash: null);

        var body = await (await s.Client.GetAsync("/api/portfolio/summary")).Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("cashRub").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("cashRubOnly").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("currencyValueRub").GetDecimal().Should().Be(7_320m);
    }
}

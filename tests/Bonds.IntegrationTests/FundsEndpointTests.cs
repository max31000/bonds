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
/// Задача 53: /api/funds, /api/funds/{id}, PUT settings, /api/portfolio/summary на реальном MySQL. Засеяно (как в IncomeEndpointTests):
/// фонд TPAY — 100 паёв по 100 (покупка −10 000), 3 выплаты по 120 (брутто), налог −46.8, цена 101 → итог 413.2; проданный фонд (куплен и
/// продан, qty 0); открытая облигация (стоимость 10 150) и деньги/брокерский итог на счёте. Каждый тест — свой счёт.
/// </summary>
[Collection("Integration")]
public class FundsEndpointTests
{
    private readonly TestWebApplicationFactory _factory;

    public FundsEndpointTests(TestWebApplicationFactory factory) => _factory = factory;

    private string Conn => _factory.Database.ConnectionString;

    private record Seeded(HttpClient Client, ulong AccountId, ulong FundId, ulong SoldFundId);

    private static DateTime Ago(int days) => DateTime.UtcNow.Date.AddDays(-days).AddHours(10);
    private static DateOnly AgoDate(int days) => DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-days));

    private async Task<Seeded> SeedAsync(decimal? cash = 500m, decimal? brokerTotal = 11_000m)
    {
        var telegramId = Random.Shared.NextInt64(1, long.MaxValue);
        var userId = await new UserRepository(Conn).CreateAsync(new User { TelegramId = telegramId });
        var accounts = new AccountRepository(Conn);
        var accountId = await accounts.CreateAsync(new Account { UserId = userId, Name = "Основной счёт" });
        if (cash is not null || brokerTotal is not null)
            await accounts.UpdateBalanceAsync(accountId, cash, brokerTotal, DateTime.UtcNow);

        var client = _factory.CreateClient();
        Mvv42TestAuth.Authorize(client, telegramId);

        // Облигация: 10 шт., грязная 1 015 → 10 150.
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

        var fundRepo = new FundRepository(Conn);
        string Suffix() => Guid.NewGuid().ToString("N")[..10];
        var s1 = Suffix();
        var fundId = await fundRepo.UpsertAsync(new Fund
        {
            Figi = $"FIGI{s1}", Isin = $"RU{s1}", Ticker = "TPAY", Name = "Пассивный доход", ManagementCompany = "Т-Капитал",
            FocusType = "fixed_income", TotalExpenseFraction = 0.0079m,
        });
        var s2 = Suffix();
        var soldId = await fundRepo.UpsertAsync(new Fund { Figi = $"FIGI{s2}", Isin = $"RU{s2}", Ticker = "TSOLD", Name = "Проданный фонд" });

        await new FundPositionRepository(Conn).UpsertAsync(new FundPosition
        {
            AccountId = accountId, FundId = fundId, Quantity = 100, AvgPurchasePriceRub = 100m, CurrentPriceRub = 101m, PriceAsOf = DateTime.UtcNow,
        });

        string Ext() => $"fund-{Guid.NewGuid():N}";
        await new FundOperationRepository(Conn).UpsertManyByExternalIdAsync([
            new FundOperation { AccountId = accountId, FundId = fundId, Type = FundOperationType.Buy, Date = Ago(150), AmountRub = -10_000m, Quantity = 100, ExternalId = Ext() },
            new FundOperation { AccountId = accountId, FundId = fundId, Type = FundOperationType.Distribution, Date = Ago(120), AmountRub = 120m, ExternalId = Ext() },
            new FundOperation { AccountId = accountId, FundId = fundId, Type = FundOperationType.Distribution, Date = Ago(90), AmountRub = 120m, ExternalId = Ext() },
            new FundOperation { AccountId = accountId, FundId = fundId, Type = FundOperationType.Distribution, Date = Ago(60), AmountRub = 120m, ExternalId = Ext() },
            new FundOperation { AccountId = accountId, FundId = fundId, Type = FundOperationType.Tax, Date = Ago(60), AmountRub = -46.8m, ExternalId = Ext() },
            // Проданный: купил 10 по 100, продал 10 по 110 → realized +100.
            new FundOperation { AccountId = accountId, FundId = soldId, Type = FundOperationType.Buy, Date = Ago(100), AmountRub = -1_000m, Quantity = 10, ExternalId = Ext() },
            new FundOperation { AccountId = accountId, FundId = soldId, Type = FundOperationType.Sell, Date = Ago(10), AmountRub = 1_100m, Quantity = 10, ExternalId = Ext() },
        ]);

        // Рыночные данные TPAY: цена 100 → 101 за последние 40 дней, 3 выплаты на пай по 1.2 в окне 92 дней.
        var market = new FundMarketDataRepository(Conn);
        await market.UpsertPricesAsync(fundId, [
            new FundPricePoint(AgoDate(200), 99m),
            new FundPricePoint(AgoDate(40), 100m),
            new FundPricePoint(AgoDate(0), 101m),
        ]);
        await market.UpsertDistributionsAsync(fundId, [
            new FundDistribution(AgoDate(80), AgoDate(75), 1.2m),
            new FundDistribution(AgoDate(50), AgoDate(45), 1.2m),
            new FundDistribution(AgoDate(20), AgoDate(15), 1.2m),
        ]);

        return new Seeded(client, accountId, fundId, soldId);
    }

    [Fact]
    public async Task Funds_ReturnsOpenFundWithPnlPerformanceAndOutlook()
    {
        var s = await SeedAsync();

        var response = await s.Client.GetAsync("/api/funds");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        var funds = body.GetProperty("funds").EnumerateArray().ToList();
        funds.Should().ContainSingle("проданный фонд в списке открытых не показывается");
        var f = funds[0];
        f.GetProperty("fundId").GetUInt64().Should().Be(s.FundId);
        f.GetProperty("ticker").GetString().Should().Be("TPAY");
        f.GetProperty("quantity").GetDecimal().Should().Be(100m);
        f.GetProperty("marketValueRub").GetDecimal().Should().Be(10_100m);
        f.GetProperty("avgCostRub").GetDecimal().Should().Be(100m);
        f.GetProperty("investedRub").GetDecimal().Should().Be(10_000m);
        f.GetProperty("unrealizedPnlRub").GetDecimal().Should().Be(100m);
        f.GetProperty("unrealizedPnlFraction").GetDecimal().Should().Be(0.01m);
        f.GetProperty("distributionsGrossRub").GetDecimal().Should().Be(360m);
        f.GetProperty("taxesRub").GetDecimal().Should().Be(-46.8m);
        f.GetProperty("distributionsNetRub").GetDecimal().Should().Be(313.2m);
        f.GetProperty("totalResultRub").GetDecimal().Should().Be(413.2m);
        f.GetProperty("totalExpenseFraction").GetDecimal().Should().Be(0.0079m);

        // Одна методика с «Доходами»: P&L строки фонда совпадает с /api/income.
        var income = await (await s.Client.GetAsync("/api/income?assetKind=fund")).Content.ReadFromJsonAsync<JsonElement>();
        var incomeRow = income.GetProperty("positions").EnumerateArray().Single(p => p.GetProperty("assetId").GetUInt64() == s.FundId);
        incomeRow.GetProperty("totalResultRub").GetDecimal().Should().Be(f.GetProperty("totalResultRub").GetDecimal());
        incomeRow.GetProperty("unrealizedPnlRub").GetDecimal().Should().Be(f.GetProperty("unrealizedPnlRub").GetDecimal());

        // Окно 3м: старт — цена 99 (forward fill с −200 дн.), конец 101, выплаты 3.6 → (101+3.6)/99 − 1.
        var m3 = f.GetProperty("performance").GetProperty("m3");
        m3.GetProperty("returnFraction").GetDecimal().Should().BeApproximately((101m + 3.6m) / 99m - 1m, 0.0001m);

        // Auto → по выплатам (3 за 92 дня): 3.6 × 365/92 / 101.
        var outlook = f.GetProperty("outlook");
        outlook.GetProperty("method").GetString().Should().Be("distributions");
        outlook.GetProperty("expectedAnnualYieldFraction").GetDecimal().Should().BeApproximately(3.6m * 365m / 92m / 101m, 0.0001m);
        outlook.GetProperty("expectedMonthlyIncomeRub").GetDecimal().Should().Be(120m);

        // Доля: фонд 10 100 / (облигации 10 150 + фонды 10 100).
        f.GetProperty("shareOfPortfolioFraction").GetDecimal().Should().BeApproximately(10_100m / 20_250m, 0.0001m);

        var totals = body.GetProperty("totals");
        totals.GetProperty("marketValueRub").GetDecimal().Should().Be(10_100m);
        totals.GetProperty("totalResultRub").GetDecimal().Should().Be(413.2m);
        body.GetProperty("disclaimer").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task FundDetail_ReturnsHistoryDistributionsOperationsAndSettings()
    {
        var s = await SeedAsync();

        var response = await s.Client.GetAsync($"/api/funds/{s.FundId}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("fund").GetProperty("fundId").GetUInt64().Should().Be(s.FundId);
        body.GetProperty("priceHistory").GetArrayLength().Should().Be(3);
        var dists = body.GetProperty("distributions").EnumerateArray().ToList();
        dists.Should().HaveCount(3);
        dists[0].GetProperty("amountPerShareRub").GetDecimal().Should().Be(1.2m);
        dists[0].GetProperty("yieldOnPriceFraction").GetDecimal().Should().BeApproximately(1.2m / 99m, 0.0001m);
        body.GetProperty("operations").GetArrayLength().Should().Be(5);
        body.GetProperty("settings").GetProperty("forecastMethod").GetString().Should().Be("auto");
    }

    [Fact]
    public async Task FundDetail_SoldFund_IsReturnedWithZeroQuantity_AndUnknownIs404()
    {
        var s = await SeedAsync();

        var response = await s.Client.GetAsync($"/api/funds/{s.SoldFundId}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var fund = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("fund");
        fund.GetProperty("quantity").GetDecimal().Should().Be(0m);
        fund.GetProperty("marketValueRub").GetDecimal().Should().Be(0m);
        fund.GetProperty("totalResultRub").GetDecimal().Should().Be(100m);

        (await s.Client.GetAsync("/api/funds/999999999")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PutSettings_PersistsMethodAndBenchmark_AndAffectsOutlook()
    {
        var s = await SeedAsync();

        var put = await s.Client.PutAsJsonAsync($"/api/funds/{s.FundId}/settings", new { forecastMethod = "indexYield", benchmarkIndex = "RGBI" });
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        var detail = await (await s.Client.GetAsync($"/api/funds/{s.FundId}")).Content.ReadFromJsonAsync<JsonElement>();
        detail.GetProperty("settings").GetProperty("forecastMethod").GetString().Should().Be("indexYield");
        detail.GetProperty("settings").GetProperty("benchmarkIndex").GetString().Should().Be("RGBI");
        // Явный метод без данных индекса — null + причина, без тихого фолбэка.
        var outlook = detail.GetProperty("fund").GetProperty("outlook");
        outlook.GetProperty("method").GetString().Should().Be("indexYield");
        outlook.GetProperty("unavailableReason").GetString().Should().NotBeNullOrEmpty();
        outlook.GetProperty("expectedAnnualYieldFraction").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Theory]
    [InlineData("bogus", null)]
    [InlineData("5", null)]
    [InlineData("", null)]
    [InlineData("auto", "rgbi")]
    [InlineData("auto", "R")]
    [InlineData("auto", "RGBI; DROP")]
    public async Task PutSettings_InvalidInput_Returns422(string method, string? benchmark)
    {
        var s = await SeedAsync();
        var response = await s.Client.PutAsJsonAsync($"/api/funds/{s.FundId}/settings", new { forecastMethod = method, benchmarkIndex = benchmark });
        response.StatusCode.Should().Be((HttpStatusCode)422);
    }

    [Fact]
    public async Task PutSettings_UnknownFund_Returns404()
    {
        var s = await SeedAsync();
        var response = await s.Client.PutAsJsonAsync("/api/funds/999999999/settings", new { forecastMethod = "auto" });
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PortfolioSummary_SumsBondsFundsAndCash_AndReconcilesWithBroker()
    {
        var s = await SeedAsync(cash: 500m, brokerTotal: 21_000m);

        var response = await s.Client.GetAsync("/api/portfolio/summary");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("bondsValueRub").GetDecimal().Should().Be(10_150m);
        body.GetProperty("fundsValueRub").GetDecimal().Should().Be(10_100m);
        body.GetProperty("cashRub").GetDecimal().Should().Be(500m);
        body.GetProperty("totalRub").GetDecimal().Should().Be(20_750m);
        body.GetProperty("brokerTotalRub").GetDecimal().Should().Be(21_000m);
        body.GetProperty("brokerDiffRub").GetDecimal().Should().Be(250m);
        body.GetProperty("balanceSyncedAt").ValueKind.Should().Be(JsonValueKind.String);
    }

    [Fact]
    public async Task PortfolioSummary_WithoutBalance_HasNullCashAndDiff()
    {
        var s = await SeedAsync(cash: null, brokerTotal: null);

        var body = await (await s.Client.GetAsync("/api/portfolio/summary")).Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("cashRub").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("brokerDiffRub").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("totalRub").GetDecimal().Should().Be(20_250m);
    }

    [Fact]
    public async Task PositionWithoutPrice_FallsBackToLastHistoryClose_ConsistentEverywhere()
    {
        var s = await SeedAsync(cash: 500m, brokerTotal: null);
        // current_price_rub исчез (напр. T-Invest не вернул цену) — берём последнее закрытие 101 из fund_price_history.
        await new FundPositionRepository(Conn).UpsertAsync(new FundPosition
        {
            AccountId = s.AccountId, FundId = s.FundId, Quantity = 100, AvgPurchasePriceRub = 100m, CurrentPriceRub = null,
        });

        var funds = await (await s.Client.GetAsync("/api/funds")).Content.ReadFromJsonAsync<JsonElement>();
        var f = funds.GetProperty("funds")[0];
        f.GetProperty("currentPriceRub").GetDecimal().Should().Be(101m);
        f.GetProperty("marketValueRub").GetDecimal().Should().Be(10_100m, "стоимость = цена × паи от той же цены");
        f.GetProperty("unrealizedPnlRub").GetDecimal().Should().Be(100m);
        f.GetProperty("dataIncomplete").GetBoolean().Should().BeFalse();

        var summary = await (await s.Client.GetAsync("/api/portfolio/summary")).Content.ReadFromJsonAsync<JsonElement>();
        summary.GetProperty("fundsValueRub").GetDecimal().Should().Be(10_100m);
        summary.GetProperty("fundsDataIncomplete").GetBoolean().Should().BeFalse();

        var income = await (await s.Client.GetAsync("/api/income?assetKind=fund")).Content.ReadFromJsonAsync<JsonElement>();
        income.GetProperty("positions")[0].GetProperty("marketValueCleanRub").GetDecimal().Should().Be(10_100m);
    }

    [Fact]
    public async Task PositionWithoutAnyPrice_IsZeroValueAndFlaggedIncomplete()
    {
        var s = await SeedAsync(cash: 0m, brokerTotal: null);
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var noPriceId = await new FundRepository(Conn).UpsertAsync(new Fund { Figi = $"FIGI{suffix}", Isin = $"RU{suffix}", Ticker = "TNOPR", Name = "Без цены" });
        await new FundPositionRepository(Conn).UpsertAsync(new FundPosition
        {
            AccountId = s.AccountId, FundId = noPriceId, Quantity = 10, AvgPurchasePriceRub = 50m, CurrentPriceRub = null,
        });
        await new FundOperationRepository(Conn).UpsertManyByExternalIdAsync([
            new FundOperation { AccountId = s.AccountId, FundId = noPriceId, Type = FundOperationType.Buy, Date = Ago(30), AmountRub = -500m, Quantity = 10, ExternalId = $"fund-{Guid.NewGuid():N}" },
        ]);

        var funds = await (await s.Client.GetAsync("/api/funds")).Content.ReadFromJsonAsync<JsonElement>();
        var row = funds.GetProperty("funds").EnumerateArray().Single(x => x.GetProperty("fundId").GetUInt64() == noPriceId);
        row.GetProperty("dataIncomplete").GetBoolean().Should().BeTrue();
        row.GetProperty("marketValueRub").GetDecimal().Should().Be(0m);
        funds.GetProperty("totals").GetProperty("dataIncomplete").GetBoolean().Should().BeTrue();

        var summary = await (await s.Client.GetAsync("/api/portfolio/summary")).Content.ReadFromJsonAsync<JsonElement>();
        summary.GetProperty("fundsValueRub").GetDecimal().Should().Be(10_100m, "фонд без цены вклада не даёт");
        summary.GetProperty("fundsDataIncomplete").GetBoolean().Should().BeTrue();
    }
}

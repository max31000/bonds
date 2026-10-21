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
/// Задача 52: GET /api/income и /api/income/{kind}/{id}/ledger на реальном MySQL. Засеяно: открытая облигация (эталон:
/// покупка −10 200 с НКД 200, купон 400, налог −52, чистая стоимость 10 100 + НКД 50 → итог 298), ЗАКРЫТАЯ облигация
/// (погашена, без строки в positions), фонд (100 паёв по 100, 3 выплаты по 120, налог −46.8, цена 101 → итог 413.2) и
/// комиссия «по счёту» −10. Каждый тест создаёт свой счёт — данные не пересекаются.
/// </summary>
[Collection("Integration")]
public class IncomeEndpointTests
{
    private readonly TestWebApplicationFactory _factory;

    public IncomeEndpointTests(TestWebApplicationFactory factory) => _factory = factory;

    private string Conn => _factory.Database.ConnectionString;

    private record Seeded(HttpClient Client, ulong AccountId, ulong OpenBondId, ulong ClosedBondId, ulong FundId);

    private static DateTime Ago(int days) => DateTime.UtcNow.Date.AddDays(-days).AddHours(10);

    private async Task<ulong> NewInstrumentAsync(string name)
    {
        var isin = $"RU{Guid.NewGuid():N}"[..12];
        return await new InstrumentRepository(Conn).UpsertAsync(new Instrument
        {
            Isin = isin, Secid = isin, Name = name, Issuer = "Тест", FaceValue = 1000m, Currency = "RUB",
            CouponType = CouponType.Fixed, MaturityDate = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(3)),
        });
    }

    private async Task<Seeded> SeedAsync()
    {
        var telegramId = Random.Shared.NextInt64(1, long.MaxValue);
        var userId = await new UserRepository(Conn).CreateAsync(new User { TelegramId = telegramId });
        var accountId = await new AccountRepository(Conn).CreateAsync(new Account { UserId = userId, Name = "Основной счёт" });
        var client = _factory.CreateClient();
        Mvv42TestAuth.Authorize(client, telegramId);

        var open = await NewInstrumentAsync("Открытая-" + Guid.NewGuid().ToString("N")[..6]);
        var closed = await NewInstrumentAsync("Закрытая-" + Guid.NewGuid().ToString("N")[..6]);

        // Только открытая бумага имеет позицию и котировку: чистая 1 010, НКД 5 на бумагу, грязная 1 015 → ×10 = 10 150.
        await new PositionRepository(Conn).UpsertAsync(new Position { AccountId = accountId, InstrumentId = open, Quantity = 10, AvgPurchasePrice = 1000m, Accrued = 5m });
        await new MarketQuoteRepository(Conn).UpsertAsync(new MarketQuote
        {
            InstrumentId = open, AsOf = DateOnly.FromDateTime(DateTime.UtcNow), CleanPrice = 1010m, DirtyPrice = 1015m, Accrued = 5m, Source = MarketQuoteSource.Moex,
        });

        string Ext() => $"inc-{Guid.NewGuid():N}";
        await new OperationRepository(Conn).UpsertManyByExternalIdAsync([
            new Operation { AccountId = accountId, InstrumentId = open, Type = OperationType.Buy, Date = Ago(100), AmountRub = -10_200m, Quantity = 10, AccruedRub = 200m, ExternalId = Ext() },
            new Operation { AccountId = accountId, InstrumentId = open, Type = OperationType.Coupon, Date = Ago(50), AmountRub = 400m, ExternalId = Ext() },
            new Operation { AccountId = accountId, InstrumentId = open, Type = OperationType.Tax, Date = Ago(50), AmountRub = -52m, ExternalId = Ext() },
            // Закрытая (погашена): купил 10 за 10 000, погашение 10 100 → реализованный 100.
            new Operation { AccountId = accountId, InstrumentId = closed, Type = OperationType.Buy, Date = Ago(200), AmountRub = -10_000m, Quantity = 10, AccruedRub = 0m, ExternalId = Ext() },
            new Operation { AccountId = accountId, InstrumentId = closed, Type = OperationType.Redemption, Date = Ago(20), AmountRub = 10_100m, Quantity = 10, ExternalId = Ext() },
            // Комиссия без бумаги — «по счёту».
            new Operation { AccountId = accountId, InstrumentId = null, Type = OperationType.Fee, Date = Ago(30), AmountRub = -10m, ExternalId = Ext() },
        ]);

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var fundId = await new FundRepository(Conn).UpsertAsync(new Fund { Figi = $"FIGI{suffix}", Isin = $"RU{suffix}", Ticker = "TPAY", Name = "Пассивный доход" });
        await new FundPositionRepository(Conn).UpsertAsync(new FundPosition { AccountId = accountId, FundId = fundId, Quantity = 100, AvgPurchasePriceRub = 100m, CurrentPriceRub = 101m });
        await new FundOperationRepository(Conn).UpsertManyByExternalIdAsync([
            new FundOperation { AccountId = accountId, FundId = fundId, Type = FundOperationType.Buy, Date = Ago(150), AmountRub = -10_000m, Quantity = 100, ExternalId = Ext() },
            new FundOperation { AccountId = accountId, FundId = fundId, Type = FundOperationType.Distribution, Date = Ago(120), AmountRub = 120m, ExternalId = Ext() },
            new FundOperation { AccountId = accountId, FundId = fundId, Type = FundOperationType.Distribution, Date = Ago(90), AmountRub = 120m, ExternalId = Ext() },
            new FundOperation { AccountId = accountId, FundId = fundId, Type = FundOperationType.Distribution, Date = Ago(60), AmountRub = 120m, ExternalId = Ext() },
            new FundOperation { AccountId = accountId, FundId = fundId, Type = FundOperationType.Tax, Date = Ago(60), AmountRub = -46.8m, ExternalId = Ext() },
        ]);

        return new Seeded(client, accountId, open, closed, fundId);
    }

    private static JsonElement Position(JsonElement body, string kind, ulong id) =>
        body.GetProperty("positions").EnumerateArray().Single(p => p.GetProperty("assetKind").GetString() == kind && p.GetProperty("assetId").GetUInt64() == id);

    [Fact]
    public async Task Income_ShowsOpenClosedBondsAndFund_WithInvariantsAndAccountLevel()
    {
        var s = await SeedAsync();

        var response = await s.Client.GetAsync("/api/income");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("positions").GetArrayLength().Should().Be(3, "закрытая облигация без строки в positions тоже показана");

        var open = Position(body, "bond", s.OpenBondId);
        open.GetProperty("status").GetString().Should().Be("open");
        open.GetProperty("positionId").ValueKind.Should().Be(JsonValueKind.Number);
        open.GetProperty("marketValueCleanRub").GetDecimal().Should().Be(10_100m);
        open.GetProperty("accruedNowRub").GetDecimal().Should().Be(50m);
        open.GetProperty("netCouponIncomeRub").GetDecimal().Should().Be(250m);
        open.GetProperty("totalResultRub").GetDecimal().Should().Be(298m);

        var closed = Position(body, "bond", s.ClosedBondId);
        closed.GetProperty("status").GetString().Should().Be("redeemed");
        closed.GetProperty("positionId").ValueKind.Should().Be(JsonValueKind.Null);
        closed.GetProperty("realizedPnlRub").GetDecimal().Should().Be(100m);
        closed.GetProperty("unrealizedPnlRub").ValueKind.Should().Be(JsonValueKind.Null);
        closed.GetProperty("totalResultRub").GetDecimal().Should().Be(100m);

        var fund = Position(body, "fund", s.FundId);
        fund.GetProperty("name").GetString().Should().Be("Пассивный доход");
        fund.GetProperty("fundId").GetUInt64().Should().Be(s.FundId);
        fund.GetProperty("totalResultRub").GetDecimal().Should().Be(413.2m);

        body.GetProperty("accountLevel").GetProperty("feesRub").GetDecimal().Should().Be(-10m);
        body.GetProperty("totals").GetProperty("totalResultRub").GetDecimal().Should().Be(298m + 100m + 413.2m - 10m);
        body.GetProperty("unrealizedIsAllTime").GetBoolean().Should().BeTrue();
        body.GetProperty("assetKind").GetString().Should().Be("all");
        body.GetProperty("disclaimer").GetString().Should().Contain("не FIFO");
        body.GetProperty("monthly").GetArrayLength().Should().BeGreaterThan(1);
        body.GetProperty("portfolioXirrFraction").ValueKind.Should().Be(JsonValueKind.Number);
    }

    [Fact]
    public async Task Income_AssetKindFilter_AndIncludeClosed()
    {
        var s = await SeedAsync();

        var bonds = await (await s.Client.GetAsync("/api/income?assetKind=bond")).Content.ReadFromJsonAsync<JsonElement>();
        bonds.GetProperty("positions").GetArrayLength().Should().Be(2);
        bonds.GetProperty("assetKind").GetString().Should().Be("bond");

        var funds = await (await s.Client.GetAsync("/api/income?assetKind=fund")).Content.ReadFromJsonAsync<JsonElement>();
        funds.GetProperty("positions").GetArrayLength().Should().Be(1);

        var openOnly = await (await s.Client.GetAsync("/api/income?includeClosed=false")).Content.ReadFromJsonAsync<JsonElement>();
        openOnly.GetProperty("positions").GetArrayLength().Should().Be(2, "погашенная облигация скрыта");
    }

    [Fact]
    public async Task Income_PortfolioXirr_DoesNotDependOnIncludeClosed_AndCountMatchesRows()
    {
        var s = await SeedAsync();

        var all = await (await s.Client.GetAsync("/api/income")).Content.ReadFromJsonAsync<JsonElement>();
        var openOnly = await (await s.Client.GetAsync("/api/income?includeClosed=false")).Content.ReadFromJsonAsync<JsonElement>();

        openOnly.GetProperty("portfolioXirrFraction").GetDecimal()
            .Should().Be(all.GetProperty("portfolioXirrFraction").GetDecimal(), "XIRR портфеля считается по всем активам, фильтр — только вид таблицы");
        openOnly.GetProperty("totals").GetProperty("positionsCount").GetInt32()
            .Should().Be(openOnly.GetProperty("positions").GetArrayLength());
        all.GetProperty("totals").GetProperty("positionsCount").GetInt32()
            .Should().Be(all.GetProperty("positions").GetArrayLength());
    }

    [Fact]
    public async Task Income_Period_CutsMoneyEvents_AndPerPositionPeriodFields()
    {
        var s = await SeedAsync();
        var from = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-55));
        var to = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-25));

        var response = await s.Client.GetAsync($"/api/income?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        // В периоде [-55, -25]: купон 400 и налог −52 (день −50), комиссия −10 (день −30), выплата фонда на −60 не входит.
        var period = body.GetProperty("period");
        period.GetProperty("couponsGrossRub").GetDecimal().Should().Be(400m);
        period.GetProperty("distributionsGrossRub").GetDecimal().Should().Be(0m);
        period.GetProperty("taxesRub").GetDecimal().Should().Be(-52m);
        period.GetProperty("feesRub").GetDecimal().Should().Be(-10m);
        period.GetProperty("realizedPnlRub").GetDecimal().Should().Be(0m);

        var open = Position(body, "bond", s.OpenBondId);
        open.GetProperty("periodIncomeGrossRub").GetDecimal().Should().Be(400m);
        open.GetProperty("periodTaxesRub").GetDecimal().Should().Be(-52m);
        open.GetProperty("totalResultRub").GetDecimal().Should().Be(298m, "итог позиции — all-time, период его не режет");
        body.GetProperty("from").GetString().Should().Be($"{from:yyyy-MM-dd}");
    }

    [Theory]
    [InlineData("/api/income?from=2030-02-01&to=2030-01-01")]
    [InlineData("/api/income?assetKind=stocks")]
    [InlineData("/api/income/all/1/ledger")]
    public async Task Income_InvalidParams_Return422(string url)
    {
        var s = await SeedAsync();
        var response = await s.Client.GetAsync(url);
        response.StatusCode.Should().Be((HttpStatusCode)422);
    }

    [Fact]
    public async Task Ledger_ForClosedBond_ReturnsChronologyWithRunningState()
    {
        var s = await SeedAsync();

        var response = await s.Client.GetAsync($"/api/income/bond/{s.ClosedBondId}/ledger");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("status").GetString().Should().Be("redeemed");
        var entries = body.GetProperty("entries").EnumerateArray().ToList();
        entries.Select(e => e.GetProperty("kind").GetString()).Should().Equal("buy", "redemption");
        entries[0].GetProperty("quantityAfter").GetDecimal().Should().Be(10m);
        entries[0].GetProperty("avgCostAfterRub").GetDecimal().Should().Be(1_000m);
        entries[0].GetProperty("accruedRub").GetDecimal().Should().Be(0m);
        entries[1].GetProperty("quantityAfter").GetDecimal().Should().Be(0m);
        entries[1].GetProperty("realizedPnlRub").GetDecimal().Should().Be(100m);
        entries[1].GetProperty("amountRub").GetDecimal().Should().Be(10_100m);
    }

    [Fact]
    public async Task Ledger_ForFund_Works_AndMissingAssetIs404()
    {
        var s = await SeedAsync();

        var fund = await s.Client.GetAsync($"/api/income/fund/{s.FundId}/ledger");
        fund.StatusCode.Should().Be(HttpStatusCode.OK);
        (await fund.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("entries").GetArrayLength().Should().Be(5);

        (await s.Client.GetAsync("/api/income/bond/999999999/ledger")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        // Чужой актив (фонд под видом облигации) в журнале этого счёта не найден.
        (await s.Client.GetAsync($"/api/income/bond/{s.FundId}/ledger")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task OperationUpsert_UpdatesAccruedRub_OnRepeat()
    {
        var s = await SeedAsync();
        var repo = new OperationRepository(Conn);
        var ext = $"acc-{Guid.NewGuid():N}";
        Operation Op(decimal? accrued) => new()
        {
            AccountId = s.AccountId, InstrumentId = s.OpenBondId, Type = OperationType.Buy, Date = Ago(5),
            AmountRub = -1_000m, Quantity = 1, AccruedRub = accrued, ExternalId = ext,
        };

        await repo.UpsertManyByExternalIdAsync([Op(null)]);
        (await repo.GetByExternalIdAsync(ext))!.AccruedRub.Should().BeNull("старая строка — НКД неизвестен");

        await repo.UpsertManyByExternalIdAsync([Op(12.5m)]);
        (await repo.GetByExternalIdAsync(ext))!.AccruedRub.Should().Be(12.5m, "повторный синк заполняет НКД (это и есть бэкфилл)");
    }
}

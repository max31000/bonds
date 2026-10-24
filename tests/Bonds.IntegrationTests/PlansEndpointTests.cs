using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bonds.Core.Capital;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Bonds.Core.Policy;
using Bonds.Core.Telegram;
using Bonds.Infrastructure.Connectors.TInvest;
using Bonds.Infrastructure.Policy;
using Bonds.Infrastructure.Repositories;
using Bonds.IntegrationTests.Infrastructure;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using MySqlConnector;
using Xunit;
using BrokerIds = Bonds.Core.Models.Brokers;

namespace Bonds.IntegrationTests;

/// <summary>
/// Задача 63 на реальном MySQL: планы сделок (dry-run, БЕЗ отправки заявок) — inflow / withdraw / preview, переходы approve/reject, изоляция
/// пользователей, повторный прогон <see cref="PolicyRunner"/> без дубликатов, P2 по новому пополнению один раз, истечение PendingApproval. Стакан подменён
/// <see cref="IPlanQuoteSource"/> (сети нет). Бумаги — уникальные тикеры на тест (общая БД и общая таблица метаданных).
/// </summary>
[Collection("Integration")]
public class PlansEndpointTests
{
    private readonly TestWebApplicationFactory _factory;

    public PlansEndpointTests(TestWebApplicationFactory factory) => _factory = factory;

    private string Conn => _factory.Database.ConnectionString;

    private sealed class FakeQuotes : IPlanQuoteSource
    {
        public Dictionary<string, BookQuote> TInvest { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, BookQuote> Freedom { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<IReadOnlyDictionary<string, BookQuote>> GetTInvestAsync(IReadOnlyCollection<string> figis, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, BookQuote>>(TInvest.Where(p => figis.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value));

        public Task<IReadOnlyDictionary<string, BookQuote>> GetFreedomAsync(ulong userId, IReadOnlyCollection<string> tickers, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, BookQuote>>(Freedom.Where(p => tickers.Contains(p.Key, StringComparer.OrdinalIgnoreCase)).ToDictionary(p => p.Key, p => p.Value));
    }

    private (Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Factory, FakeQuotes Quotes) NewFactory(
        Dictionary<string, string?>? config = null, Action<IServiceCollection>? configure = null)
    {
        var quotes = new FakeQuotes();
        var factory = _factory.WithWebHostBuilder(b =>
        {
            if (config is not null) b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(config));
            b.ConfigureServices(s =>
            {
                s.RemoveAll<IPlanQuoteSource>();
                s.AddSingleton<IPlanQuoteSource>(quotes);
                configure?.Invoke(s);
            });
        });
        return (factory, quotes);
    }

    private async Task<(ulong UserId, HttpClient Client)> NewUserAsync(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory)
    {
        var telegramId = Random.Shared.NextInt64(1, long.MaxValue);
        var userId = await new UserRepository(Conn).CreateAsync(new User { TelegramId = telegramId });
        var client = factory.CreateClient();
        Mvv42TestAuth.Authorize(client, telegramId);
        return (userId, client);
    }

    /// <summary>
    /// T-Invest: счёт со свободными рублями, два фонда (A — прибыль, лот 1, шаг 0.01), корзины liquidity (auto_buy, члены A/B 0.5/0.5), bonds (engine) и
    /// temporary (hold). A: 100 паёв по 166.58 (вход 150), стакан 166.57/166.59; B: 1000.00 (999.99/1000.01).
    /// </summary>
    private async Task<(ulong UserId, HttpClient Client, ulong AccountId, string A, string B, FakeQuotes Quotes, Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Factory)>
        SeedRubAsync(decimal cashRub, decimal aQuantity = 100m)
    {
        var sfx = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        string a = $"FA{sfx}", b = $"FB{sfx}";

        // Тестовые фонды — «свои» (комиссия известна без журнала); сторонний фонд без сделки в журнале в авто-покупки не попадает (отдельный юнит-тест).
        var (factory, quotes) = NewFactory(new Dictionary<string, string?> { ["Policy:OwnFunds:100"] = a, ["Policy:OwnFunds:101"] = b });
        var (userId, client) = await NewUserAsync(factory);

        var accounts = new AccountRepository(Conn);
        var accountId = await accounts.CreateAsync(new Account { UserId = userId, Name = "Основной счёт", BrokerAccountId = "T-" + sfx });
        await accounts.UpdateBalanceAsync(accountId, cashRub, cashRub + 16_658m, DateTime.UtcNow);

        var funds = new FundRepository(Conn);
        var fundA = await funds.UpsertAsync(new Fund { Figi = $"FIGIA{sfx}", Isin = $"RUA{sfx}", Ticker = a, Name = "Фонд A" });
        var fundB = await funds.UpsertAsync(new Fund { Figi = $"FIGIB{sfx}", Isin = $"RUB{sfx}", Ticker = b, Name = "Фонд B" });
        var positions = new FundPositionRepository(Conn);
        if (aQuantity > 0m)
            await positions.UpsertAsync(new FundPosition { AccountId = accountId, FundId = fundA, Quantity = aQuantity, AvgPurchasePriceRub = 150m, CurrentPriceRub = 166.58m, PriceAsOf = DateTime.UtcNow });
        _ = fundB;

        var meta = new InstrumentTradingMetaRepository(Conn);
        foreach (var (ticker, figi, step) in new[] { (a, $"FIGIA{sfx}", 0.01m), (b, $"FIGIB{sfx}", 0.01m) })
        {
            await meta.UpsertAsync(new InstrumentTradingMeta
            {
                Broker = BrokerIds.TInvest, InstrumentKey = $"ISIN{ticker}", Ticker = ticker, Figi = figi, ClassCode = "TQBR", Lot = 1m,
                MinPriceIncrement = step, Currency = "RUB", ApiTradeAvailable = true, BuyAvailable = true, SellAvailable = true,
                PaysDistributions = false, // фонды без выплат — проходят белый список авто (задача 67)
                UpdatedAt = DateTime.UtcNow,
            });
        }

        quotes.TInvest[$"FIGIA{sfx}"] = new BookQuote(166.57m, 166.59m, 166.58m);
        quotes.TInvest[$"FIGIB{sfx}"] = new BookQuote(999.99m, 1000.01m, 1000m);

        await new CapitalBasketRepository(Conn).SeedIfEmptyAsync(userId,
        [
            Basket(userId, "liquidity", "Ликвидность", 0.15m, 0, BasketFillRules.Fixed, 10, true, true, Member(a, 0.5m), Member(b, 0.5m)),
            Basket(userId, "bonds", "Облигации", 0.85m, 2, BasketFillRules.Engine, 20, false, false, new CapitalBasketMember { Broker = BrokerIds.TInvest, InstrumentKey = "bond", MatchRule = BasketMatchRules.AssetClass }),
            Basket(userId, "temporary", "Временные", null, 1, BasketFillRules.Hold, 30, false, false),
        ]);

        return (userId, client, accountId, a, b, quotes, factory);
    }

    private static CapitalBasket Basket(
        ulong userId, string code, string title, decimal? target, int tier, string fill, int sort, bool autoBuy, bool autoSell, params CapitalBasketMember[] members) => new()
    {
        UserId = userId, Portfolio = CapitalPortfolios.Rub, Code = code, Title = title, TargetWeight = target, Band = 0.05m, HardBand = 0.10m,
        LiquidityTier = tier, FillRule = fill, AutoBuy = autoBuy, AutoSell = autoSell, SortOrder = sort, Members = [.. members],
    };

    private static CapitalBasketMember Member(string key, decimal sub) => new() { Broker = BrokerIds.TInvest, InstrumentKey = key, SubWeight = sub, MatchRule = BasketMatchRules.Instrument };

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    // ─── inflow / withdraw / preview ───────────────────────────────────────────────────────

    [Fact]
    public async Task Inflow_BuildsPendingDryRunPlan_ForLiquidityShare_AndStoresIt()
    {
        var (_, client, _, a, _, _, _) = await SeedRubAsync(cashRub: 7_340m, aQuantity: 0m);

        var response = await client.PostAsJsonAsync("/api/plans/inflow", new { amount = 7_340m });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ReadAsync(response);
        body.GetProperty("dryRun").GetBoolean().Should().BeTrue();
        body.GetProperty("disclaimer").GetString().Should().Contain("сделки не отправляются");
        var plan = body.GetProperty("plan");
        plan.GetProperty("rule").GetString().Should().Be("P2_INFLOW");
        plan.GetProperty("status").GetString().Should().Be("PendingApproval", "ручной план всегда ждёт подтверждения");
        plan.GetProperty("dryRun").GetBoolean().Should().BeTrue();
        var line = plan.GetProperty("lines").EnumerateArray().First(l => l.GetProperty("ticker").GetString() == a);
        line.GetProperty("side").GetString().Should().Be("Buy");
        line.GetProperty("limitPrice").GetDecimal().Should().Be(166.58m, "mid 166.58, покупка вниз к шагу 0.01");
        line.GetProperty("quantityLots").GetInt64().Should().BeGreaterThan(0);
        line.GetProperty("estFee").GetDecimal().Should().Be(0m, "тестовые фонды — фонды управляющей компании (Policy:OwnFunds): комиссия 0");
        plan.GetProperty("summary").GetProperty("cashBefore").GetDecimal().Should().Be(7_340m);
        plan.GetProperty("summary").GetProperty("buyTotal").GetDecimal().Should().BeLessThanOrEqualTo(7_340m);

        var list = await ReadAsync(await client.GetAsync("/api/plans"));
        list.GetProperty("items").EnumerateArray().Should().ContainSingle(p => p.GetProperty("id").GetUInt64() == plan.GetProperty("id").GetUInt64());
        list.GetProperty("dryRun").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Withdraw_SellsFundWithTax_ToReachNetAmount()
    {
        var (_, client, _, a, _, _, _) = await SeedRubAsync(cashRub: 0m);

        var response = await client.PostAsJsonAsync("/api/plans/withdraw", new { amount = 5_000m, portfolio = "rub" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var plan = (await ReadAsync(response)).GetProperty("plan");
        plan.GetProperty("rule").GetString().Should().Be("P5_WITHDRAW");
        var line = plan.GetProperty("lines").EnumerateArray().Single();
        line.GetProperty("ticker").GetString().Should().Be(a);
        line.GetProperty("side").GetString().Should().Be("Sell");
        line.GetProperty("limitPrice").GetDecimal().Should().Be(166.58m);
        line.GetProperty("estTax").GetDecimal().Should().BePositive("цена входа 150 ниже цены продажи 166.58");
        var summary = plan.GetProperty("summary");
        summary.GetProperty("requestedNet").GetDecimal().Should().Be(5_000m);
        summary.GetProperty("netToWithdraw").GetDecimal().Should().BeGreaterThanOrEqualTo(5_000m);
        summary.GetProperty("shortfall").ValueKind.Should().Be(JsonValueKind.Null);
        summary.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()).Should().Contain(w => w!.Contains("средней цене входа"));
    }

    [Fact]
    public async Task Preview_BuildsWithoutSaving_AndValidatesRule()
    {
        var (_, client, _, _, _, _, _) = await SeedRubAsync(cashRub: 7_340m, aQuantity: 0m);

        var response = await client.PostAsJsonAsync("/api/plans/preview", new { rule = "P1_SWEEP" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ReadAsync(response);
        var plan = body.GetProperty("plan");
        plan.GetProperty("id").GetUInt64().Should().Be(0, "предпросмотр не сохраняется");
        plan.GetProperty("rule").GetString().Should().Be("P1_SWEEP");
        plan.GetProperty("summary").GetProperty("executionNote").GetString().Should().Contain("dry-run");
        (await ReadAsync(await client.GetAsync("/api/plans"))).GetProperty("items").GetArrayLength().Should().Be(0);

        (await client.PostAsJsonAsync("/api/plans/preview", new { rule = "NOPE" })).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await client.PostAsJsonAsync("/api/plans/preview", new { })).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Validation_Returns422_ForBadInflowWithdrawAndExit()
    {
        var (_, client, _, _, _, _, _) = await SeedRubAsync(cashRub: 7_340m);

        (await client.PostAsJsonAsync("/api/plans/inflow", new { amount = 0m })).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await client.PostAsJsonAsync("/api/plans/inflow", new { amount = 100m, portfolio = "eur" })).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await client.PostAsJsonAsync("/api/plans/withdraw", new { })).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await client.PostAsJsonAsync("/api/plans/withdraw", new { amount = 1m, fraction = 0.1m })).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await client.PostAsJsonAsync("/api/plans/withdraw", new { fraction = 1.5m })).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await client.PostAsJsonAsync("/api/plans/exit-basket", new { code = "liquidity" })).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, "у корзины есть цель");
        (await client.PostAsJsonAsync("/api/plans/exit-basket", new { code = "nope" })).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await client.GetAsync("/api/plans?status=Bogus")).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task ExitBasket_TemporaryBasket_SellsAllMembers()
    {
        var (userId, client, accountId, _, _, quotes, _) = await SeedRubAsync(cashRub: 0m, aQuantity: 0m);
        var sfx = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var tpay = $"FT{sfx}";
        var fundId = await new FundRepository(Conn).UpsertAsync(new Fund { Figi = $"FIGIT{sfx}", Isin = $"RUT{sfx}", Ticker = tpay, Name = "Временный" });
        await new FundPositionRepository(Conn).UpsertAsync(new FundPosition { AccountId = accountId, FundId = fundId, Quantity = 50m, AvgPurchasePriceRub = 100m, CurrentPriceRub = 100m, PriceAsOf = DateTime.UtcNow });
        await new InstrumentTradingMetaRepository(Conn).UpsertAsync(new InstrumentTradingMeta
        {
            Broker = BrokerIds.TInvest, InstrumentKey = $"ISIN{tpay}", Ticker = tpay, Figi = $"FIGIT{sfx}", ClassCode = "TQBR", Lot = 1m, MinPriceIncrement = 0.01m, Currency = "RUB",
            ApiTradeAvailable = true, BuyAvailable = true, SellAvailable = true, UpdatedAt = DateTime.UtcNow,
        });
        quotes.TInvest[$"FIGIT{sfx}"] = new BookQuote(99.99m, 100.01m, 100m);
        var temporaryId = (await new CapitalBasketRepository(Conn).GetByUserAsync(userId)).Single(b => b.Code == "temporary").Id;
        (await new CapitalBasketRepository(Conn).AddMemberAsync(userId, temporaryId, Member(tpay, 1m))).Should().BeGreaterThan(0UL);

        var response = await client.PostAsJsonAsync("/api/plans/exit-basket", new { code = "temporary" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var plan = (await ReadAsync(response)).GetProperty("plan");
        var line = plan.GetProperty("lines").EnumerateArray().Single();
        line.GetProperty("ticker").GetString().Should().Be(tpay);
        line.GetProperty("quantityLots").GetInt64().Should().Be(50);
        plan.GetProperty("status").GetString().Should().Be("PendingApproval");
    }

    [Fact]
    public async Task Inflow_EngineBasket_BuysGreenBondCandidate_WithRealLotAndPointsGrid()
    {
        var marker = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        string secid = $"PB{marker}", isin = $"IPB{marker}000", figi = $"FIGIPB{marker}";
        var conn = Conn;

        // Брокер знает лот 2 и шаг 0.01 пункта, номинал 1000 ₽; остальные ISIN у брокера не находятся.
        var tInvest = new Mock<ITInvestPortfolioClient>();
        tInvest.Setup(c => c.GetInstrumentListingsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(new List<TInvestInstrumentListing>());
        tInvest.Setup(c => c.GetInstrumentListingsAsync(isin, It.IsAny<CancellationToken>())).ReturnsAsync(new List<TInvestInstrumentListing>
        {
            new()
            {
                Uid = "uid-" + marker, Figi = figi, Ticker = secid, ClassCode = "TQCB", Isin = isin, IsBond = true, Lot = 2m, MinPriceIncrementRaw = 0.01m,
                Nominal = 1000m, NominalCurrency = "RUB", Currency = "RUB", ApiTradeAvailable = true, BuyAvailable = true, SellAvailable = true,
            },
        });
        var (factory, quotes) = NewFactory(configure: s =>
        {
            s.RemoveAll<ITInvestPortfolioClient>();
            s.AddScoped(_ => tInvest.Object);
        });
        quotes.TInvest[figi] = new BookQuote(99.90m, 100.10m, 100m);
        var (userId, client) = await NewUserAsync(factory);
        var accounts = new AccountRepository(conn);
        var accountId = await accounts.CreateAsync(new Account { UserId = userId, Name = "Основной счёт", BrokerAccountId = "T-" + marker });
        await accounts.UpdateBalanceAsync(accountId, 100_000m, 100_000m, DateTime.UtcNow);
        await new CapitalBasketRepository(conn).SeedIfEmptyAsync(userId,
        [
            Basket(userId, "bonds", "Облигации", 1m, 2, BasketFillRules.Engine, 20, false, false,
                new CapitalBasketMember { Broker = BrokerIds.TInvest, InstrumentKey = "bond", MatchRule = BasketMatchRules.AssetClass }),
        ]);

        // Green: здоровый эмитент, листинг 1, живая ликвидность. Доходность выше любых соседей по общей БД, чтобы попасть в верхушку пула.
        var entry = new BondUniverseEntry
        {
            Secid = secid, Isin = isin, ShortName = "BOND " + secid, SecName = "Full " + secid, FaceValue = 1000m, Sector = "Sector" + marker,
            YieldFraction = 0.449m, DurationYears = 2m, PricePercent = 100m, AccruedRub = 10m, TurnoverRub = 10_000_000m, BidPercent = 99.9m, OfferPercent = 100m,
            NumTrades = 10, ListLevel = 1, GspreadApproxFraction = 0.349m, MaturityDate = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(3)), UpdatedAt = DateTime.UtcNow,
        };
        try
        {
            await new BondUniverseRepository(conn).UpsertSnapshotBatchAsync([entry]);
            await ReliabilitySeed.SeedHealthyIssuerAsync(conn, secid, isin);

            var response = await client.PostAsJsonAsync("/api/plans/inflow", new { amount = 50_000m });

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var body = await ReadAsync(response);
            body.GetProperty("plan").ValueKind.Should().NotBe(JsonValueKind.Null, body.GetProperty("notes").ToString());
            var plan = body.GetProperty("plan");
            plan.GetProperty("status").GetString().Should().Be("PendingApproval", "облигационная часть — всегда на подтверждение");
            var line = plan.GetProperty("lines").EnumerateArray().Single(l => l.GetProperty("ticker").GetString() == secid);
            line.GetProperty("side").GetString().Should().Be("Buy");
            line.GetProperty("lotSize").GetDecimal().Should().Be(2m, "реальный лот из метаданных брокера, а не 1");
            line.GetProperty("quantityLots").GetInt64().Should().Be(1);
            line.GetProperty("quantityPieces").GetDecimal().Should().Be(2m);
            line.GetProperty("limitPricePoints").GetDecimal().Should().Be(100.00m, "mid 100.00 пунктов, шаг 0.01 пункта");
            line.GetProperty("limitPrice").GetDecimal().Should().Be(1000m, "100 пунктов × номинал 1000 ₽ / 100");
            line.GetProperty("estAmount").GetDecimal().Should().Be(2_020m, "2 шт × (чистая 1000 + НКД 10)");
        }
        finally
        {
            await using var db = new MySqlConnection(conn);
            await db.ExecuteAsync("DELETE FROM bond_universe WHERE secid = @Secid", new { Secid = secid });
            await db.ExecuteAsync("DELETE FROM instrument_trading_meta WHERE broker = 'tinvest' AND instrument_key = @Isin", new { Isin = isin });
        }
    }

    // ─── approve / reject ──────────────────────────────────────────────────────────────────

    private static async Task<ulong> CreateWithdrawPlanAsync(HttpClient client)
    {
        var body = await ReadAsync(await client.PostAsJsonAsync("/api/plans/withdraw", new { amount = 1_000m }));
        return body.GetProperty("plan").GetProperty("id").GetUInt64();
    }

    [Fact]
    public async Task Approve_InDryRun_MarksApprovedWithNote_AndSecondDecisionConflicts()
    {
        var (_, client, _, _, _, _, _) = await SeedRubAsync(cashRub: 0m);
        var id = await CreateWithdrawPlanAsync(client);

        var approve = await client.PostAsync($"/api/plans/{id}/approve", null);

        approve.StatusCode.Should().Be(HttpStatusCode.OK);
        var plan = await ReadAsync(approve);
        plan.GetProperty("status").GetString().Should().Be("Approved");
        plan.GetProperty("decidedVia").GetString().Should().Be("web");
        plan.GetProperty("decidedAt").ValueKind.Should().Be(JsonValueKind.String);
        plan.GetProperty("dryRun").GetBoolean().Should().BeTrue();
        plan.GetProperty("summary").GetProperty("executionNote").GetString().Should().Be("не исполнен: dry-run");

        (await client.PostAsync($"/api/plans/{id}/approve", null)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await client.PostAsync($"/api/plans/{id}/reject", null)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadAsync(await client.GetAsync($"/api/plans/{id}"))).GetProperty("status").GetString().Should().Be("Approved");
    }

    [Fact]
    public async Task Reject_MarksRejected_AndFiltersByStatus()
    {
        var (_, client, _, _, _, _, _) = await SeedRubAsync(cashRub: 0m);
        var rejected = await CreateWithdrawPlanAsync(client);
        var pending = await CreateWithdrawPlanAsync(client);

        var reject = await client.PostAsync($"/api/plans/{rejected}/reject", null);

        reject.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadAsync(reject)).GetProperty("status").GetString().Should().Be("Rejected");
        (await client.PostAsync($"/api/plans/{rejected}/approve", null)).StatusCode.Should().Be(HttpStatusCode.Conflict);

        var onlyPending = await ReadAsync(await client.GetAsync("/api/plans?status=PendingApproval"));
        onlyPending.GetProperty("items").EnumerateArray().Select(p => p.GetProperty("id").GetUInt64()).Should().Equal(pending);
    }

    [Fact]
    public async Task Approve_ExpiredPlan_Returns409AndMarksExpired()
    {
        var (_, client, _, _, _, _, _) = await SeedRubAsync(cashRub: 0m);
        var id = await CreateWithdrawPlanAsync(client);
        await using (var conn = new MySqlConnection(Conn))
            await conn.ExecuteAsync("UPDATE trade_plans SET expires_at = @Past WHERE id = @Id", new { Past = DateTime.UtcNow.AddMinutes(-5), Id = id });

        var response = await client.PostAsync($"/api/plans/{id}/approve", null);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadAsync(await client.GetAsync($"/api/plans/{id}"))).GetProperty("status").GetString().Should().Be("Expired");
    }

    [Fact]
    public async Task Plans_AreIsolatedBetweenUsers()
    {
        var (_, owner, _, _, _, _, factory) = await SeedRubAsync(cashRub: 0m);
        var id = await CreateWithdrawPlanAsync(owner);
        var (_, stranger) = await NewUserAsync(factory);

        (await stranger.GetAsync($"/api/plans/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await stranger.PostAsync($"/api/plans/{id}/approve", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await stranger.PostAsync($"/api/plans/{id}/reject", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadAsync(await stranger.GetAsync("/api/plans"))).GetProperty("items").GetArrayLength().Should().Be(0);
        (await ReadAsync(await owner.GetAsync($"/api/plans/{id}"))).GetProperty("status").GetString().Should().Be("PendingApproval");
    }

    // ─── PolicyRunner ──────────────────────────────────────────────────────────────────────

    private static DateTime OpenNowUtc()
    {
        var d = DateTime.UtcNow.Date;
        while (d.DayOfWeek != DayOfWeek.Wednesday || d > DateTime.UtcNow.Date) d = d.AddDays(-1);
        return d.AddHours(9); // среда 12:00 МСК — Мосбиржа открыта
    }

    private async Task<List<TradePlan>> PlansOfAsync(ulong userId) =>
        (await new TradePlanRepository(Conn).ListAsync(userId, null, 100)).ToList();

    [Fact]
    public async Task Runner_P1_AutoApprovesWithinLimits_AndSecondRunDoesNotDuplicate()
    {
        var (userId, _, _, _, _, _, factory) = await SeedRubAsync(cashRub: 7_340m, aQuantity: 0m);
        var now = OpenNowUtc();

        for (var i = 0; i < 2; i++)
        {
            using var scope = factory.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<PolicyRunner>().RunForUserAsync(userId, now, new PolicyRunResult());
        }

        // Пауза повтора: после истечения плана тот же план не возвращается сразу (иначе без исполнителя каждый цикл рождал бы новый).
        await using (var db = new MySqlConnection(Conn))
            await db.ExecuteAsync("UPDATE trade_plans SET expires_at = @Past WHERE user_id = @UserId", new { Past = now.AddMinutes(-1), UserId = userId });
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<PolicyRunner>().RunForUserAsync(userId, now, new PolicyRunResult());

        var plans = await PlansOfAsync(userId);
        var p1 = plans.Where(p => p.Rule == PlanRules.P1Sweep).Should().ContainSingle("повтор в паузу не создаёт новый план").Subject;
        p1.Status.Should().Be(PlanStatuses.AutoApproved, "корзина auto_buy, суммы в пределах лимитов из конфигурации");
        p1.DecidedVia.Should().Be("auto");
        p1.DryRun.Should().BeTrue();
        p1.Lines.Should().NotBeEmpty().And.OnlyContain(l => l.Side == PlanSides.Buy);
    }

    [Fact]
    public async Task Runner_AutoApprovedPlan_SendsInformationalTelegramMessage_WithoutButtons()
    {
        var (userId, _, _, _, _, _, factory) = await SeedRubAsync(cashRub: 7_340m, aQuantity: 0m);
        var telegramId = (await new UserRepository(Conn).GetByIdAsync(userId))!.TelegramId;
        var now = OpenNowUtc();

        for (var i = 0; i < 2; i++)
        {
            using var scope = factory.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<PolicyRunner>().RunForUserAsync(userId, now, new PolicyRunResult());
        }

        var plans = await PlansOfAsync(userId);
        plans.Should().NotBeEmpty().And.OnlyContain(p => p.Status == PlanStatuses.AutoApproved);
        var sent = factory.Services.GetRequiredService<FakeTelegramBotClient>().SentTo(telegramId);
        sent.Should().HaveCount(plans.Count, "одно сообщение на план, повторный прогон дубликатов не создаёт");
        sent.Should().OnlyContain(m => m.Keyboard == null && m.Html.Contains("Авто-подтверждено"));
    }

    [Fact]
    public async Task Runner_WhenPaused_CreatesOnlyPendingApproval_AndNotifiesOncePerPlanWithButtons()
    {
        var (userId, _, _, _, _, _, factory) = await SeedRubAsync(cashRub: 7_340m, aQuantity: 0m);
        var telegramId = (await new UserRepository(Conn).GetByIdAsync(userId))!.TelegramId;
        await new UserSettingsRepository(Conn).SetExecutionPausedAsync(userId, true);
        var now = OpenNowUtc();

        for (var i = 0; i < 2; i++)
        {
            using var scope = factory.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<PolicyRunner>().RunForUserAsync(userId, now, new PolicyRunResult());
        }

        // Пауза (/stop): та же ситуация, что в тесте авто-подтверждения, но ни одного AutoApproved — только PendingApproval без решения.
        var plans = await PlansOfAsync(userId);
        plans.Should().NotBeEmpty();
        plans.Should().OnlyContain(p => p.Status == PlanStatuses.PendingApproval && p.DecidedVia == null && p.DecidedAt == null);
        plans.Should().OnlyContain(p => !string.IsNullOrEmpty(p.BotNonce));

        var sent = factory.Services.GetRequiredService<FakeTelegramBotClient>().SentTo(telegramId);
        sent.Should().HaveCount(plans.Count, "ровно одно сообщение на план");
        foreach (var plan in plans)
        {
            var message = sent.Should().ContainSingle(m => m.Html.Contains($"План №{plan.Id}")).Subject;
            message.Html.Should().Contain("режим наблюдения: сделка не отправляется");
            var data = message.Keyboard!.Rows.SelectMany(r => r).Select(b => b.CallbackData).Where(d => d != null).ToList();
            data.Should().Equal(
                BotCallback.Build((ulong)plan.Id, BotPlanAction.Approve, plan.BotNonce!),
                BotCallback.Build((ulong)plan.Id, BotPlanAction.Reject, plan.BotNonce!));
        }
    }

    [Fact]
    public async Task Runner_P2_NewDeposit_PlansOnce_BeforeP1ParksTheRest()
    {
        var (userId, _, accountId, _, _, _, factory) = await SeedRubAsync(cashRub: 7_340m, aQuantity: 0m);
        var now = OpenNowUtc();
        await new AccountCashMovementRepository(Conn).UpsertManyAsync([new AccountCashMovement
        {
            AccountId = accountId, ExternalId = "dep-" + Guid.NewGuid().ToString("N"), Date = now.AddHours(-2), Type = CashMovementType.Deposit, Amount = 5_000m, AmountRub = 5_000m, Currency = "RUB",
        }]);

        for (var i = 0; i < 2; i++)
        {
            using var scope = factory.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<PolicyRunner>().RunForUserAsync(userId, now, new PolicyRunResult());
        }

        var plans = await PlansOfAsync(userId);
        plans.Where(p => p.Rule == PlanRules.P2Inflow).Should().ContainSingle("пополнение планируется один раз").Which.Trigger.Should().Contain("5000");
        plans.Where(p => p.Rule == PlanRules.P1Sweep).Should().ContainSingle();
        var reserved = plans.SelectMany(p => p.Lines).Where(l => l.Side == PlanSides.Buy).Sum(l => l.EstAmount + l.EstFee);
        reserved.Should().BeLessThanOrEqualTo(7_340m, "планы не тратят больше свободных денег (резерв учитывается следующими правилами)");
    }

    [Fact]
    public async Task Runner_ExpiresStalePendingPlans()
    {
        var (userId, client, _, _, _, _, factory) = await SeedRubAsync(cashRub: 0m);
        var id = await CreateWithdrawPlanAsync(client);
        await using (var conn = new MySqlConnection(Conn))
            await conn.ExecuteAsync("UPDATE trade_plans SET expires_at = @Past WHERE id = @Id", new { Past = DateTime.UtcNow.AddMinutes(-1), Id = id });

        using var scope = factory.Services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<PolicyRunner>().RunAllAsync(DateTime.UtcNow);

        result.Expired.Should().BeGreaterThanOrEqualTo(1);
        (await new TradePlanRepository(Conn).GetAsync(userId, id))!.Status.Should().Be(PlanStatuses.Expired);
    }

    [Fact]
    public async Task Runner_DryRunStaysTrue_EvenIfConfigSaysFalse()
    {
        var sfx = Guid.NewGuid().ToString("N")[..8];
        var (factory, _) = NewFactory(new Dictionary<string, string?> { ["Execution:DryRun"] = "false" });
        var (userId, client) = await NewUserAsync(factory);
        var accounts = new AccountRepository(Conn);
        var accountId = await accounts.CreateAsync(new Account { UserId = userId, Name = "Основной счёт", BrokerAccountId = "T-" + sfx });
        await accounts.UpdateBalanceAsync(accountId, 0m, 0m, DateTime.UtcNow);

        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<PolicyService>();
        var saved = await service.SaveAsync(userId, new TradePlanDraft(
            PlanRules.Manual, "тест", PlanStatuses.PendingApproval, DateTime.UtcNow.AddMinutes(30), [], new TradePlanSummary()), DateTime.UtcNow, forcePending: false);

        saved.DryRun.Should().BeTrue("в фазе 2 исполнения нет — dry-run принудительный");
        (await ReadAsync(await client.GetAsync("/api/plans"))).GetProperty("dryRun").GetBoolean().Should().BeTrue();
    }

    // ─── Freedom: P7 ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Preview_P7_FreedomTopUp_PicksCheaperRouteTicker_AndPricesInUsd()
    {
        var sfx = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        string small = $"SP{sfx}.US", large = $"VU{sfx}.EU";
        var (factory, quotes) = NewFactory(new Dictionary<string, string?>
        {
            ["Policy:FreedomSmallTopUpTicker"] = small,
            ["Policy:FreedomLargeTopUpTicker"] = large,
        });
        var (userId, client) = await NewUserAsync(factory);

        var accountId = await new AccountRepository(Conn).CreateAsync(new Account { UserId = userId, Broker = BrokerIds.Freedom, Name = "Freedom", BrokerAccountId = "F-" + sfx });
        await new BrokerCashRepository(Conn).UpsertAsync(new BrokerCash { AccountId = accountId, Currency = "USD", Amount = 400m, SyncedAt = DateTime.UtcNow });
        var meta = new InstrumentTradingMetaRepository(Conn);
        foreach (var (ticker, step) in new[] { (small, 0.01m), (large, 0.01m) })
        {
            await meta.UpsertAsync(new InstrumentTradingMeta
            {
                Broker = BrokerIds.Freedom, InstrumentKey = ticker, Ticker = ticker, Lot = 1m, MinPriceIncrement = step, Currency = "USD",
                ApiTradeAvailable = true, BuyAvailable = true, SellAvailable = true, ItsTwin = false, UpdatedAt = DateTime.UtcNow,
            });
        }

        quotes.Freedom[small] = new BookQuote(72.00m, 72.02m, 72m);
        quotes.Freedom[large] = new BookQuote(110.00m, 110.04m, 110m);
        await new CapitalBasketRepository(Conn).SeedIfEmptyAsync(userId,
        [
            new CapitalBasket
            {
                UserId = userId, Portfolio = CapitalPortfolios.Usd, Code = "global_core", Title = "Глобальное ядро", TargetWeight = 1m, Band = 0.05m, HardBand = 0.10m,
                LiquidityTier = 1, FillRule = BasketFillRules.Fixed, SortOrder = 110,
                Members = [new CapitalBasketMember { Broker = BrokerIds.Freedom, InstrumentKey = large, SubWeight = 0.8m }, new CapitalBasketMember { Broker = BrokerIds.Freedom, InstrumentKey = small, SubWeight = 0.2m }],
            },
        ]);

        var body = await ReadAsync(await client.PostAsJsonAsync("/api/plans/preview", new { rule = "P7_FREEDOM_TOPUP" }));

        var plan = body.GetProperty("plan");
        plan.GetProperty("status").GetString().Should().Be("PendingApproval", "авто на Freedom = 0");
        var line = plan.GetProperty("lines").EnumerateArray().Single();
        line.GetProperty("ticker").GetString().Should().Be(large, "$400 выше точки безубыточности ≈ $315.79 — маршрут Europe");
        line.GetProperty("broker").GetString().Should().Be("freedom");
        line.GetProperty("currency").GetString().Should().Be("USD");
        line.GetProperty("quantityLots").GetInt64().Should().Be(3);
        line.GetProperty("limitPrice").GetDecimal().Should().Be(110.02m);
        line.GetProperty("estFee").GetDecimal().Should().Be(1.60m);
        plan.GetProperty("summary").GetProperty("currency").GetString().Should().Be("USD");
    }

    // ─── Фиксы по ревью: лимит дня, резерв в валюте портфеля, AutoApproved, дубликаты до сбора облигаций ───────────

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private static TradePlan NewPlan(
        ulong userId, string status, DateTime nowUtc, DateTime? expiresAt = null, string rule = PlanRules.P1Sweep, string trigger = "тест",
        string broker = BrokerIds.TInvest, string currency = "RUB", decimal amount = 10_000m, decimal fee = 0m, TradePlanSummary? summary = null) => new()
    {
        UserId = userId, Rule = rule, Trigger = trigger, Status = status, DryRun = true, CreatedAt = nowUtc, ExpiresAt = expiresAt ?? nowUtc.AddMinutes(30),
        DecidedAt = status is PlanStatuses.AutoApproved or PlanStatuses.Done or PlanStatuses.Submitting ? nowUtc : null,
        DecidedVia = status is PlanStatuses.AutoApproved ? "auto" : null,
        SummaryJson = JsonSerializer.Serialize(summary ?? new TradePlanSummary { Currency = currency }, WebJson),
        Lines =
        [
            new TradePlanLine
            {
                LineNo = 1, Broker = broker, AccountId = 1, InstrumentKey = "K", Ticker = "K", Side = PlanSides.Buy, QuantityLots = 1, LotSize = 1m, LimitPrice = amount,
                EstAmount = amount, EstFee = fee, Currency = currency, Reason = "тест",
            },
        ],
    };

    [Fact]
    public async Task DayLimit_InDryRun_AutoApprovedPlansDoNotCount_ButDoneOnesDo()
    {
        var (factory, _) = NewFactory();
        var (userId, _) = await NewUserAsync(factory);
        var repo = new TradePlanRepository(Conn);
        var now = DateTime.UtcNow;
        await repo.InsertAsync(NewPlan(userId, PlanStatuses.AutoApproved, now, amount: 10_000m)); // в dry-run никогда не исполняется — деньги не потрачены
        await repo.InsertAsync(Auto(NewPlan(userId, PlanStatuses.Done, now, amount: 5_000m)));
        await repo.InsertAsync(Auto(NewPlan(userId, PlanStatuses.Submitting, now, amount: 1_000m)));
        await repo.InsertAsync(NewPlan(userId, PlanStatuses.PendingApproval, now, amount: 77_000m));

        var sum = await repo.SumAutoApprovedRubSinceAsync(userId, now.AddHours(-1));

        sum.Should().Be(6_000m, "только Submitting/Done; AutoApproved в dry-run не считается");
    }

    private static TradePlan Auto(TradePlan plan)
    {
        plan.DecidedVia = DecidedVia.Auto;
        return plan;
    }

    [Fact]
    public async Task T67_DayLimit_ManualBuy200k_DoesNotEatAutoLimit()
    {
        // Задача 67 (#10): ручная покупка на 200 000 ₽ (подтверждена в вебе, исполнена) не входит в суточный авто-лимит — ни в оценку раннера, ни в факт исполнителя.
        var (factory, _) = NewFactory();
        var (userId, _) = await NewUserAsync(factory);
        var repo = new TradePlanRepository(Conn);
        var now = DateTime.UtcNow;
        var manual = NewPlan(userId, PlanStatuses.Done, now, rule: PlanRules.P2Inflow, amount: 200_000m);
        manual.DecidedVia = DecidedVia.Web;
        manual.DryRun = false;
        await repo.InsertAsync(manual);
        await repo.InsertAsync(Auto(NewPlan(userId, PlanStatuses.Done, now, amount: 3_000m)));

        (await repo.SumAutoApprovedRubSinceAsync(userId, now.AddHours(-1))).Should().Be(3_000m);
        (await repo.ListAutoDayPlansAsync(userId, now.AddHours(-1))).Should().ContainSingle().Which.DecidedVia.Should().Be(DecidedVia.Auto);
    }

    [Fact]
    public async Task ReservedBuys_AreTakenFromSummaryInPortfolioCurrency_NotFromMixedCurrencyLines()
    {
        var (factory, _) = NewFactory();
        var (userId, _) = await NewUserAsync(factory);
        var repo = new TradePlanRepository(Conn);
        var now = DateTime.UtcNow;
        // Freedom: строка в EUR (300.06) + комиссия в EUR (1.4545), портфель USD: резерв 331.666 USD (курс 1.1, комиссия 1.60 USD).
        await repo.InsertAsync(NewPlan(userId, PlanStatuses.PendingApproval, now, broker: BrokerIds.Freedom, currency: "EUR", amount: 300.06m, fee: 1.4545m,
            summary: new TradePlanSummary { Currency = "USD", ReservedBuy = 331.666m }));
        // Approved dry-run деньги не резервирует; просроченный план — тоже.
        await repo.InsertAsync(NewPlan(userId, PlanStatuses.Approved, now, broker: BrokerIds.Freedom, currency: "EUR", summary: new TradePlanSummary { Currency = "USD", ReservedBuy = 999m }));
        await repo.InsertAsync(NewPlan(userId, PlanStatuses.PendingApproval, now.AddHours(-2), expiresAt: now.AddHours(-1), broker: BrokerIds.Freedom, currency: "EUR",
            summary: new TradePlanSummary { Currency = "USD", ReservedBuy = 888m }));

        (await repo.SumReservedBuysAsync(userId, BrokerIds.Freedom, "USD", now)).Should().Be(331.666m);
        (await repo.SumReservedBuysAsync(userId, BrokerIds.Freedom, "RUB", now)).Should().Be(0m, "другая валюта портфеля");
        (await repo.SumReservedBuysAsync(userId, BrokerIds.TInvest, "USD", now)).Should().Be(0m, "другой брокер");
    }

    [Fact]
    public async Task T67_ApprovedLivePlanFromEvening_ReservesCash_P1At10SeesFreeCashMinusReserve()
    {
        // Вечером (вторник 22:00 МСК) одобрен живой план облигаций на 50 000 ₽ — он ждёт открытия торгов в Approved до 18:30 среды. В среду 10:00 МСК P1 видит
        // свободные 60 000 − 50 000 = 10 000 ₽, а не все 60 000 (иначе P1 «увёл» бы деньги, под которые утром пойдут заявки облигаций).
        var (userId, _, _, _, _, _, factory) = await SeedRubAsync(cashRub: 60_000m, aQuantity: 0m);
        var wednesdayNoon = OpenNowUtc();
        var evening = wednesdayNoon.AddDays(-1).AddHours(10); // вторник 19:00 UTC = 22:00 МСК
        var at10 = wednesdayNoon.AddHours(-2); // среда 07:00 UTC = 10:00 МСК
        var repo = new TradePlanRepository(Conn);

        var live = NewPlan(userId, PlanStatuses.Approved, evening, expiresAt: wednesdayNoon.AddHours(6.5), rule: PlanRules.P2Inflow, trigger: "облигации",
            summary: new TradePlanSummary { Currency = "RUB", ReservedBuy = 50_000m });
        live.DryRun = false;
        live.DecidedAt = evening;
        live.DecidedVia = DecidedVia.Web;
        await repo.InsertAsync(live);

        // Approved dry-run не исполнится никогда — резерва нет.
        var dry = NewPlan(userId, PlanStatuses.Approved, evening, expiresAt: wednesdayNoon.AddHours(6.5), rule: PlanRules.P2Inflow, trigger: "dry",
            summary: new TradePlanSummary { Currency = "RUB", ReservedBuy = 7_000m });
        dry.DecidedAt = evening;
        await repo.InsertAsync(dry);

        (await repo.SumReservedBuysAsync(userId, BrokerIds.TInvest, "RUB", at10)).Should().Be(50_000m);

        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<PolicyRunner>().RunForUserAsync(userId, at10, new PolicyRunResult());

        var p1 = (await PlansOfAsync(userId)).Single(p => p.Rule == PlanRules.P1Sweep);
        var summary = JsonSerializer.Deserialize<TradePlanSummary>(p1.SummaryJson!, WebJson)!;
        summary.CashBefore.Should().Be(10_000m);
        summary.ReservedBuy.Should().BeLessThanOrEqualTo(10_000m);
    }

    [Fact]
    public async Task T67_Withdraw100k_With40kFree_NextP1ParksNothing_UntilMoneyWithdrawnReleased()
    {
        // Свободно 40 000 ₽, вывод 100 000 ₽: 40 000 из денег + продажа фонда A. Вывод в банк ручной — пока план вывода открыт, P1 ничего не паркует, а резерв
        // учитывает всю сумму к выводу. «Деньги выведены» (веб) снимает удержание — следующий P1 снова паркует свободные рубли.
        var (userId, client, _, _, _, _, factory) = await SeedRubAsync(cashRub: 40_000m, aQuantity: 1_000m);
        var now = OpenNowUtc();

        TradePlan withdraw;
        using (var scope = factory.Services.CreateScope())
        {
            var (plan, _) = await scope.ServiceProvider.GetRequiredService<PolicyService>()
                .CreateManualAsync(userId, PlanRules.P5Withdraw, new PlanParams(Amount: 100_000m), now);
            withdraw = plan!;
        }

        JsonSerializer.Deserialize<TradePlanSummary>(withdraw.SummaryJson!, WebJson)!.CashHold.Should().Be(100_000m);
        (await new TradePlanRepository(Conn).SumReservedBuysAsync(userId, BrokerIds.TInvest, "RUB", now)).Should().Be(100_000m);

        var result = new PolicyRunResult();
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<PolicyRunner>().RunForUserAsync(userId, now, result);
        (await PlansOfAsync(userId)).Should().NotContain(p => p.Rule == PlanRules.P1Sweep);
        result.Notes.Should().Contain(n => n.Contains("удержан"));

        var release = await client.PostAsync("/api/plans/cash-hold/release", null);
        release.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadAsync(release)).GetProperty("released").GetInt32().Should().Be(1);
        (await new TradePlanRepository(Conn).SumReservedBuysAsync(userId, BrokerIds.TInvest, "RUB", now)).Should().Be(0m);

        // Удержание снято: P1 решает по своим обычным правилам (здесь ликвидность фондом A и так выше цели — паркует ли, решает уже не удержание).
        var after = new PolicyRunResult();
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<PolicyRunner>().RunForUserAsync(userId, now.AddMinutes(1), after);
        after.Notes.Should().NotContain(n => n.Contains("удержан"));
    }

    [Fact]
    public async Task T67_IncompleteOrStaleData_StopsRules_WithReasonInRunAndPreview()
    {
        // Фонд B корзины «liquidity» без цены (ни в позиции, ни в истории) — оценка неполна: P1 не паркует, причина в заметках прогона.
        var (userId, client, accountId, _, b, _, factory) = await SeedRubAsync(cashRub: 7_340m, aQuantity: 0m);
        var sfx = b[2..];
        var now = OpenNowUtc();
        var fundB = await new FundRepository(Conn).UpsertAsync(new Fund { Figi = $"FIGIB{sfx}", Isin = $"RUB{sfx}", Ticker = b, Name = "Фонд B" });
        await new FundPositionRepository(Conn).UpsertAsync(new FundPosition { AccountId = accountId, FundId = fundB, Quantity = 10m, AvgPurchasePriceRub = 900m });

        using (var scope = factory.Services.CreateScope())
        {
            var input = (await scope.ServiceProvider.GetRequiredService<PolicyInputBuilder>().BuildAsync(userId, now, ignoreHours: false))[CapitalPortfolios.Rub];
            input.DataIncomplete.Should().BeTrue();
            input.IncompleteBaskets.Should().Contain("liquidity");
            input.StaleSince.Should().BeNull("синк свежий");

            var result = new PolicyRunResult();
            await scope.ServiceProvider.GetRequiredService<PolicyRunner>().RunForUserAsync(userId, now, result);
            result.Notes.Should().Contain(n => n.StartsWith("P1: данные неполные"));
        }

        (await PlansOfAsync(userId)).Should().NotContain(p => p.Rule == PlanRules.P1Sweep);

        // Синк брокера старше 26 ч: StaleSince выставлен, предпросмотр распределения суммы объясняет, почему плана нет.
        var syncedAt = DateTime.UtcNow.AddHours(-30);
        await new AccountRepository(Conn).UpdateBalanceAsync(accountId, 7_340m, 7_340m + 16_658m, syncedAt);
        using (var scope = factory.Services.CreateScope())
        {
            var input = (await scope.ServiceProvider.GetRequiredService<PolicyInputBuilder>().BuildAsync(userId, DateTime.UtcNow, ignoreHours: true))[CapitalPortfolios.Rub];
            input.StaleSince.Should().BeCloseTo(syncedAt, TimeSpan.FromSeconds(2));
        }

        var preview = await client.PostAsJsonAsync("/api/plans/preview", new { rule = PlanRules.P2Inflow, @params = new { amount = 5_000m } });
        preview.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ReadAsync(preview);
        body.GetProperty("plan").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("notes").EnumerateArray().Select(n => n.GetString()).Should().Contain(n => n!.Contains("устарели"));
    }

    [Fact]
    public async Task T67A2_Withdraw30k_FullyCoveredBy40kFree_HoldOnlyPlan_NextP1ParksAtMost10k()
    {
        // Вывод 30 000 ₽ при свободных 40 000 ₽ — продажи не нужны, но деньги всё равно удерживаются планом «только удержание» (без строк, cashHold = 30 000).
        // Подтверждение переводит его сразу в Done (заявок нет), удержание продолжает действовать — P1 не паркует удержанные деньги.
        var (userId, _, _, _, _, _, factory) = await SeedRubAsync(cashRub: 40_000m, aQuantity: 0m);
        var now = OpenNowUtc();
        var repo = new TradePlanRepository(Conn);

        TradePlan withdraw;
        using (var scope = factory.Services.CreateScope())
        {
            var (plan, _) = await scope.ServiceProvider.GetRequiredService<PolicyService>()
                .CreateManualAsync(userId, PlanRules.P5Withdraw, new PlanParams(Amount: 30_000m), now);
            plan.Should().NotBeNull("вывод из свободных денег — тоже план (удержание)");
            withdraw = plan!;
        }

        withdraw.Lines.Should().BeEmpty();
        withdraw.Status.Should().Be(PlanStatuses.PendingApproval);
        JsonSerializer.Deserialize<TradePlanSummary>(withdraw.SummaryJson!, WebJson)!.CashHold.Should().Be(30_000m);
        (await repo.SumReservedBuysAsync(userId, BrokerIds.TInvest, "RUB", now)).Should().Be(30_000m);

        using (var scope = factory.Services.CreateScope())
            (await scope.ServiceProvider.GetRequiredService<PolicyService>().ApproveAsync(userId, withdraw.Id, DecidedVia.Web, now)).Kind.Should().Be(PlanDecisionKind.Ok);
        (await repo.GetAsync(userId, withdraw.Id))!.Status.Should().Be(PlanStatuses.Done);
        (await repo.SumReservedBuysAsync(userId, BrokerIds.TInvest, "RUB", now.AddMinutes(1))).Should().Be(30_000m, "удержание живёт после подтверждения");

        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<PolicyRunner>().RunForUserAsync(userId, now.AddMinutes(1), new PolicyRunResult());

        var p1 = (await PlansOfAsync(userId)).Where(p => p.Rule == PlanRules.P1Sweep).ToList();
        foreach (var plan in p1)
            JsonSerializer.Deserialize<TradePlanSummary>(plan.SummaryJson!, WebJson)!.ReservedBuy.Should().BeLessThanOrEqualTo(10_000m);
    }

    [Fact]
    public async Task Runner_RealPlan_SavesReservedBuy_ThatTheRepositoryReserves()
    {
        var (userId, _, _, _, _, _, factory) = await SeedRubAsync(cashRub: 7_340m, aQuantity: 0m);
        var now = OpenNowUtc();
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<PolicyRunner>().RunForUserAsync(userId, now, new PolicyRunResult());

        var plan = (await PlansOfAsync(userId)).Single(p => p.Rule == PlanRules.P1Sweep);
        var summary = JsonSerializer.Deserialize<TradePlanSummary>(plan.SummaryJson!, WebJson)!;
        summary.ReservedBuy.Should().Be(plan.Lines.Sum(l => l.EstAmount + l.EstFee)).And.BeGreaterThan(7_000m);
        (await new TradePlanRepository(Conn).SumReservedBuysAsync(userId, BrokerIds.TInvest, "RUB", now)).Should().Be(summary.ReservedBuy);
    }

    [Fact]
    public async Task ExpirePending_AlsoExpiresAutoApproved_WithNotExecutedNote_AndRejectCancelsAutoApproved()
    {
        var (userId, client, _, _, _, _, _) = await SeedRubAsync(cashRub: 0m);
        var repo = new TradePlanRepository(Conn);
        var now = DateTime.UtcNow;
        var stale = await repo.InsertAsync(NewPlan(userId, PlanStatuses.AutoApproved, now.AddHours(-1), expiresAt: now.AddMinutes(-5)));
        var fresh = await repo.InsertAsync(NewPlan(userId, PlanStatuses.AutoApproved, now));

        (await repo.ExpirePendingAsync(now)).Should().BeGreaterThanOrEqualTo(1);

        var expired = (await repo.GetAsync(userId, stale))!;
        expired.Status.Should().Be(PlanStatuses.Expired);
        expired.SummaryJson.Should().Contain("не исполнен");
        (await repo.GetAsync(userId, fresh))!.Status.Should().Be(PlanStatuses.AutoApproved, "не просроченный авто-план остаётся");

        // Отменить авто-план можно тем же reject; подтвердить (approve) уже авто-подтверждённый нельзя.
        (await client.PostAsync($"/api/plans/{fresh}/approve", null)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        var reject = await client.PostAsync($"/api/plans/{fresh}/reject", null);
        reject.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadAsync(reject)).GetProperty("status").GetString().Should().Be("Rejected");
        (await client.PostAsync($"/api/plans/{stale}/reject", null)).StatusCode.Should().Be(HttpStatusCode.Conflict, "истёкший план уже не отклонить");
    }

    private static (Mock<IBondUniverseRepository> Repo, Func<int> Calls) CountingUniverse()
    {
        var calls = 0;
        var repo = new Mock<IBondUniverseRepository>();
        repo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .Callback(() => Interlocked.Increment(ref calls))
            .ReturnsAsync((IReadOnlyList<BondUniverseEntry>)[]);
        repo.Setup(r => r.GetRecentHistoryAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync((IReadOnlyList<BondUniverseHistoryPoint>)[]);
        return (repo, () => Volatile.Read(ref calls));
    }

    [Fact]
    public async Task Runner_P3b_OpenPlanWithSameTrigger_SkipsBondDataCollection()
    {
        var (userId, _, _, _, _, _, seeded) = await SeedRubAsync(cashRub: 0m, aQuantity: 100m); // ликвидность — весь портфель: HardAbove, вес > 0.20
        var (universe, calls) = CountingUniverse();
        var factory = seeded.WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?> { ["Policy:LiquidityCheckDay"] = "Wednesday" }));
            b.ConfigureServices(s =>
            {
                s.RemoveAll<IBondUniverseRepository>();
                s.AddScoped(_ => universe.Object);
            });
        });
        var now = OpenNowUtc();
        await new TradePlanRepository(Conn).InsertAsync(NewPlan(userId, PlanStatuses.PendingApproval, now, rule: PlanRules.P3bLiqToBonds, trigger: LiquidityToBondsRule.TriggerFor(now)));
        var result = new PolicyRunResult();

        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<PolicyRunner>().RunForUserAsync(userId, now, result);

        calls().Should().Be(0, "открытый P3b с той же причиной: дорогой сбор кандидатов не выполняется (а P4 не строится, пока открыт P3b)");
        result.Notes.Should().Contain(n => n.Contains("данные по облигациям не собирались"));
    }

    [Fact]
    public async Task Runner_P4_DuplicateTrigger_SkipsBondDataCollection_OnSecondRun()
    {
        var (userId, _, _, _, _, _, seeded) = await SeedRubAsync(cashRub: 0m, aQuantity: 100m);
        var (universe, calls) = CountingUniverse();
        var factory = seeded.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.RemoveAll<IBondUniverseRepository>();
            s.AddScoped(_ => universe.Object);
        }));
        var now = OpenNowUtc();

        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<PolicyRunner>().RunForUserAsync(userId, now, new PolicyRunResult());
        var afterFirst = calls();
        afterFirst.Should().BeGreaterThan(0, "контроль: первый прогон P4 собирает облигационные данные");
        (await PlansOfAsync(userId)).Should().Contain(p => p.Rule == PlanRules.P4Rebalance);

        var second = new PolicyRunResult();
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<PolicyRunner>().RunForUserAsync(userId, now, second);

        calls().Should().Be(afterFirst, "второй прогон: план P4 с той же причиной уже есть — кандидатов не собираем");
        second.Notes.Should().Contain(n => n.Contains("P4_REBALANCE") && n.Contains("не собирались"));
    }

    [Fact]
    public async Task T67_PolicyUserLock_SerializesRunnerAndManualPlansPerUser()
    {
        // Задача 67 (#13): прогон правил и ручной план одного пользователя — под одним GET_LOCK на пользователя; другой пользователь не блокируется.
        var userLock = new Bonds.Infrastructure.Policy.MySqlPolicyUserLock(Conn);
        await using (var held = await userLock.TryAcquireAsync(9_000_001, TimeSpan.Zero))
        {
            held.Should().NotBeNull();
            (await userLock.TryAcquireAsync(9_000_001, TimeSpan.Zero)).Should().BeNull("тот же пользователь занят");
            await using var other = await userLock.TryAcquireAsync(9_000_002, TimeSpan.Zero);
            other.Should().NotBeNull("другой пользователь независим");
        }

        await using var again = await userLock.TryAcquireAsync(9_000_001, TimeSpan.Zero);
        again.Should().NotBeNull("освобождено после Dispose");
    }
}

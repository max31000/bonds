using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bonds.Core.Capital;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Bonds.Core.Policy;
using Bonds.Infrastructure.Policy;
using Bonds.Infrastructure.Repositories;
using Bonds.IntegrationTests.Infrastructure;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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

    private (Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Factory, FakeQuotes Quotes) NewFactory(Dictionary<string, string?>? config = null)
    {
        var quotes = new FakeQuotes();
        var factory = _factory.WithWebHostBuilder(b =>
        {
            if (config is not null) b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(config));
            b.ConfigureServices(s =>
            {
                s.RemoveAll<IPlanQuoteSource>();
                s.AddSingleton<IPlanQuoteSource>(quotes);
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
                MinPriceIncrement = step, Currency = "RUB", ApiTradeAvailable = true, BuyAvailable = true, SellAvailable = true, UpdatedAt = DateTime.UtcNow,
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
        line.GetProperty("estFee").GetDecimal().Should().BePositive();
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

        var plans = await PlansOfAsync(userId);
        var p1 = plans.Where(p => p.Rule == PlanRules.P1Sweep).Should().ContainSingle().Subject;
        p1.Status.Should().Be(PlanStatuses.AutoApproved, "корзина auto_buy, суммы в пределах лимитов из конфигурации");
        p1.DecidedVia.Should().Be("auto");
        p1.DryRun.Should().BeTrue();
        p1.Lines.Should().NotBeEmpty().And.OnlyContain(l => l.Side == PlanSides.Buy);
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
}

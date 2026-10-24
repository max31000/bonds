using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bonds.Core.Credentials;
using Bonds.Core.Execution;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Bonds.Core.Policy;
using Bonds.Core.Telegram;
using Bonds.Infrastructure.Connectors.Freedom;
using Bonds.Infrastructure.Connectors.Freedom.Models;
using Bonds.Infrastructure.Execution;
using Bonds.Infrastructure.Repositories;
using Bonds.IntegrationTests.Infrastructure;
using Bonds.Tests.Execution;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Moq;
using MySqlConnector;
using Xunit;
using BrokerIds = Bonds.Core.Models.Brokers;

namespace Bonds.IntegrationTests;

/// <summary>
/// Задача 66 на реальном MySQL: ворота Freedom (все 16 комбинаций env × режим × ключ <c>freedom_trade</c> × пауза — заявку отправляет ровно одна), запись заявки
/// (<c>trade_orders.broker = freedom</c>, числовой <c>user_order_id</c> до отправки), <c>dry_run</c> плана по воротам Freedom, сетевая проверка торгового ключа
/// (тот же счёт, что ключ на чтение), смена/удаление <c>freedom_trade</c> при живых заявках Freedom. Брокер — <see cref="FakeFreedomOrderGateway"/> (сети нет).
/// </summary>
[Collection("Integration")]
public class FreedomExecutionIntegrationTests
{
    private readonly TestWebApplicationFactory _factory;

    public FreedomExecutionIntegrationTests(TestWebApplicationFactory factory) => _factory = factory;

    private string Conn => _factory.Database.ConnectionString;

    private FakeFreedomOrderGateway SharedFreedom => _factory.Services.GetRequiredService<FakeFreedomOrderGateway>();

    private static readonly DateTime Noon = NextWeekdayNoonMskUtc();

    private static DateTime NextWeekdayNoonMskUtc()
    {
        var d = DateTime.UtcNow.Date.AddDays(1);
        while (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) d = d.AddDays(1);
        return DateTime.SpecifyKind(d.AddHours(9), DateTimeKind.Utc);
    }

    private sealed record Seed(ulong UserId, long TelegramId, ulong AccountId, string Number, HttpClient Client);

    private async Task<Seed> SeedAsync(bool readKey = true)
    {
        var telegramId = Random.Shared.NextInt64(1, long.MaxValue);
        var userId = await new UserRepository(Conn).CreateAsync(new User { TelegramId = telegramId });
        var number = Random.Shared.Next(100_000, 999_999).ToString();
        var accountId = await new AccountRepository(Conn).CreateAsync(new Account { UserId = userId, Broker = BrokerIds.Freedom, BrokerAccountId = number, Name = "Freedom" });
        using (var scope = _factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IInstrumentTradingMetaRepository>().UpsertAsync(new InstrumentTradingMeta
            {
                Broker = BrokerIds.Freedom, InstrumentKey = "VUAA.EU", Ticker = "VUAA.EU", Lot = 1m, MinPriceIncrement = 0.01m, Currency = "USD",
                ApiTradeAvailable = true, BuyAvailable = true, SellAvailable = true,
            });
            if (readKey)
            {
                await scope.ServiceProvider.GetRequiredService<IUserCredentialStore>().SaveAsync(
                    userId, CredentialProviders.Freedom, new Dictionary<string, string> { ["publicKey"] = "read-pub-TEST", ["privateKey"] = "read-priv-TEST" }, "TEST",
                    JsonSerializer.Serialize(new { accountNumber = number, tariffId = 1431, tariffName = "Standard" }));
            }
        }

        var client = _factory.CreateClient();
        Mvv42TestAuth.Authorize(client, telegramId);
        return new Seed(userId, telegramId, accountId, number, client);
    }

    private async Task SaveTradeKeyAsync(Seed s, string publicKey = "trade-pub-TEST", string privateKey = "trade-priv-TEST")
    {
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IUserCredentialStore>().SaveAsync(
            s.UserId, CredentialProviders.FreedomTrade, new Dictionary<string, string> { ["publicKey"] = publicKey, ["privateKey"] = privateKey }, "TEST",
            FreedomTradeCredentialMeta.Build(s.Number, 1431));
    }

    private static TradePlanLine Line(Seed s) => new()
    {
        LineNo = 1, Broker = BrokerIds.Freedom, AccountId = s.AccountId, InstrumentKey = "VUAA.EU", Ticker = "VUAA.EU", Side = PlanSides.Buy, QuantityLots = 3, LotSize = 1m,
        LimitPrice = 147.70m, EstAmount = 443.10m, EstFee = 1.73m, Currency = "USD", Reason = "тест",
    };

    private async Task<TradePlan> InsertPlanAsync(Seed s, string status = PlanStatuses.Approved, string via = DecidedVia.Web)
    {
        var plan = new TradePlan
        {
            UserId = s.UserId, Rule = PlanRules.P7FreedomTopUp, Trigger = "fx " + Guid.NewGuid().ToString("N")[..6], Status = status, DryRun = false, CreatedAt = Noon.AddMinutes(-1),
            ExpiresAt = Noon.AddMinutes(29), DecidedAt = Noon, DecidedVia = via, BotNonce = BotCallback.NewNonce(), SummaryJson = """{"portfolio":"usd","currency":"USD"}""",
            Lines = [Line(s)],
        };
        plan.Id = await new TradePlanRepository(Conn).InsertAsync(plan);
        return plan;
    }

    private (PlanExecutionEngine Engine, FakeFreedomOrderGateway Freedom) NewEngine(IServiceScope scope, bool enabled, Seed s)
    {
        var freedom = new FakeFreedomOrderGateway { Account = new FreedomTradeAccountInfo(s.Number, 1431), Now = Noon }; // «сейчас» брокера = момент тика
        freedom.Quotes["VUAA.EU"] = new FreedomMarketQuote(147.65m, 147.75m, 0.01m, "USD");
        var monitor = Mock.Of<IOptionsMonitor<ExecutionOptions>>(m => m.CurrentValue == new ExecutionOptions { Enabled = enabled });
        var sp = scope.ServiceProvider;
        var gate = new ExecutionGateResolver(monitor, sp.GetRequiredService<IUserSettingsRepository>(), sp.GetRequiredService<IUserCredentialStore>());
        var engine = ActivatorUtilities.CreateInstance<PlanExecutionEngine>(
            sp, new FakeOrderGateway(), (IExecutionGateResolver)gate, monitor, new NullPortfolioSyncTrigger(), (IFreedomOrderGateway)freedom);
        return (engine, freedom);
    }

    public static IEnumerable<object[]> AllGateCombinations()
    {
        foreach (var enabled in new[] { false, true })
        foreach (var live in new[] { false, true })
        foreach (var key in new[] { false, true })
        foreach (var paused in new[] { false, true })
            yield return [enabled, live, key, paused];
    }

    [Theory]
    [MemberData(nameof(AllGateCombinations))]
    public async Task Gate_OnlyAllConditions_SubmitAFreedomOrder(bool enabled, bool live, bool key, bool paused)
    {
        var s = await SeedAsync();
        var settings = new UserSettingsRepository(Conn);
        await settings.SetExecutionModeAsync(s.UserId, live ? ExecutionModes.Live : ExecutionModes.Observe);
        await settings.SetExecutionPausedAsync(s.UserId, paused);
        if (key) await SaveTradeKeyAsync(s);
        var plan = await InsertPlanAsync(s);

        using var scope = _factory.Services.CreateScope();
        var (engine, freedom) = NewEngine(scope, enabled, s);
        await engine.RunForUserAsync(s.UserId, Noon);

        var allOpen = enabled && live && key && !paused;
        freedom.PostCount.Should().Be(allOpen ? 1 : 0);
        var stored = await new TradePlanRepository(Conn).GetAsync(s.UserId, plan.Id);
        stored!.Status.Should().Be(allOpen ? PlanStatuses.Submitting : PlanStatuses.Approved);
        if (!allOpen) (await new TradeOrderRepository(Conn).ListByPlanAsync(plan.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task LiveOrder_PersistedAsFreedom_WithNumericUserOrderId()
    {
        var s = await SeedAsync();
        await new UserSettingsRepository(Conn).SetExecutionModeAsync(s.UserId, ExecutionModes.Live);
        await SaveTradeKeyAsync(s);
        var plan = await InsertPlanAsync(s);

        using var scope = _factory.Services.CreateScope();
        var (engine, freedom) = NewEngine(scope, true, s);
        await engine.RunForUserAsync(s.UserId, Noon);

        var order = (await new TradeOrderRepository(Conn).ListByPlanAsync(plan.Id)).Single();
        order.Broker.Should().Be(BrokerIds.Freedom);
        FreedomOrderRules.IsValidUserOrderId(order.ClientOrderId).Should().BeTrue();
        order.ClientOrderId.Should().Be(freedom.Posted.Single().Order.UserOrderId);
        order.BrokerOrderId.Should().NotBeNullOrEmpty();
        order.LimitPrice.Should().Be(147.70m);
        (await new TradeOrderRepository(Conn).ListActiveByUserAsync(s.UserId, BrokerIds.Freedom)).Should().ContainSingle();
        (await new TradeOrderRepository(Conn).ListActiveByUserAsync(s.UserId, BrokerIds.TInvest)).Should().BeEmpty();

        await using var conn = new MySqlConnection(Conn);
        (await conn.ExecuteScalarAsync<string>("SELECT broker FROM trade_orders WHERE id = @Id", new { order.Id })).Should().Be("freedom");
    }

    [Fact]
    public async Task AutoApprovedFreedomPlan_NotExecuted_EvenWithOpenGate()
    {
        var s = await SeedAsync();
        await new UserSettingsRepository(Conn).SetExecutionModeAsync(s.UserId, ExecutionModes.Live);
        await SaveTradeKeyAsync(s);
        var plan = await InsertPlanAsync(s, PlanStatuses.AutoApproved, DecidedVia.Auto);

        using var scope = _factory.Services.CreateScope();
        var (engine, freedom) = NewEngine(scope, true, s);
        await engine.RunForUserAsync(s.UserId, Noon);

        freedom.PostCount.Should().Be(0);
        (await new TradePlanRepository(Conn).GetAsync(s.UserId, plan.Id))!.Status.Should().Be(PlanStatuses.Failed);
    }

    [Fact]
    public async Task PolicyService_FreedomPlanLive_OnlyWithFreedomGate_AndNeverAutoApproved()
    {
        var s = await SeedAsync();
        var draft = new TradePlanDraft(PlanRules.P7FreedomTopUp, "t " + Guid.NewGuid().ToString("N")[..6], PlanStatuses.PendingApproval, Noon.AddMinutes(30),
            [Line(s)], new TradePlanSummary { Portfolio = "usd", Currency = "USD" });

        var enabledFactory = _factory.WithWebHostBuilder(b => b.UseSetting("Execution:Enabled", "true"));
        using var scope = enabledFactory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<Bonds.Infrastructure.Policy.PolicyService>();
        await new UserSettingsRepository(Conn).SetExecutionModeAsync(s.UserId, ExecutionModes.Live);
        (await service.SaveAsync(s.UserId, draft, Noon, forcePending: false)).DryRun.Should().BeTrue("нет ключа freedom_trade");

        await SaveTradeKeyAsync(s);
        (await service.SaveAsync(s.UserId, draft, Noon, forcePending: false)).DryRun.Should().BeFalse("ворота Freedom открыты");
        (await service.SaveAsync(s.UserId, draft with { Status = PlanStatuses.AutoApproved }, Noon, forcePending: false)).DryRun
            .Should().BeTrue("AutoApproved со строками Freedom не исполняется никогда");
    }

    // ─── Ключ freedom_trade: сетевая проверка и смена при живых заявках ────────────────────

    private HttpClient ClientWithFreedomApi(Seed s, string accountNumber)
    {
        var api = new Mock<IFreedomApiClient>();
        api.Setup(a => a.GetUserInfoAsync(It.IsAny<FreedomCredentials>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FreedomUserInfo(accountNumber, 1431, "Standard"));
        var factory = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            services.RemoveAll<IFreedomApiClient>();
            services.AddScoped(_ => api.Object);
        }));
        var client = factory.CreateClient();
        Mvv42TestAuth.Authorize(client, s.TelegramId);
        return client;
    }

    private static Task<HttpResponseMessage> PutTradeKey(HttpClient client, string publicKey = "trade-pub-NEW", string query = "") =>
        client.PutAsJsonAsync("/api/credentials/freedom_trade" + query,
            new { fields = new Dictionary<string, string> { ["publicKey"] = publicKey, ["privateKey"] = "trade-priv-NEW" } });

    [Fact]
    public async Task PutTradeKey_SameAccountAsReadKey_Ok_WithHonestTradeRightNote()
    {
        var s = await SeedAsync();
        var response = await PutTradeKey(ClientWithFreedomApi(s, s.Number));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("status").GetString().Should().Be(CredentialStatus.Ok);
        body.GetProperty("meta").GetProperty("tradeRight").GetString().Should().Be("unverified");
        body.GetProperty("meta").GetProperty("note").GetString().Should().Contain("первой заявке");
        body.GetRawText().Should().NotContain("trade-priv-NEW");
    }

    [Fact]
    public async Task PutTradeKey_OtherAccount_Or_NoReadKey_422()
    {
        var s = await SeedAsync();
        (await PutTradeKey(ClientWithFreedomApi(s, "999999"))).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        var noRead = await SeedAsync(readKey: false);
        (await PutTradeKey(ClientWithFreedomApi(noRead, noRead.Number))).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    private async Task<(TradePlan Plan, TradeOrder Order)> LiveFreedomOrderAsync(Seed s)
    {
        var plan = await InsertPlanAsync(s, PlanStatuses.Submitting);
        var brokerId = Random.Shared.NextInt64(800_000_000, 899_999_999).ToString();
        var order = new TradeOrder
        {
            PlanId = plan.Id, LineNo = 1, Attempt = 1, ClientOrderId = FreedomOrderRules.NewUserOrderId(), BrokerOrderId = brokerId, Broker = BrokerIds.Freedom,
            Status = TradeOrderStatuses.Submitted, LimitPrice = 147.70m, PriceUnit = OrderPriceUnits.Currency, LotsRequested = 3, Currency = "USD",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        order.Id = await new TradeOrderRepository(Conn).InsertAsync(order);
        SharedFreedom.Orders[brokerId] = new GatewayOrderState(brokerId, order.ClientOrderId, TradeOrderStatuses.Submitted, 3, 0, null, null, "USD", null);
        return (plan, order);
    }

    [Fact]
    public async Task ExecutionSettings_And_Plans_ReportGatesPerBroker()
    {
        var s = await SeedAsync();
        await SaveTradeKeyAsync(s);

        var state = await s.Client.GetFromJsonAsync<JsonElement>("/api/settings/execution");
        var brokers = state.GetProperty("brokers");
        brokers.GetProperty("freedom").GetProperty("tradeCredentialOk").GetBoolean().Should().BeTrue("проверенный freedom_trade");
        brokers.GetProperty("tinvest").GetProperty("tradeCredentialOk").GetBoolean().Should().BeFalse("tinvest_trade нет");
        brokers.GetProperty("freedom").GetProperty("live").GetBoolean().Should().BeFalse();
        brokers.GetProperty("tinvest").GetProperty("live").GetBoolean().Should().BeFalse();
        state.GetProperty("tradeCredentialOk").GetBoolean().Should().BeTrue("включить режим можно и с одним ключом Freedom");
        var reasons = state.GetProperty("reasons").EnumerateArray().Select(r => r.GetString()!).ToList();
        reasons.Should().Contain(r => r.StartsWith("T-Invest: ")).And.Contain(r => r.StartsWith("Freedom: "));

        var plans = await s.Client.GetFromJsonAsync<JsonElement>("/api/plans");
        plans.GetProperty("dryRun").GetBoolean().Should().BeTrue();
        plans.GetProperty("brokers").GetProperty("tinvest").GetBoolean().Should().BeFalse();
        plans.GetProperty("brokers").GetProperty("freedom").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task IsBrokerOrderLinked_ByBrokerAndId_ExceptSelf()
    {
        var s = await SeedAsync();
        var (_, order) = await LiveFreedomOrderAsync(s);
        var repo = new TradeOrderRepository(Conn);

        (await repo.IsBrokerOrderLinkedAsync(BrokerIds.Freedom, order.BrokerOrderId!, 0)).Should().BeTrue();
        (await repo.IsBrokerOrderLinkedAsync(BrokerIds.Freedom, order.BrokerOrderId!, order.Id)).Should().BeFalse("своя строка не считается");
        (await repo.IsBrokerOrderLinkedAsync(BrokerIds.TInvest, order.BrokerOrderId!, 0)).Should().BeFalse("другой брокер");
        (await repo.IsBrokerOrderLinkedAsync(BrokerIds.Freedom, "1", 0)).Should().BeFalse();
    }

    [Fact]
    public async Task DeleteTradeKey_WithActiveFreedomOrder_409_ThenCancelActive_CancelsThenDeletes()
    {
        var s = await SeedAsync();
        await SaveTradeKeyAsync(s);
        var (plan, order) = await LiveFreedomOrderAsync(s);

        var blocked = await s.Client.DeleteAsync("/api/credentials/freedom_trade");
        blocked.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await blocked.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("type").GetString().Should().Be("ActiveOrders");
        SharedFreedom.Cancelled.Should().NotContain(order.BrokerOrderId);

        var ok = await s.Client.DeleteAsync("/api/credentials/freedom_trade?cancelActive=true");
        ok.StatusCode.Should().Be(HttpStatusCode.NoContent);
        SharedFreedom.Cancelled.Should().Contain(order.BrokerOrderId!, "снята ТЕКУЩИМ ключом до удаления");
        var stored = (await new TradeOrderRepository(Conn).ListByPlanAsync(plan.Id)).Single();
        stored.Should().Match<TradeOrder>(o => o.Status == TradeOrderStatuses.Cancelled && o.ErrorCode == TradeOrderErrorCodes.TokenChange);
    }

    [Fact]
    public async Task FreedomOrders_DoNotBlockTInvestTokenChange_AndViceVersa()
    {
        var s = await SeedAsync();
        await SaveTradeKeyAsync(s);
        await LiveFreedomOrderAsync(s);
        using (var scope = _factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IUserCredentialStore>().SaveAsync(
                s.UserId, CredentialProviders.TInvestTrade, new Dictionary<string, string> { ["token"] = "t.trade-x" }, "x", TradeCredentialMeta.Build("ACC-1"));
        }

        (await s.Client.DeleteAsync("/api/credentials/tinvest_trade")).StatusCode.Should().Be(HttpStatusCode.NoContent, "активные заявки — только Freedom");
        (await s.Client.DeleteAsync("/api/credentials/freedom_trade")).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task ReplaceTradeKey_WithActiveFreedomOrder_409_SameKeyAllowed()
    {
        var s = await SeedAsync();
        await SaveTradeKeyAsync(s, "trade-pub-NEW", "trade-priv-NEW"); // текущий ключ = тот, что будет «пересохранён»
        await LiveFreedomOrderAsync(s);
        var client = ClientWithFreedomApi(s, s.Number);

        (await PutTradeKey(client, "trade-pub-NEW")).StatusCode.Should().Be(HttpStatusCode.OK, "тот же ключ — не смена");
        (await PutTradeKey(client, "trade-pub-OTHER")).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task LiveMode_CanBeEnabledWithFreedomTradeKeyOnly()
    {
        var s = await SeedAsync();
        var denied = await s.Client.PutAsJsonAsync("/api/settings/execution-mode", new { mode = "live", confirmation = "LIVE" });
        denied.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, "нет ни одного торгового ключа");

        await SaveTradeKeyAsync(s);
        var ok = await s.Client.PutAsJsonAsync("/api/settings/execution-mode", new { mode = "live", confirmation = "LIVE" });
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        (await new UserSettingsRepository(Conn).GetExecutionModeAsync(s.UserId)).Should().Be(ExecutionModes.Live);
    }
}

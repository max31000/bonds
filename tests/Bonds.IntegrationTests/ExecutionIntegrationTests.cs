using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bonds.Core.Credentials;
using Bonds.Core.Execution;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Bonds.Core.Policy;
using Bonds.Core.Telegram;
using Bonds.Infrastructure.Execution;
using Bonds.Infrastructure.Repositories;
using Bonds.Infrastructure.Telegram;
using Bonds.IntegrationTests.Infrastructure;
using Bonds.Tests.Execution;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using MySqlConnector;
using Xunit;
using BrokerIds = Bonds.Core.Models.Brokers;

namespace Bonds.IntegrationTests;

/// <summary>
/// Задача 65 на реальном MySQL: ворота исполнения (все 16 комбинаций env × режим × токен × пауза — заявку отправляет ровно одна), переходы статусов плана и
/// заявок, идемпотентность <c>client_order_id</c> (UNIQUE), пауза останавливает исполнение, эндпоинты режима (<c>LIVE</c>, проверенный токен, сообщение в бот),
/// сетевая проверка торгового токена, заявки строк в <c>GET /api/plans</c>, кнопка «Отменить». Брокер — <see cref="FakeOrderGateway"/> (сети нет).
/// </summary>
[Collection("Integration")]
public class ExecutionIntegrationTests
{
    private const string BrokerAccount = "ACC-1";

    private readonly TestWebApplicationFactory _factory;

    public ExecutionIntegrationTests(TestWebApplicationFactory factory) => _factory = factory;

    private string Conn => _factory.Database.ConnectionString;

    private static readonly DateTime Noon = NextWeekdayNoonMskUtc();

    private static DateTime NextWeekdayNoonMskUtc()
    {
        var d = DateTime.UtcNow.Date.AddDays(1);
        while (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) d = d.AddDays(1);
        return DateTime.SpecifyKind(d.AddHours(9), DateTimeKind.Utc);
    }

    private sealed record Seed(ulong UserId, long TelegramId, ulong AccountId, string Uid, string Key, HttpClient Client);

    private async Task<Seed> SeedAsync()
    {
        var telegramId = Random.Shared.NextInt64(1, long.MaxValue);
        var userId = await new UserRepository(Conn).CreateAsync(new User { TelegramId = telegramId });
        var accountId = await new AccountRepository(Conn).CreateAsync(new Account { UserId = userId, Broker = BrokerIds.TInvest, BrokerAccountId = BrokerAccount, Name = "T" });
        var suffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var key = "EXE" + suffix;
        var uid = "uid-" + suffix;
        using (var scope = _factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IInstrumentTradingMetaRepository>().UpsertAsync(new InstrumentTradingMeta
            {
                Broker = BrokerIds.TInvest, InstrumentKey = key, Uid = uid, Ticker = key, ClassCode = "TQTF", Lot = 1m, MinPriceIncrement = 0.01m, Currency = "RUB",
                ApiTradeAvailable = true, BuyAvailable = true, SellAvailable = true,
            });
        }

        var client = _factory.CreateClient();
        Mvv42TestAuth.Authorize(client, telegramId);
        return new Seed(userId, telegramId, accountId, uid, key, client);
    }

    private async Task SaveTradeTokenAsync(ulong userId)
    {
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IUserCredentialStore>().SaveAsync(
            userId, CredentialProviders.TInvestTrade, new Dictionary<string, string> { ["token"] = "t.trade-test" }, "test", TradeCredentialMeta.Build(BrokerAccount));
    }

    private async Task<TradePlan> InsertPlanAsync(Seed s, string status = PlanStatuses.Approved, bool dryRun = false, long lots = 3)
    {
        var plan = new TradePlan
        {
            UserId = s.UserId, Rule = PlanRules.Manual, Trigger = "exec " + Guid.NewGuid().ToString("N")[..6], Status = status, DryRun = dryRun, CreatedAt = Noon.AddMinutes(-1),
            ExpiresAt = Noon.AddMinutes(29), DecidedAt = Noon, DecidedVia = DecidedVia.Web, BotNonce = BotCallback.NewNonce(),
            SummaryJson = """{"portfolio":"rub","currency":"RUB"}""",
            Lines =
            [
                new TradePlanLine
                {
                    LineNo = 1, Broker = BrokerIds.TInvest, AccountId = s.AccountId, InstrumentKey = s.Key, Ticker = s.Key, Side = PlanSides.Buy, QuantityLots = lots,
                    LotSize = 1m, LimitPrice = 166.57m, EstAmount = 166.57m * lots, EstFee = 0m, Currency = "RUB", Reason = "тест",
                },
            ],
        };
        plan.Id = await new TradePlanRepository(Conn).InsertAsync(plan);
        return plan;
    }

    private (PlanExecutionEngine Engine, FakeOrderGateway Gateway) NewEngine(IServiceScope scope, bool enabled, Seed s)
    {
        var gateway = new FakeOrderGateway();
        gateway.Books[s.Uid] = new GatewayOrderBook(166.50m, 166.60m, Noon.AddSeconds(-2));
        var monitor = Mock.Of<IOptionsMonitor<ExecutionOptions>>(m => m.CurrentValue == new ExecutionOptions { Enabled = enabled });
        var sp = scope.ServiceProvider;
        var gate = new ExecutionGateResolver(monitor, sp.GetRequiredService<IUserSettingsRepository>(), sp.GetRequiredService<IUserCredentialStore>());
        var engine = ActivatorUtilities.CreateInstance<PlanExecutionEngine>(sp, gateway, (IExecutionGateResolver)gate, monitor, new NullPortfolioSyncTrigger());
        return (engine, gateway);
    }

    private async Task<TradePlan> ReloadAsync(TradePlan plan) => (await new TradePlanRepository(Conn).GetAsync(plan.UserId, plan.Id))!;

    // ─── Ворота: 16 комбинаций ─────────────────────────────────────────────────────────────

    public static IEnumerable<object[]> AllGateCombinations()
    {
        foreach (var enabled in new[] { false, true })
        foreach (var live in new[] { false, true })
        foreach (var token in new[] { false, true })
        foreach (var paused in new[] { false, true })
            yield return [enabled, live, token, paused];
    }

    [Theory]
    [MemberData(nameof(AllGateCombinations))]
    public async Task Gate_OnlyAllConditions_SubmitAnOrder(bool enabled, bool live, bool token, bool paused)
    {
        var s = await SeedAsync();
        var settings = new UserSettingsRepository(Conn);
        await settings.SetExecutionModeAsync(s.UserId, live ? ExecutionModes.Live : ExecutionModes.Observe);
        await settings.SetExecutionPausedAsync(s.UserId, paused);
        if (token) await SaveTradeTokenAsync(s.UserId);
        var plan = await InsertPlanAsync(s);

        using var scope = _factory.Services.CreateScope();
        var (engine, gateway) = NewEngine(scope, enabled, s);
        await engine.RunForUserAsync(s.UserId, Noon);

        var expected = enabled && live && token && !paused;
        gateway.PostCount.Should().Be(expected ? 1 : 0);
        (await ReloadAsync(plan)).Status.Should().Be(expected ? PlanStatuses.Submitting : PlanStatuses.Failed);
        var orders = await new TradeOrderRepository(Conn).ListByPlanAsync(plan.Id);
        if (expected) orders.Should().ContainSingle(o => o.Status == TradeOrderStatuses.Submitted && o.ClientOrderId == gateway.Posted[0].Order.ClientOrderId);
        else orders.Should().BeEmpty("при закрытых воротах заявки даже не создаются");
    }

    [Fact]
    public async Task TradeTokenSavedByFormalValidatorBeforeTask65_DoesNotOpenGate()
    {
        var s = await SeedAsync();
        await new UserSettingsRepository(Conn).SetExecutionModeAsync(s.UserId, ExecutionModes.Live);
        using (var scope = _factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IUserCredentialStore>().SaveAsync(
                s.UserId, CredentialProviders.TInvestTrade, new Dictionary<string, string> { ["token"] = "t.old" }, "x.old", metaJson: null);
        await InsertPlanAsync(s);

        using var scope2 = _factory.Services.CreateScope();
        var (engine, gateway) = NewEngine(scope2, enabled: true, s);
        await engine.RunForUserAsync(s.UserId, Noon);

        gateway.PostCount.Should().Be(0, "status ok без отметки сетевой проверки — не торговый токен");
    }

    // ─── Переходы статусов и идемпотентность ───────────────────────────────────────────────

    [Fact]
    public async Task StatusTransitions_ApprovedSubmittingDone_OnMySql()
    {
        var s = await SeedAsync();
        await new UserSettingsRepository(Conn).SetExecutionModeAsync(s.UserId, ExecutionModes.Live);
        await SaveTradeTokenAsync(s.UserId);
        var plan = await InsertPlanAsync(s);

        using var scope = _factory.Services.CreateScope();
        var (engine, gateway) = NewEngine(scope, enabled: true, s);
        await engine.RunForUserAsync(s.UserId, Noon);
        (await ReloadAsync(plan)).Status.Should().Be(PlanStatuses.Submitting);

        gateway.Fill(gateway.Posted[0].Order.ClientOrderId, 3, 166.56m, 0m);
        await engine.RunForUserAsync(s.UserId, Noon.AddSeconds(20));

        var done = await ReloadAsync(plan);
        done.Status.Should().Be(PlanStatuses.Done);
        done.DecidedVia.Should().Be(DecidedVia.Web, "переходы исполнения не трогают, кто решил");
        var order = (await new TradeOrderRepository(Conn).ListByPlanAsync(plan.Id)).Single();
        order.Should().Match<TradeOrder>(o => o.Status == TradeOrderStatuses.Filled && o.LotsFilled == 3 && o.AvgFillPrice == 166.56m && o.BrokerOrderId != null);

        // Дальнейшие тики ничего не отправляют: план вне исполнимых.
        await engine.RunForUserAsync(s.UserId, Noon.AddSeconds(40));
        gateway.PostCount.Should().Be(1);

        // Суточный лимит авто учитывает исполненное (Done) — и только RUB-покупки.
        (await new TradePlanRepository(Conn).SumAutoApprovedRubSinceAsync(s.UserId, Noon.AddHours(-1))).Should().Be(166.57m * 3);
        (await new TradePlanRepository(Conn).SumAutoApprovedRubSinceAsync(s.UserId, Noon.AddHours(-1), plan.Id)).Should().Be(0m);
    }

    [Fact]
    public async Task ClientOrderId_IsUnique_InDatabase()
    {
        var s = await SeedAsync();
        var plan = await InsertPlanAsync(s, PlanStatuses.Submitting);
        var repo = new TradeOrderRepository(Conn);
        var id = Guid.NewGuid().ToString("D");
        TradeOrder Row(int attempt) => new()
        {
            PlanId = plan.Id, LineNo = 1, Attempt = attempt, ClientOrderId = id, Status = TradeOrderStatuses.New, LimitPrice = 1m, PriceUnit = OrderPriceUnits.Currency,
            LotsRequested = 1, Currency = "RUB", CreatedAt = Noon, UpdatedAt = Noon,
        };
        await repo.InsertAsync(Row(1));

        await FluentActions.Invoking(() => repo.InsertAsync(Row(2))).Should().ThrowAsync<MySqlException>();
        var sameAttempt = Row(1);
        sameAttempt.ClientOrderId = Guid.NewGuid().ToString("D");
        await FluentActions.Invoking(() => repo.InsertAsync(sameAttempt)).Should().ThrowAsync<MySqlException>("одна попытка строки — одна заявка");
    }

    [Fact]
    public async Task Pause_StopsExecution_CancelsActiveOrder()
    {
        var s = await SeedAsync();
        await new UserSettingsRepository(Conn).SetExecutionModeAsync(s.UserId, ExecutionModes.Live);
        await SaveTradeTokenAsync(s.UserId);
        var plan = await InsertPlanAsync(s);

        using var scope = _factory.Services.CreateScope();
        var (engine, gateway) = NewEngine(scope, enabled: true, s);
        await engine.RunForUserAsync(s.UserId, Noon);
        gateway.PostCount.Should().Be(1);

        var resp = await s.Client.PutAsJsonAsync("/api/settings/execution-pause", new { paused = true });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("live").GetBoolean().Should().BeFalse();

        await engine.RunForUserAsync(s.UserId, Noon.AddSeconds(20));

        gateway.Cancelled.Should().ContainSingle();
        gateway.PostCount.Should().Be(1);
        (await ReloadAsync(plan)).Status.Should().Be(PlanStatuses.Failed);
        (await new TradeOrderRepository(Conn).ListByPlanAsync(plan.Id)).Single().Status.Should().Be(TradeOrderStatuses.Cancelled);
    }

    // ─── Режим исполнения (API) ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecutionMode_Live_RequiresVerifiedTokenAndLiveWord_NotifiesBot()
    {
        var s = await SeedAsync();

        (await s.Client.PutAsJsonAsync("/api/settings/execution-mode", new { mode = "live", confirmation = "LIVE" })).StatusCode
            .Should().Be(HttpStatusCode.UnprocessableEntity, "нет торгового токена");

        await SaveTradeTokenAsync(s.UserId);
        (await s.Client.PutAsJsonAsync("/api/settings/execution-mode", new { mode = "live", confirmation = "live" })).StatusCode
            .Should().Be(HttpStatusCode.UnprocessableEntity, "слово LIVE — с учётом регистра");
        (await s.Client.PutAsJsonAsync("/api/settings/execution-mode", new { mode = "bogus" })).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        var ok = await s.Client.PutAsJsonAsync("/api/settings/execution-mode", new { mode = "live", confirmation = "LIVE" });
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ok.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("mode").GetString().Should().Be("live");
        body.GetProperty("tradeCredentialOk").GetBoolean().Should().BeTrue();
        body.GetProperty("globalEnabled").GetBoolean().Should().BeFalse("в тестовой конфигурации Execution:Enabled не задан — false");
        body.GetProperty("live").GetBoolean().Should().BeFalse();
        (await new UserSettingsRepository(Conn).GetExecutionModeAsync(s.UserId)).Should().Be(ExecutionModes.Live);

        var bot = _factory.Services.GetRequiredService<FakeTelegramBotClient>();
        bot.SentTo(s.TelegramId).Should().Contain(m => m.Html.Contains("Исполнение включено"));

        var get = await s.Client.GetFromJsonAsync<JsonElement>("/api/settings/execution");
        get.GetProperty("reasons").EnumerateArray().Select(r => r.GetString()).Should().Contain(r => r!.Contains("Execution__Enabled"));

        (await s.Client.PutAsJsonAsync("/api/settings/execution-mode", new { mode = "observe" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await new UserSettingsRepository(Conn).GetExecutionModeAsync(s.UserId)).Should().Be(ExecutionModes.Observe);
    }

    [Fact]
    public async Task DefaultMode_IsObserve_PlansListIsDryRun()
    {
        var s = await SeedAsync();
        (await new UserSettingsRepository(Conn).GetExecutionModeAsync(s.UserId)).Should().Be(ExecutionModes.Observe);
        var list = await s.Client.GetFromJsonAsync<JsonElement>("/api/plans");
        list.GetProperty("dryRun").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task PolicyService_SavesLivePlansWithDryRunFalse_OnlyWhenGateOpen()
    {
        var s = await SeedAsync();
        var draft = new TradePlanDraft(PlanRules.Manual, "t " + Guid.NewGuid().ToString("N")[..6], PlanStatuses.PendingApproval, Noon.AddMinutes(30),
            [new TradePlanLine { LineNo = 1, Broker = BrokerIds.TInvest, AccountId = s.AccountId, InstrumentKey = s.Key, Ticker = s.Key, Side = PlanSides.Buy, QuantityLots = 1, LimitPrice = 1m, EstAmount = 1m, Currency = "RUB" }],
            new TradePlanSummary { Portfolio = "rub", Currency = "RUB" });

        var enabledFactory = _factory.WithWebHostBuilder(b => b.UseSetting("Execution:Enabled", "true"));
        using (var scope = enabledFactory.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<Bonds.Infrastructure.Policy.PolicyService>();
            (await service.SaveAsync(s.UserId, draft, Noon, forcePending: false)).DryRun.Should().BeTrue("режим observe");

            await new UserSettingsRepository(Conn).SetExecutionModeAsync(s.UserId, ExecutionModes.Live);
            await SaveTradeTokenAsync(s.UserId);
            (await service.SaveAsync(s.UserId, draft, Noon, forcePending: false)).DryRun.Should().BeFalse("все ворота открыты");

            var freedomLine = draft.Lines[0];
            var mixed = draft with { Lines = [freedomLine, new TradePlanLine { LineNo = 2, Broker = BrokerIds.Freedom, Ticker = "SPYM.US", InstrumentKey = "SPYM.US", Currency = "USD" }] };
            (await service.SaveAsync(s.UserId, mixed, Noon, forcePending: false)).DryRun.Should().BeTrue("Freedom не исполняется");
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<Bonds.Infrastructure.Policy.PolicyService>();
            (await service.SaveAsync(s.UserId, draft, Noon, forcePending: false)).DryRun.Should().BeTrue("Execution:Enabled=false");
        }
    }

    // ─── Торговый токен: сетевая проверка ──────────────────────────────────────────────────

    [Fact]
    public async Task PutTradeToken_NetworkValidated_AgainstPrimaryAccount()
    {
        var s = await SeedAsync();
        var ok = await s.Client.PutAsJsonAsync("/api/credentials/tinvest_trade", new { fields = new Dictionary<string, string> { ["token"] = "t.trade-xyz" } });
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ok.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("status").GetString().Should().Be("ok");
        body.GetProperty("meta").GetProperty("verification").GetString().Should().Be("network");
        body.GetProperty("meta").GetProperty("accountIdLast4").GetString().Should().Be("CC-1");
        (await ok.Content.ReadAsStringAsync()).Should().NotContain("t.trade-xyz");

        // Другой основной счёт — токен не принимается.
        await using (var conn = new MySqlConnection(Conn))
            await conn.ExecuteAsync("UPDATE accounts SET broker_account_id = 'ACC-OTHER' WHERE id = @Id", new { Id = s.AccountId });
        var bad = await s.Client.PutAsJsonAsync("/api/credentials/tinvest_trade", new { fields = new Dictionary<string, string> { ["token"] = "t.trade-xyz" } });
        bad.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await bad.Content.ReadAsStringAsync()).Should().Contain("другой счёт");
    }

    // ─── Заявки в API и отмена ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task PlansApi_ShowsLineOrders_AndCancelExecutionSetsFlag()
    {
        var s = await SeedAsync();
        var plan = await InsertPlanAsync(s, PlanStatuses.Submitting);
        await new TradeOrderRepository(Conn).InsertAsync(new TradeOrder
        {
            PlanId = plan.Id, LineNo = 1, Attempt = 1, ClientOrderId = Guid.NewGuid().ToString("D"), BrokerOrderId = "BRK-SECRET-ID", Status = TradeOrderStatuses.PartiallyFilled,
            LimitPrice = 166.57m, PriceUnit = OrderPriceUnits.Currency, LotsRequested = 3, LotsFilled = 1, AvgFillPrice = 166.57m, ExecutedCommission = 0m, Currency = "RUB",
            CreatedAt = Noon, UpdatedAt = Noon,
        });

        var raw = await s.Client.GetStringAsync($"/api/plans/{plan.Id}");
        raw.Should().NotContain("BRK-SECRET-ID");
        var dto = JsonDocument.Parse(raw).RootElement;
        var line = dto.GetProperty("lines")[0];
        line.GetProperty("executionStatus").GetString().Should().Be(TradeOrderStatuses.PartiallyFilled);
        line.GetProperty("filledLots").GetInt64().Should().Be(1);
        line.GetProperty("orders")[0].GetProperty("priceUnit").GetString().Should().Be("currency");

        (await s.Client.PostAsync($"/api/plans/{plan.Id}/cancel-execution", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReloadAsync(plan)).CancelRequested.Should().BeTrue();

        var dryPlan = await InsertPlanAsync(s, PlanStatuses.Approved, dryRun: true);
        (await s.Client.PostAsync($"/api/plans/{dryPlan.Id}/cancel-execution", null)).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task BotCancelButton_SetsCancelFlag_OnlyForExecutingPlan()
    {
        var s = await SeedAsync();
        var plan = await InsertPlanAsync(s, PlanStatuses.Submitting);
        using (var scope = _factory.Services.CreateScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<ITelegramUpdateHandler>();
            var data = BotCallback.Build(plan.Id, BotPlanAction.CancelExecution, plan.BotNonce!);
            await handler.HandleAsync(new BotUpdate(Random.Shared.NextInt64(1, 1_000_000), null,
                new BotIncomingCallback("cb-" + Guid.NewGuid().ToString("N"), s.TelegramId, s.TelegramId, "private", 900, data)));
        }

        (await ReloadAsync(plan)).CancelRequested.Should().BeTrue();

        var other = await SeedAsync();
        var foreign = BotCallback.Build(plan.Id, BotPlanAction.CancelExecution, plan.BotNonce!);
        var cbId = "cb-" + Guid.NewGuid().ToString("N");
        using (var scope = _factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ITelegramUpdateHandler>().HandleAsync(new BotUpdate(Random.Shared.NextInt64(1, 1_000_000), null,
                new BotIncomingCallback(cbId, other.TelegramId, other.TelegramId, "private", 901, foreign)));
        }

        _factory.Services.GetRequiredService<FakeTelegramBotClient>().Answers.Should().Contain(a => a.CallbackId == cbId && a.Text!.Contains("не найден"));
    }
}

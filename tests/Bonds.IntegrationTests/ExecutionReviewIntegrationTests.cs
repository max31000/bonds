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
using Bonds.IntegrationTests.Infrastructure;
using Bonds.Tests.Execution;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;
using BrokerIds = Bonds.Core.Models.Brokers;

namespace Bonds.IntegrationTests;

/// <summary>
/// Доработки задачи 65 по ревью на реальном MySQL: удаление/замена торгового токена при живых заявках (409 / <c>cancelActive=true</c>), счётчик активных заявок и
/// <c>needsAttention</c> в API, взаимоисключение исполнителя (<c>GET_LOCK</c>), продление срока живого плана при подтверждении. Брокер — общий
/// <see cref="FakeOrderGateway"/> фабрики (сети нет).
/// </summary>
[Collection("Integration")]
public class ExecutionReviewIntegrationTests
{
    private const string BrokerAccount = "ACC-1";

    private readonly TestWebApplicationFactory _factory;

    public ExecutionReviewIntegrationTests(TestWebApplicationFactory factory) => _factory = factory;

    private string Conn => _factory.Database.ConnectionString;

    private FakeOrderGateway SharedGateway => _factory.Services.GetRequiredService<FakeOrderGateway>();

    private sealed record Seed(ulong UserId, ulong AccountId, HttpClient Client);

    private async Task<Seed> SeedAsync()
    {
        var telegramId = Random.Shared.NextInt64(1, long.MaxValue);
        var userId = await new UserRepository(Conn).CreateAsync(new User { TelegramId = telegramId });
        var accountId = await new AccountRepository(Conn).CreateAsync(new Account { UserId = userId, Broker = BrokerIds.TInvest, BrokerAccountId = BrokerAccount, Name = "T" });
        var client = _factory.CreateClient();
        Mvv42TestAuth.Authorize(client, telegramId);
        return new Seed(userId, accountId, client);
    }

    private async Task SaveTradeTokenAsync(ulong userId)
    {
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IUserCredentialStore>().SaveAsync(
            userId, CredentialProviders.TInvestTrade, new Dictionary<string, string> { ["token"] = "t.trade-current" }, "curr", TradeCredentialMeta.Build(BrokerAccount));
    }

    private async Task<TradePlan> InsertPlanAsync(Seed s, string status, bool dryRun = false, DateTime? expiresAt = null)
    {
        var now = DateTime.UtcNow;
        var plan = new TradePlan
        {
            UserId = s.UserId, Rule = PlanRules.Manual, Trigger = "review " + Guid.NewGuid().ToString("N")[..6], Status = status, DryRun = dryRun, CreatedAt = now.AddMinutes(-1),
            ExpiresAt = expiresAt ?? now.AddMinutes(29), DecidedAt = status == PlanStatuses.PendingApproval ? null : now, DecidedVia = status == PlanStatuses.PendingApproval ? null : DecidedVia.Web,
            BotNonce = BotCallback.NewNonce(), SummaryJson = """{"portfolio":"rub","currency":"RUB"}""",
            Lines =
            [
                new TradePlanLine
                {
                    LineNo = 1, Broker = BrokerIds.TInvest, AccountId = s.AccountId, InstrumentKey = "TMON", Ticker = "TMON@", Side = PlanSides.Buy, QuantityLots = 3,
                    LotSize = 1m, LimitPrice = 166.57m, EstAmount = 166.57m * 3, EstFee = 0m, Currency = "RUB", Reason = "тест",
                },
            ],
        };
        plan.Id = await new TradePlanRepository(Conn).InsertAsync(plan);
        return plan;
    }

    /// <summary>Исполняемый план с живой заявкой: строка Submitted в БД и у «брокера».</summary>
    private async Task<(TradePlan Plan, TradeOrder Order)> LiveOrderAsync(Seed s, DateTime? haltAlertedAt = null)
    {
        var plan = await InsertPlanAsync(s, PlanStatuses.Submitting);
        var order = new TradeOrder
        {
            PlanId = plan.Id, LineNo = 1, Attempt = 1, ClientOrderId = Guid.NewGuid().ToString("D"), BrokerOrderId = "BRK-1", Status = TradeOrderStatuses.Submitted,
            LimitPrice = 166.57m, PriceUnit = OrderPriceUnits.Currency, LotsRequested = 3, Currency = "RUB", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            HaltAlertedAt = haltAlertedAt,
        };
        var repo = new TradeOrderRepository(Conn);
        order.Id = await repo.InsertAsync(order);
        if (haltAlertedAt is not null) await repo.UpdateAsync(order);
        SharedGateway.States[order.ClientOrderId] = new GatewayOrderState("BRK-1", order.ClientOrderId, TradeOrderStatuses.Submitted, 3, 0, null, null, "RUB", null);
        return (plan, order);
    }

    private async Task<string?> TradeTokenStatusAsync(Seed s)
    {
        var list = await s.Client.GetFromJsonAsync<JsonElement>("/api/credentials");
        return list.EnumerateArray().Single(e => e.GetProperty("provider").GetString() == CredentialProviders.TInvestTrade).GetProperty("status").GetString();
    }

    // ─── M1a: удаление/замена токена при живых заявках ─────────────────────────────────────

    [Fact]
    public async Task DeleteTradeToken_WithActiveOrders_409_ThenCancelActive_CancelsWithCurrentToken_ThenDeletes()
    {
        var s = await SeedAsync();
        await SaveTradeTokenAsync(s.UserId);
        var (plan, order) = await LiveOrderAsync(s);

        var blocked = await s.Client.DeleteAsync("/api/credentials/tinvest_trade");
        blocked.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await blocked.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("type").GetString().Should().Be("ActiveOrders");
        body.GetProperty("error").GetString().Should().Contain("активные заявки");
        (await TradeTokenStatusAsync(s)).Should().Be(CredentialStatus.Ok, "без cancelActive токен не трогается");
        SharedGateway.Cancelled.Should().NotContain(order.ClientOrderId);

        var ok = await s.Client.DeleteAsync("/api/credentials/tinvest_trade?cancelActive=true");
        ok.StatusCode.Should().Be(HttpStatusCode.NoContent);

        SharedGateway.Cancelled.Should().Contain(order.ClientOrderId, "снята ТЕКУЩИМ токеном до удаления");
        var stored = (await new TradeOrderRepository(Conn).ListByPlanAsync(plan.Id)).Single();
        stored.Should().Match<TradeOrder>(o => o.Status == TradeOrderStatuses.Cancelled && o.ErrorCode == TradeOrderErrorCodes.TokenChange);
        (await new TradePlanRepository(Conn).GetAsync(s.UserId, plan.Id))!.CancelRequested.Should().BeTrue("остальные строки плана исполнитель закроет");
        (await TradeTokenStatusAsync(s)).Should().Be(CredentialStatus.Missing);
    }

    [Fact]
    public async Task ReplaceTradeToken_WithActiveOrders_409_CancelFailure_KeepsToken_SameTokenAllowed()
    {
        var s = await SeedAsync();
        await SaveTradeTokenAsync(s.UserId);
        var (plan, order) = await LiveOrderAsync(s);
        var put = (string token, string query) =>
            s.Client.PutAsJsonAsync("/api/credentials/tinvest_trade" + query, new { fields = new Dictionary<string, string> { ["token"] = token } });

        (await put("t.trade-new", "")).StatusCode.Should().Be(HttpStatusCode.Conflict);

        SharedGateway.CancelError = new TInvestGatewayException(GatewayErrorKind.Transient, "Unavailable", "сеть");
        try
        {
            var failed = await put("t.trade-new", "?cancelActive=true");
            failed.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await failed.Content.ReadAsStringAsync()).Should().Contain("токен не изменён");
        }
        finally
        {
            SharedGateway.CancelError = null;
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var secret = await scope.ServiceProvider.GetRequiredService<IUserCredentialStore>().GetSecretAsync(s.UserId, CredentialProviders.TInvestTrade);
            secret!["token"].Should().Be("t.trade-current");
        }

        (await new TradeOrderRepository(Conn).ListByPlanAsync(plan.Id)).Single().Status.Should().Be(TradeOrderStatuses.Submitted);

        // Тот же токен (повторная проверка) — не замена, разрешено и при живых заявках.
        (await put("t.trade-current", "")).StatusCode.Should().Be(HttpStatusCode.OK);

        // Замена с cancelActive и рабочим брокером — заявка снята, токен заменён.
        (await put("t.trade-new", "?cancelActive=true")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await new TradeOrderRepository(Conn).ListByPlanAsync(plan.Id)).Single().ErrorCode.Should().Be(TradeOrderErrorCodes.TokenChange);
        SharedGateway.Cancelled.Should().Contain(order.ClientOrderId);
    }

    // ─── M1c: активные заявки и needsAttention в API ───────────────────────────────────────

    [Fact]
    public async Task ActiveOrders_ShownInPlansAndExecutionState_RegardlessOfMode_NeedsAttentionFlag()
    {
        var s = await SeedAsync();
        var (plan, _) = await LiveOrderAsync(s, haltAlertedAt: DateTime.UtcNow);

        var plans = await s.Client.GetFromJsonAsync<JsonElement>("/api/plans");
        plans.GetProperty("dryRun").GetBoolean().Should().BeTrue("режим наблюдения и ворота закрыты");
        plans.GetProperty("activeOrders").GetInt32().Should().Be(1);
        var dto = plans.GetProperty("items").EnumerateArray().Single(p => p.GetProperty("id").GetUInt64() == plan.Id);
        dto.GetProperty("needsAttention").GetBoolean().Should().BeTrue();
        dto.GetProperty("lines")[0].GetProperty("orders")[0].GetProperty("needsAttention").GetBoolean().Should().BeTrue();

        var state = await s.Client.GetFromJsonAsync<JsonElement>("/api/settings/execution");
        state.GetProperty("activeOrders").GetInt32().Should().Be(1);

        var other = await SeedAsync();
        (await other.Client.GetFromJsonAsync<JsonElement>("/api/plans")).GetProperty("activeOrders").GetInt32().Should().Be(0, "только свои заявки");
    }

    // ─── MINOR 1: взаимоисключение исполнителя ─────────────────────────────────────────────

    [Fact]
    public async Task ExecutorLock_OnlyOneHolder_SecondExecutorSkipsTick_UntilReleased()
    {
        var a = new MySqlPlanExecutorLock(Conn);
        var b = new MySqlPlanExecutorLock(Conn);
        var monitor = Mock.Of<IOptionsMonitor<ExecutionOptions>>(m => m.CurrentValue == new ExecutionOptions());
        var executorB = new PlanExecutorService(
            _factory.Services.GetRequiredService<IServiceScopeFactory>(), monitor, b, TimeProvider.System, NullLogger<PlanExecutorService>.Instance);

        var held = await a.TryAcquireAsync(TimeSpan.Zero);
        held.Should().NotBeNull();
        try
        {
            (await b.TryAcquireAsync(TimeSpan.Zero)).Should().BeNull("блокировку держит другой экземпляр");
            (await executorB.RunOnceAsync(CancellationToken.None)).Should().BeNull("тик второго исполнителя пропущен");
        }
        finally
        {
            await held!.DisposeAsync();
        }

        (await executorB.RunOnceAsync(CancellationToken.None)).Should().NotBeNull("после освобождения тик выполняется");
        var again = await a.TryAcquireAsync(TimeSpan.Zero);
        again.Should().NotBeNull("RunOnceAsync освобождает блокировку в finally");
        await again!.DisposeAsync();
    }

    // ─── MINOR 2: продление срока живого плана при подтверждении ───────────────────────────

    [Fact]
    public async Task ApproveLivePlan_ExtendsExpiryToNextSessionClose_NoteReflectsCurrentGate()
    {
        var s = await SeedAsync();
        var now = DateTime.UtcNow;
        var plan = await InsertPlanAsync(s, PlanStatuses.PendingApproval, dryRun: false, expiresAt: now.AddMinutes(20));

        var resp = await s.Client.PostAsync($"/api/plans/{plan.Id}/approve", null);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var expected = TradingHours.NextSessionCloseUtc(PolicyMarket.Moex, now, new PolicyOptions(), new ExecutionOptions().CancelBeforeClose)!.Value;
        var reloaded = (await new TradePlanRepository(Conn).GetAsync(s.UserId, plan.Id))!;
        reloaded.Status.Should().Be(PlanStatuses.Approved);
        reloaded.ExpiresAt.Should().BeCloseTo(expected > now.AddMinutes(20) ? expected : now.AddMinutes(20), TimeSpan.FromMinutes(2));

        // Ворота в тестовой фабрике закрыты (Execution__Enabled=false) — заметка говорит об этом, а не «передан исполнителю».
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("summary").GetProperty("executionNote").GetString().Should().StartWith("не будет исполнен");

        // dry-run план срок не продлевает.
        var dry = await InsertPlanAsync(s, PlanStatuses.PendingApproval, dryRun: true, expiresAt: now.AddMinutes(20));
        (await s.Client.PostAsync($"/api/plans/{dry.Id}/approve", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await new TradePlanRepository(Conn).GetAsync(s.UserId, dry.Id))!.ExpiresAt.Should().BeCloseTo(now.AddMinutes(20), TimeSpan.FromSeconds(2));
    }
}

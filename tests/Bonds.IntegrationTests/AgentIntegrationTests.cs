using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Bonds.Core.Agents;
using Bonds.Core.Capital;
using Bonds.Core.Credentials;
using Bonds.Core.Models;
using Bonds.Core.Policy;
using Bonds.Infrastructure.Agents;
using Bonds.Infrastructure.Agents.Mcp;
using Bonds.Infrastructure.Policy;
using Bonds.Infrastructure.Repositories;
using Bonds.Infrastructure.Telegram;
using Bonds.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;
using BrokerIds = Bonds.Core.Models.Brokers;

namespace Bonds.IntegrationTests;

/// <summary>
/// Задача 68 на реальном MySQL: MCP-сервер bonds (loopback + одноразовый bearer запуска, изоляция пользователей, <c>propose_plan</c> → PendingApproval/source=agent),
/// оркестратор с двойником раннера (ворота: выключен / без подключения / лимит; токен только раннеру), расписание, раздел «Агент» в API и <c>/ask</c> в боте.
/// </summary>
[Collection("Integration")]
public class AgentIntegrationTests
{
    private const string ClaudeToken = "sk-ant-oat01-INTEGRATION-TEST-TOKEN-000000";

    private readonly TestWebApplicationFactory _factory;

    public AgentIntegrationTests(TestWebApplicationFactory factory) => _factory = factory;

    private string Conn => _factory.Database.ConnectionString;

    private sealed class FakeQuotes : IPlanQuoteSource
    {
        public Dictionary<string, BookQuote> TInvest { get; } = new(StringComparer.Ordinal);

        public Task<IReadOnlyDictionary<string, BookQuote>> GetTInvestAsync(IReadOnlyCollection<string> figis, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, BookQuote>>(TInvest.Where(p => figis.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value));

        public Task<IReadOnlyDictionary<string, BookQuote>> GetFreedomAsync(ulong userId, IReadOnlyCollection<string> tickers, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, BookQuote>>(new Dictionary<string, BookQuote>());
    }

    private WebApplicationFactory<Program> NewFactory(FakeQuotes? quotes = null, Dictionary<string, string?>? config = null) =>
        _factory.WithWebHostBuilder(b =>
        {
            if (config is not null) b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(config));
            b.ConfigureServices(s =>
            {
                s.RemoveAll<IPlanQuoteSource>();
                s.AddSingleton<IPlanQuoteSource>(quotes ?? new FakeQuotes());
            });
        });

    private async Task<(ulong UserId, long TelegramId, HttpClient Client)> NewUserAsync(WebApplicationFactory<Program> factory)
    {
        var telegramId = Random.Shared.NextInt64(1, long.MaxValue);
        var userId = await new UserRepository(Conn).CreateAsync(new User { TelegramId = telegramId });
        var client = factory.CreateClient();
        Mvv42TestAuth.Authorize(client, telegramId);
        return (userId, telegramId, client);
    }

    private static async Task SaveClaudeTokenAsync(WebApplicationFactory<Program> factory, ulong userId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IUserCredentialStore>()
            .SaveAsync(userId, CredentialProviders.ClaudeCode, new Dictionary<string, string> { ["oauthToken"] = ClaudeToken }, "0000", null);
    }

    private async Task EnableAgentAsync(ulong userId, int limit = 5) =>
        await new AgentSettingsRepository(Conn).UpsertAsync(userId, new AgentSettings(true, "sonnet", "opus", limit));

    /// <summary>MCP-запрос «как от процесса агента»: RemoteIpAddress = loopback (HttpClient TestServer его не ставит).</summary>
    private static async Task<(int Status, JsonElement? Body)> McpAsync(
        WebApplicationFactory<Program> factory, ulong runId, string? bearer, object payload, bool loopback = true, string? origin = null)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(payload);
        var ctx = await factory.Server.SendAsync(c =>
        {
            c.Request.Method = "POST";
            c.Request.Path = $"/internal/mcp/{runId}";
            c.Request.ContentType = "application/json";
            c.Request.Headers.Accept = "application/json, text/event-stream";
            if (bearer is not null) c.Request.Headers.Authorization = "Bearer " + bearer;
            if (origin is not null) c.Request.Headers.Origin = origin;
            c.Request.Body = new MemoryStream(json);
            c.Request.ContentLength = json.Length;
            if (loopback) c.Connection.RemoteIpAddress = IPAddress.Loopback;
        });

        var text = await new StreamReader(ctx.Response.Body, Encoding.UTF8).ReadToEndAsync();
        return (ctx.Response.StatusCode, text.Length > 0 && text.TrimStart().StartsWith('{') ? JsonDocument.Parse(text).RootElement.Clone() : null);
    }

    private static object ToolCall(string name, object? args = null) =>
        new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name, arguments = args ?? new { } } };

    private static string ToolText(JsonElement body) => body.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;

    // ─── MCP: доступ ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Mcp_Auth_WrongExpiredOtherRunOrMissingToken_401_NonLoopback_404_Origin_403()
    {
        var factory = NewFactory();
        var tokens = factory.Services.GetRequiredService<AgentMcpTokens>();
        var good = tokens.Issue(userId: 1, runId: 501, TimeSpan.FromMinutes(5));
        var expired = tokens.Issue(userId: 1, runId: 502, TimeSpan.FromMilliseconds(-1));
        var otherUser = tokens.Issue(userId: 2, runId: 503, TimeSpan.FromMinutes(5));
        var ping = new { jsonrpc = "2.0", id = 1, method = "ping" };

        (await McpAsync(factory, 501, good, ping)).Status.Should().Be(200);
        (await McpAsync(factory, 501, null, ping)).Status.Should().Be(401);
        (await McpAsync(factory, 501, "not-a-token", ping)).Status.Should().Be(401);
        (await McpAsync(factory, 502, expired, ping)).Status.Should().Be(401);
        (await McpAsync(factory, 501, otherUser, ping)).Status.Should().Be(401, "токен другого пользователя/запуска к этому запуску не подходит");
        (await McpAsync(factory, 501, good, ping, loopback: false)).Status.Should().Be(404);
        (await McpAsync(factory, 501, good, ping, origin: "https://evil.example")).Status.Should().Be(403);

        tokens.Revoke(good);
        (await McpAsync(factory, 501, good, ping)).Status.Should().Be(401, "после запуска токен отозван");

        // Обычный HttpClient (как nginx/docker-сеть: не loopback) — эндпоинта «нет».
        var external = await factory.CreateClient().PostAsJsonAsync("/internal/mcp/501", ping);
        external.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Mcp_InitializeAndToolsList_Work_NotificationIs202()
    {
        var factory = NewFactory();
        var bearer = factory.Services.GetRequiredService<AgentMcpTokens>().Issue(1, 601, TimeSpan.FromMinutes(5));

        var (status, init) = await McpAsync(factory, 601, bearer,
            new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "t", version = "1" } } });
        status.Should().Be(200);
        init!.Value.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString().Should().Be("bonds");

        (await McpAsync(factory, 601, bearer, new { jsonrpc = "2.0", method = "notifications/initialized" })).Status.Should().Be(202);

        var (_, list) = await McpAsync(factory, 601, bearer, new { jsonrpc = "2.0", id = 2, method = "tools/list" });
        list!.Value.GetProperty("result").GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString())
            .Should().Contain(["portfolio", "plans", "signals", "propose_plan"]);
    }

    [Fact]
    public async Task Mcp_ToolsSeeOnlyTokenOwnersData()
    {
        var factory = NewFactory();
        var (userA, _, _) = await NewUserAsync(factory);
        var (userB, _, _) = await NewUserAsync(factory);
        var plans = new TradePlanRepository(Conn);
        var planB = await plans.InsertAsync(new TradePlan
        {
            UserId = userB, Rule = PlanRules.P5Withdraw, Trigger = "secret-of-B", Status = PlanStatuses.PendingApproval, CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(30), SummaryJson = "{}",
        });

        var tokens = factory.Services.GetRequiredService<AgentMcpTokens>();
        var bearerA = tokens.Issue(userA, 701, TimeSpan.FromMinutes(5));
        var bearerB = tokens.Issue(userB, 702, TimeSpan.FromMinutes(5));

        var (_, asA) = await McpAsync(factory, 701, bearerA, ToolCall("plans"));
        ToolText(asA!.Value).Should().NotContain("secret-of-B");

        var (_, asB) = await McpAsync(factory, 702, bearerB, ToolCall("plans"));
        ToolText(asB!.Value).Should().Contain("secret-of-B").And.Contain($"\"id\":{planB}");

        var (_, portfolio) = await McpAsync(factory, 701, bearerA, ToolCall("portfolio"));
        portfolio!.Value.GetProperty("result").GetProperty("isError").GetBoolean().Should().BeFalse();
    }

    // ─── propose_plan ─────────────────────────────────────────────────────────────────────

    /// <summary>T-Invest: фонд A (100 паёв, вход 150, стакан 166.57/166.59) в корзине liquidity — достаточно для плана вывода.</summary>
    private async Task<(WebApplicationFactory<Program> Factory, ulong UserId, long TelegramId, HttpClient Client)> SeedWithdrawableAsync()
    {
        var sfx = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var a = $"GA{sfx}";
        var quotes = new FakeQuotes();
        var factory = NewFactory(quotes, new Dictionary<string, string?> { ["Policy:OwnFunds:100"] = a });
        var (userId, telegramId, client) = await NewUserAsync(factory);

        var accounts = new AccountRepository(Conn);
        var accountId = await accounts.CreateAsync(new Account { UserId = userId, Name = "Основной счёт", BrokerAccountId = "T-" + sfx });
        await accounts.UpdateBalanceAsync(accountId, 0m, 16_658m, DateTime.UtcNow);

        var fundId = await new FundRepository(Conn).UpsertAsync(new Fund { Figi = $"FIGIG{sfx}", Isin = $"RUG{sfx}", Ticker = a, Name = "Фонд A" });
        await new FundPositionRepository(Conn).UpsertAsync(new FundPosition
        {
            AccountId = accountId, FundId = fundId, Quantity = 100m, AvgPurchasePriceRub = 150m, CurrentPriceRub = 166.58m, PriceAsOf = DateTime.UtcNow,
        });
        await new InstrumentTradingMetaRepository(Conn).UpsertAsync(new InstrumentTradingMeta
        {
            Broker = BrokerIds.TInvest, InstrumentKey = $"ISIN{a}", Ticker = a, Figi = $"FIGIG{sfx}", ClassCode = "TQBR", Lot = 1m, MinPriceIncrement = 0.01m,
            Currency = "RUB", ApiTradeAvailable = true, BuyAvailable = true, SellAvailable = true, PaysDistributions = false, UpdatedAt = DateTime.UtcNow,
        });
        quotes.TInvest[$"FIGIG{sfx}"] = new BookQuote(166.57m, 166.59m, 166.58m);

        await new CapitalBasketRepository(Conn).SeedIfEmptyAsync(userId,
        [
            new CapitalBasket
            {
                UserId = userId, Portfolio = CapitalPortfolios.Rub, Code = "liquidity", Title = "Ликвидность", TargetWeight = 1m, Band = 0.05m, HardBand = 0.10m,
                LiquidityTier = 0, FillRule = BasketFillRules.Fixed, AutoBuy = true, AutoSell = true, SortOrder = 10,
                Members = [new CapitalBasketMember { Broker = BrokerIds.TInvest, InstrumentKey = a, SubWeight = 1m, MatchRule = BasketMatchRules.Instrument }],
            },
        ]);
        return (factory, userId, telegramId, client);
    }

    [Fact]
    public async Task ProposePlan_CreatesPendingApprovalPlan_SourceAgent_NotifiesWithButtons()
    {
        var (factory, userId, telegramId, client) = await SeedWithdrawableAsync();
        var bearer = factory.Services.GetRequiredService<AgentMcpTokens>().Issue(userId, 801, TimeSpan.FromMinutes(5));

        var (status, body) = await McpAsync(factory, 801, bearer,
            ToolCall("propose_plan", new { kind = "withdraw", amount = 5_000m, rationale = "Оферта через неделю — готовим деньги <script>" }));

        status.Should().Be(200);
        body!.Value.GetProperty("result").GetProperty("isError").GetBoolean().Should().BeFalse(ToolText(body.Value));
        using var created = JsonDocument.Parse(ToolText(body.Value));
        created.RootElement.GetProperty("created").GetBoolean().Should().BeTrue();
        created.RootElement.GetProperty("status").GetString().Should().Be(PlanStatuses.PendingApproval);
        var planId = created.RootElement.GetProperty("planId").GetUInt64();

        var plan = await new TradePlanRepository(Conn).GetAsync(userId, planId);
        plan!.Status.Should().Be(PlanStatuses.PendingApproval);
        plan.Source.Should().Be(PlanSources.Agent);
        plan.DryRun.Should().BeTrue("ворота исполнения закрыты — dry-run решают обычные ворота");
        plan.DecidedVia.Should().BeNull();
        plan.Trigger.Should().StartWith("ИИ-агент: ");

        var dto = await client.GetFromJsonAsync<JsonElement>($"/api/plans/{planId}");
        dto.GetProperty("source").GetString().Should().Be("agent");

        var sent = factory.Services.GetRequiredService<FakeTelegramBotClient>().SentTo(telegramId);
        sent.Should().ContainSingle(m => m.Keyboard != null, "план ушёл пользователю тем же путём подтверждения — с кнопками");
        sent.Single().Html.Should().NotContain("<script>");
    }

    [Fact]
    public async Task ProposePlan_ValidationFailure_IsToolError_NoPlan()
    {
        var (factory, userId, _, _) = await SeedWithdrawableAsync();
        var bearer = factory.Services.GetRequiredService<AgentMcpTokens>().Issue(userId, 802, TimeSpan.FromMinutes(5));

        var (_, body) = await McpAsync(factory, 802, bearer, ToolCall("propose_plan", new { kind = "withdraw", amount = 1m, fraction = 0.1m, rationale = "x" }));
        body!.Value.GetProperty("result").GetProperty("isError").GetBoolean().Should().BeTrue();
        ToolText(body.Value).Should().Contain("ровно одно");

        var (_, exit) = await McpAsync(factory, 802, bearer, ToolCall("propose_plan", new { kind = "exit_basket", basketCode = "liquidity", rationale = "x" }));
        exit!.Value.GetProperty("result").GetProperty("isError").GetBoolean().Should().BeTrue("у корзины есть цель — та же валидация, что у ручного плана");

        (await new TradePlanRepository(Conn).ListAsync(userId, null, 10)).Should().BeEmpty();
    }

    // ─── Оркестратор и ворота ─────────────────────────────────────────────────────────────

    private static async Task<AgentRunOutcome> RunAsync(WebApplicationFactory<Program> factory, ulong userId, AgentTaskKind kind, string? question = null)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AgentService>().RunAsync(userId, kind, question);
    }

    [Fact]
    public async Task Run_Disabled_OrWithoutCapability_DoesNotRun()
    {
        var factory = NewFactory();
        var runner = factory.Services.GetRequiredService<FakeAgentRunner>();
        var (noCap, _, _) = await NewUserAsync(factory);
        await EnableAgentAsync(noCap);
        var (disabled, _, _) = await NewUserAsync(factory);
        await SaveClaudeTokenAsync(factory, disabled);

        (await RunAsync(factory, noCap, AgentTaskKind.DailyDigest)).SkipReason.Should().Be(AgentRunGate.NoCapability);
        (await RunAsync(factory, disabled, AgentTaskKind.DailyDigest)).SkipReason.Should().Be(AgentRunGate.NotEnabled);

        runner.CallsFor(noCap).Should().BeEmpty();
        runner.CallsFor(disabled).Should().BeEmpty();
        (await new AgentRunRepository(Conn).ListAsync(disabled, 10)).Should().BeEmpty();
    }

    [Fact]
    public async Task Run_Enabled_PassesTokenToRunnerOnly_RecordsRun_NotifiesEscaped_AndRespectsDailyLimit()
    {
        var factory = NewFactory();
        var runner = factory.Services.GetRequiredService<FakeAgentRunner>();
        var (userId, telegramId, _) = await NewUserAsync(factory);
        await SaveClaudeTokenAsync(factory, userId);
        await EnableAgentAsync(userId, limit: 2);

        var first = await RunAsync(factory, userId, AgentTaskKind.OnDemand, "какие оферты?");
        first.Started.Should().BeTrue();
        var call = runner.CallsFor(userId).Single();
        call.Token.Should().Be(ClaudeToken);
        call.Task.Model.Should().Be("sonnet");
        call.Task.McpUrl.Should().Be($"http://127.0.0.1:5001/internal/mcp/{first.Run!.Id}");
        call.Task.Prompt.Should().Contain("какие оферты?").And.NotContain(ClaudeToken);
        call.Task.AllowWebResearch.Should().BeFalse();
        factory.Services.GetRequiredService<AgentMcpTokens>().Validate(call.Task.McpBearer, first.Run.Id).Should().BeNull("bearer отозван после запуска");

        var stored = (await new AgentRunRepository(Conn).ListAsync(userId, 10)).Single();
        stored.Kind.Should().Be("OnDemand");
        stored.Finished.Should().NotBeNull();
        stored.ExitCode.Should().Be(0);
        stored.CostUsd.Should().Be(0.02m);
        stored.ResultText.Should().Contain("ответ агента");

        var msg = factory.Services.GetRequiredService<FakeTelegramBotClient>().SentTo(telegramId).Single();
        msg.Html.Should().Contain("&lt;b&gt;&amp;").And.NotContain("ответ агента для OnDemand <b>");
        msg.Keyboard.Should().BeNull();

        (await RunAsync(factory, userId, AgentTaskKind.MonthlyReview)).Started.Should().BeTrue();
        runner.CallsFor(userId).Last().Task.Model.Should().Be("opus");
        var third = await RunAsync(factory, userId, AgentTaskKind.DailyDigest);
        third.Started.Should().BeFalse();
        third.SkipReason.Should().Contain("Лимит");
        runner.CallsFor(userId).Should().HaveCount(2);
    }

    [Fact]
    public async Task Run_EventReview_AtMostThreePerDay()
    {
        var factory = NewFactory();
        var (userId, _, _) = await NewUserAsync(factory);
        await SaveClaudeTokenAsync(factory, userId);
        await EnableAgentAsync(userId, limit: 10);

        for (var i = 0; i < 3; i++) (await RunAsync(factory, userId, AgentTaskKind.EventReview, "• дефолт")).Started.Should().BeTrue();
        (await RunAsync(factory, userId, AgentTaskKind.EventReview)).Started.Should().BeFalse();
        (await RunAsync(factory, userId, AgentTaskKind.OnDemand, "q")).Started.Should().BeTrue();
    }

    [Fact]
    public async Task Scheduler_CollectsDigest_OnlyForEnabledUsersWithCapability_OncePerDay()
    {
        var factory = NewFactory();
        var (ready, _, _) = await NewUserAsync(factory);
        await SaveClaudeTokenAsync(factory, ready);
        await EnableAgentAsync(ready);
        var (noCap, _, _) = await NewUserAsync(factory);
        await EnableAgentAsync(noCap);
        var (disabled, _, _) = await NewUserAsync(factory);
        await SaveClaudeTokenAsync(factory, disabled);

        // Ближайший прошедший будний день, 16:30 UTC = 19:30 МСК (журнал запусков пуст для новых пользователей).
        var day = DateTime.UtcNow.Date;
        while (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || day.Day == 1) day = day.AddDays(-1);
        var at = DateTime.SpecifyKind(day.AddHours(16).AddMinutes(30), DateTimeKind.Utc);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var due = await scope.ServiceProvider.GetRequiredService<AgentScheduler>().CollectDueAsync(at);
            due.Where(d => d.UserId == ready).Select(d => d.Kind).Should().Equal(AgentTaskKind.DailyDigest);
            due.Should().NotContain(d => d.UserId == noCap || d.UserId == disabled);

            (await scope.ServiceProvider.GetRequiredService<AgentScheduler>().CollectDueAsync(at.AddHours(-1))).Should().NotContain(d => d.UserId == ready, "до 19:00 МСК рано");
        }

        await new AgentRunRepository(Conn).InsertStartedAsync(new AgentRun { UserId = ready, Kind = nameof(AgentTaskKind.DailyDigest), Started = at.AddMinutes(-10) });
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            (await scope.ServiceProvider.GetRequiredService<AgentScheduler>().CollectDueAsync(at)).Should().NotContain(d => d.UserId == ready, "сегодня дайджест уже был");
        }
    }

    [Fact]
    public async Task Scheduler_ServerDisabled_CollectsNothing()
    {
        var factory = NewFactory(config: new Dictionary<string, string?> { ["Agent:Enabled"] = "false" });
        var (ready, _, _) = await NewUserAsync(factory);
        await SaveClaudeTokenAsync(factory, ready);
        await EnableAgentAsync(ready);
        var monday = DateTime.UtcNow.Date;
        while (monday.DayOfWeek != DayOfWeek.Monday || monday.Day == 1) monday = monday.AddDays(-1);

        await using var scope = factory.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<AgentScheduler>().CollectDueAsync(DateTime.SpecifyKind(monday.AddHours(16).AddMinutes(5), DateTimeKind.Utc)))
            .Should().BeEmpty();
        (await RunAsync(factory, ready, AgentTaskKind.OnDemand, "q")).SkipReason.Should().Be(AgentRunGate.ServerDisabled);
    }

    // ─── API раздела «Агент» ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task AgentApi_DefaultsDisabled_EnableRequiresCapability_ValidatesModelAndLimit()
    {
        var factory = NewFactory();
        var (userId, _, client) = await NewUserAsync(factory);

        var state = await client.GetFromJsonAsync<JsonElement>("/api/agent");
        state.GetProperty("enabled").GetBoolean().Should().BeFalse();
        state.GetProperty("model").GetString().Should().Be("sonnet");
        state.GetProperty("credentialOk").GetBoolean().Should().BeFalse();
        state.GetProperty("runs").GetArrayLength().Should().Be(0);

        (await client.PutAsJsonAsync("/api/agent/settings", new { enabled = true, model = "sonnet", monthlyModel = "opus", dailyRunLimit = 5 }))
            .StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, "без подключения Claude Code включить нельзя");

        await SaveClaudeTokenAsync(factory, userId);
        (await client.PutAsJsonAsync("/api/agent/settings", new { enabled = true, model = "--dangerously-skip-permissions", monthlyModel = "opus", dailyRunLimit = 5 }))
            .StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await client.PutAsJsonAsync("/api/agent/settings", new { enabled = true, model = "sonnet", monthlyModel = "opus", dailyRunLimit = 0 }))
            .StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await client.PutAsJsonAsync("/api/agent/settings", new { enabled = true, model = "sonnet", monthlyModel = "opus", dailyRunLimit = 999 }))
            .StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        var saved = await client.PutAsJsonAsync("/api/agent/settings", new { enabled = true, model = "haiku", monthlyModel = "opus", dailyRunLimit = 3 });
        saved.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await saved.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("enabled").GetBoolean().Should().BeTrue();
        body.GetProperty("model").GetString().Should().Be("haiku");
        body.GetProperty("dailyRunLimit").GetInt32().Should().Be(3);

        await RunAsync(factory, userId, AgentTaskKind.OnDemand, "вопрос");
        var history = await client.GetFromJsonAsync<JsonElement>("/api/agent");
        history.GetProperty("runsToday").GetInt32().Should().Be(1);
        var run = history.GetProperty("runs")[0];
        run.GetProperty("kind").GetString().Should().Be("OnDemand");
        run.GetProperty("question").GetString().Should().Be("вопрос");
        run.GetProperty("resultText").GetString().Should().Contain("<b>&", "API отдаёт текст как есть — экранирует фронт");
    }

    [Fact]
    public async Task AgentApi_IsolatedPerUser()
    {
        var factory = NewFactory();
        var (owner, _, _) = await NewUserAsync(factory);
        await SaveClaudeTokenAsync(factory, owner);
        await EnableAgentAsync(owner);
        await RunAsync(factory, owner, AgentTaskKind.OnDemand, "мой секретный вопрос");

        var (_, _, other) = await NewUserAsync(factory);
        var state = await other.GetFromJsonAsync<JsonElement>("/api/agent");
        state.GetProperty("runs").GetArrayLength().Should().Be(0);
    }

    // ─── /ask в боте ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task BotAsk_Disabled_RepliesReason_Enabled_Enqueues_ThenCooldown()
    {
        var factory = NewFactory();
        var bot = factory.Services.GetRequiredService<FakeTelegramBotClient>();
        var queue = factory.Services.GetRequiredService<AgentWorkQueue>();
        var (userId, tg, _) = await NewUserAsync(factory);

        async Task SendAsync(string text)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ITelegramUpdateHandler>()
                .HandleAsync(new BotUpdate(Random.Shared.NextInt64(1, long.MaxValue), new BotIncomingMessage(tg, "private", tg, "T", text), null));
        }

        await SendAsync("/ask что с портфелем?");
        bot.SentTo(tg).Last().Html.Should().Contain("Claude Code");

        await SaveClaudeTokenAsync(factory, userId);
        await EnableAgentAsync(userId);
        await SendAsync("/ask что с портфелем? <b>");
        bot.SentTo(tg).Last().Html.Should().Contain("Вопрос принят");

        await SendAsync("/ask ещё вопрос");
        bot.SentTo(tg).Last().Html.Should().Contain("Не так часто");

        var items = new List<AgentWorkItem>();
        while (queue.Reader.TryRead(out var item)) items.Add(item);
        items.Should().Contain(i => i.UserId == userId && i.Kind == AgentTaskKind.OnDemand && i.Question == "что с портфелем? <b>");
        factory.Services.GetRequiredService<FakeAgentRunner>().CallsFor(userId).Should().BeEmpty("в тестах фоновые сервисы вырезаны — запуск только через очередь");
    }
}

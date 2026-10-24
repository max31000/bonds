using Bonds.Core.Credentials;
using Bonds.Core.Execution;
using Bonds.Core.Models;
using Bonds.Core.Policy;
using Bonds.Core.Telegram;
using Bonds.Infrastructure.Execution;
using Bonds.Infrastructure.Repositories;
using Bonds.Infrastructure.Telegram;
using Bonds.IntegrationTests.Infrastructure;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using Xunit;

namespace Bonds.IntegrationTests;

/// <summary>
/// Задача 64 на реальном MySQL: обработчик апдейтов бота (<see cref="TelegramBotUpdateHandler"/>) — кнопки подтверждения/отклонения (чужой план, незнакомый id, неверный nonce,
/// истёкший, повторное нажатие), команды (/stop /resume /plans /status /help, ошибки планов) и личный чат. Сеть подменена <see cref="FakeTelegramBotClient"/>.
/// Заявки брокерам не отправляются: «Подтвердить» лишь переводит план в Approved (dry-run).
/// </summary>
[Collection("Integration")]
public class TelegramBotTests
{
    private readonly TestWebApplicationFactory _factory;

    public TelegramBotTests(TestWebApplicationFactory factory) => _factory = factory;

    private string Conn => _factory.Database.ConnectionString;

    private FakeTelegramBotClient Bot => _factory.Services.GetRequiredService<FakeTelegramBotClient>();

    private async Task<(ulong UserId, long TelegramId)> NewUserAsync()
    {
        var telegramId = Random.Shared.NextInt64(1, long.MaxValue);
        var userId = await new UserRepository(Conn).CreateAsync(new User { TelegramId = telegramId });
        return (userId, telegramId);
    }

    private async Task<TradePlan> NewPlanAsync(ulong userId, string status = PlanStatuses.PendingApproval, TimeSpan? ttl = null)
    {
        var plan = new TradePlan
        {
            UserId = userId,
            Rule = PlanRules.P1Sweep,
            Trigger = "Тест " + Guid.NewGuid().ToString("N")[..6],
            Status = status,
            DryRun = true,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow + (ttl ?? TimeSpan.FromMinutes(30)),
            BotNonce = BotCallback.NewNonce(),
            SummaryJson = """{"portfolio":"rub","currency":"RUB","cashBefore":7340,"cashAfter":11,"buyTotal":7329,"reservedBuy":7329,"feeTotal":0}""",
            Lines =
            [
                new TradePlanLine
                {
                    LineNo = 1, Broker = "tinvest", AccountId = 1, InstrumentKey = "ISINX", Ticker = "TMON@", Side = PlanSides.Buy, QuantityLots = 44, LotSize = 1m,
                    LimitPrice = 166.57m, EstAmount = 7329.08m, Currency = "RUB", Reason = "тест",
                },
            ],
        };
        plan.Id = await new TradePlanRepository(Conn).InsertAsync(plan);
        return plan;
    }

    private async Task<TradePlan> ReloadAsync(TradePlan plan) => (await new TradePlanRepository(Conn).GetAsync(plan.UserId, plan.Id))!;

    private async Task SendAsync(BotUpdate update, Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>? factory = null)
    {
        using var scope = (factory ?? _factory).Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ITelegramUpdateHandler>().HandleAsync(update);
    }

    private static long _updateId = 1000;

    private Task PressAsync(
        long fromId, string callbackId, string data, long? chatId = null, string chatType = "private", Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>? factory = null) =>
        SendAsync(new BotUpdate(Interlocked.Increment(ref _updateId), null, new BotIncomingCallback(callbackId, fromId, chatId ?? fromId, chatType, 900, data)), factory);

    private Task TextAsync(long fromId, string text, long? chatId = null, string chatType = "private") =>
        SendAsync(new BotUpdate(Interlocked.Increment(ref _updateId), new BotIncomingMessage(chatId ?? fromId, chatType, fromId, "T", text), null));

    private FakeTelegramBotClient.Answered AnswerFor(string callbackId) => Bot.Answers.Single(a => a.CallbackId == callbackId);

    private static string Approve(TradePlan p) => BotCallback.Build(p.Id, BotPlanAction.Approve, p.BotNonce!);

    private static string Reject(TradePlan p) => BotCallback.Build(p.Id, BotPlanAction.Reject, p.BotNonce!);

    // ─── Кнопки ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Approve_ChangesStatus_EditsMessage_AndRepeatPressIsHarmless()
    {
        var (userId, tg) = await NewUserAsync();
        var plan = await NewPlanAsync(userId);
        var cb1 = "cb-" + Guid.NewGuid();
        var cb2 = "cb-" + Guid.NewGuid();

        await PressAsync(tg, cb1, Approve(plan));

        var approved = await ReloadAsync(plan);
        approved.Status.Should().Be(PlanStatuses.Approved);
        approved.DecidedVia.Should().Be(DecidedVia.Telegram);
        approved.SummaryJson.Should().Contain("не исполнен: dry-run");
        AnswerFor(cb1).Text.Should().Be("Подтверждено.");
        var edit = Bot.Edits.Single(e => e.ChatId == tg);
        edit.MessageId.Should().Be(900);
        edit.Html.Should().Contain("✅ Подтверждено").And.Contain("режим наблюдения: сделка не отправляется");
        edit.Keyboard.Should().BeNull("после решения кнопок нет");

        // Повторное нажатие (и противоположное действие) безопасно: статус не меняется.
        await PressAsync(tg, cb2, Reject(plan));
        (await ReloadAsync(plan)).Status.Should().Be(PlanStatuses.Approved);
        AnswerFor(cb2).Text.Should().Contain("уже обработан");
    }

    [Fact]
    public async Task T67_LivePlan_TwoStepConfirm_FirstTapShowsOrders_SecondApproves_NonceOneTime()
    {
        var (userId, tg) = await NewUserAsync();
        var plan = await NewPlanAsync(userId);
        await using (var conn = new MySqlConnector.MySqlConnection(Conn))
            await Dapper.SqlMapper.ExecuteAsync(conn, "UPDATE trade_plans SET dry_run = 0 WHERE id = @Id", new { plan.Id });
        var firstApprove = Approve(plan);

        // T-67E (M3): «живой» решается по ТЕКУЩИМ воротам (как пересчитает ApproveAsync) — открываем их: env, режим live, проверенный торговый токен.
        var live = _factory.WithWebHostBuilder(b => b.UseSetting("Execution:Enabled", "true"));
        await new UserSettingsRepository(Conn).SetExecutionModeAsync(userId, ExecutionModes.Live);
        using (var scope = live.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IUserCredentialStore>().SaveAsync(
                userId, CredentialProviders.TInvestTrade, new Dictionary<string, string> { ["token"] = "t.trade-test" }, "test", TradeCredentialMeta.Build("ACC-T67E"));
        var bot = live.Services.GetRequiredService<FakeTelegramBotClient>();
        FakeTelegramBotClient.Answered AnswerFor(string callbackId) => bot.Answers.Single(a => a.CallbackId == callbackId);
        Task PressAsync(long fromId, string callbackId, string data) => this.PressAsync(fromId, callbackId, data, factory: live);

        // Шаг 1: «Подтвердить» — план НЕ решён, сообщение показывает реальные заявки и кнопку «Отправить заявки» с новым nonce.
        var cb1 = "cb-" + Guid.NewGuid();
        await PressAsync(tg, cb1, firstApprove);
        var afterFirst = await ReloadAsync(plan);
        afterFirst.Status.Should().Be(PlanStatuses.PendingApproval);
        afterFirst.BotNonce.Should().NotBe(plan.BotNonce, "nonce меняется на каждом шаге");
        var edit = bot.Edits.Last(e => e.ChatId == tg);
        edit.Html.Should().Contain("РЕАЛЬНЫЕ ЗАЯВКИ").And.Contain("TMON@");
        var send = BotCallback.Build(plan.Id, BotPlanAction.Send, afterFirst.BotNonce!);
        edit.Keyboard!.Rows.SelectMany(r => r).Select(b => b.CallbackData).Should().Contain(send);

        // Старая кнопка «Подтвердить» после смены nonce недействительна.
        var cbOld = "cb-" + Guid.NewGuid();
        await PressAsync(tg, cbOld, firstApprove);
        AnswerFor(cbOld).Text.Should().Contain("недействительна");
        (await ReloadAsync(plan)).Status.Should().Be(PlanStatuses.PendingApproval);

        // Шаг 2: «Отправить заявки» — план подтверждён; та же кнопка второй раз не работает (nonce одноразовый).
        var cb2 = "cb-" + Guid.NewGuid();
        await PressAsync(tg, cb2, send);
        var approved = await ReloadAsync(plan);
        approved.Status.Should().Be(PlanStatuses.Approved);
        approved.DecidedVia.Should().Be(DecidedVia.Telegram);
        approved.BotNonce.Should().NotBe(afterFirst.BotNonce);

        var cb3 = "cb-" + Guid.NewGuid();
        await PressAsync(tg, cb3, send);
        AnswerFor(cb3).Text.Should().Contain("недействительна");
    }

    [Fact]
    public async Task T67_PlansList_LivePlan_HasNoApproveButtons()
    {
        var (userId, tg) = await NewUserAsync();
        var plan = await NewPlanAsync(userId);
        await using (var conn = new MySqlConnector.MySqlConnection(Conn))
            await Dapper.SqlMapper.ExecuteAsync(conn, "UPDATE trade_plans SET dry_run = 0 WHERE id = @Id", new { plan.Id });

        await TextAsync(tg, "/plans");

        var message = Bot.SentTo(tg).Single();
        message.Html.Should().Contain($"№{plan.Id}");
        (message.Keyboard?.Rows.SelectMany(r => r).Where(b => b.CallbackData is not null) ?? []).Should().BeEmpty();
    }

    [Fact]
    public async Task Reject_ChangesStatus()
    {
        var (userId, tg) = await NewUserAsync();
        var plan = await NewPlanAsync(userId);
        var cb = "cb-" + Guid.NewGuid();

        await PressAsync(tg, cb, Reject(plan));

        var rejected = await ReloadAsync(plan);
        rejected.Status.Should().Be(PlanStatuses.Rejected);
        rejected.DecidedVia.Should().Be(DecidedVia.Telegram);
        AnswerFor(cb).Text.Should().Be("Отклонено.");
        Bot.Edits.Single(e => e.ChatId == tg).Html.Should().Contain("❌ Отклонено");
    }

    [Fact]
    public async Task Callback_OnAnotherUsersPlan_IsRejected_AndPlanUntouched()
    {
        var (ownerId, _) = await NewUserAsync();
        var (_, strangerTg) = await NewUserAsync();
        var plan = await NewPlanAsync(ownerId);
        var cb = "cb-" + Guid.NewGuid();

        // Чужой (но зарегистрированный) пользователь, даже зная и id плана, и nonce, чужим планом управлять не может.
        await PressAsync(strangerTg, cb, Approve(plan));

        (await ReloadAsync(plan)).Status.Should().Be(PlanStatuses.PendingApproval);
        AnswerFor(cb).Text.Should().Contain("не найден");
        Bot.Edits.Where(e => e.ChatId == strangerTg).Should().BeEmpty();
    }

    [Fact]
    public async Task Callback_FromUnregisteredTelegramId_GetsNoAccess()
    {
        var (userId, _) = await NewUserAsync();
        var plan = await NewPlanAsync(userId);
        var unknownTg = Random.Shared.NextInt64(1, long.MaxValue);
        var cb = "cb-" + Guid.NewGuid();

        await PressAsync(unknownTg, cb, Approve(plan));

        (await ReloadAsync(plan)).Status.Should().Be(PlanStatuses.PendingApproval);
        AnswerFor(cb).Should().Be(new FakeTelegramBotClient.Answered(cb, BotTexts.NoAccess, true));
    }

    [Fact]
    public async Task Callback_WithWrongNonce_OrGarbage_IsRejected()
    {
        var (userId, tg) = await NewUserAsync();
        var plan = await NewPlanAsync(userId);
        var wrong = "cb-" + Guid.NewGuid();
        var garbage = "cb-" + Guid.NewGuid();

        await PressAsync(tg, wrong, BotCallback.Build(plan.Id, BotPlanAction.Approve, BotCallback.NewNonce()));
        await PressAsync(tg, garbage, "p:not-a-plan");

        (await ReloadAsync(plan)).Status.Should().Be(PlanStatuses.PendingApproval);
        AnswerFor(wrong).Text.Should().Contain("недействительна");
        AnswerFor(garbage).Text.Should().Contain("недействительна");
    }

    [Fact]
    public async Task Callback_OnExpiredPlan_DoesNotApprove()
    {
        var (userId, tg) = await NewUserAsync();
        var plan = await NewPlanAsync(userId, ttl: TimeSpan.FromMinutes(-1));
        var cb = "cb-" + Guid.NewGuid();

        await PressAsync(tg, cb, Approve(plan));

        (await ReloadAsync(plan)).Status.Should().NotBe(PlanStatuses.Approved);
        AnswerFor(cb).Text.Should().Contain("истёк");
        Bot.Edits.Single(e => e.ChatId == tg).Html.Should().Contain("Срок действия истёк");
    }

    [Fact]
    public async Task Callback_OutsidePrivateChat_IsIgnored()
    {
        var (userId, tg) = await NewUserAsync();
        var plan = await NewPlanAsync(userId);
        var cb = "cb-" + Guid.NewGuid();

        await PressAsync(tg, cb, Approve(plan), chatId: -100123, chatType: "supergroup");

        (await ReloadAsync(plan)).Status.Should().Be(PlanStatuses.PendingApproval);
        Bot.Edits.Where(e => e.ChatId == -100123).Should().BeEmpty();
    }

    // ─── Команды ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Message_FromUnknownUser_GetsNoAccess_AndNothingElse()
    {
        var unknownTg = Random.Shared.NextInt64(1, long.MaxValue);

        await TextAsync(unknownTg, "/status");
        await TextAsync(unknownTg, "/stop");

        Bot.SentTo(unknownTg).Select(m => m.Html).Should().Equal(BotTexts.NoAccess, BotTexts.NoAccess);
    }

    [Fact]
    public async Task Message_InGroupChat_IsIgnored()
    {
        var (_, tg) = await NewUserAsync();

        await TextAsync(tg, "/status", chatId: -4242, chatType: "group");

        Bot.SentTo(-4242).Should().BeEmpty();
        Bot.SentTo(tg).Should().BeEmpty();
    }

    [Fact]
    public async Task StopAndResume_TogglePauseFlag()
    {
        var (userId, tg) = await NewUserAsync();
        var settings = new UserSettingsRepository(Conn);
        (await settings.IsExecutionPausedAsync(userId)).Should().BeFalse();

        await TextAsync(tg, "/stop");
        (await settings.IsExecutionPausedAsync(userId)).Should().BeTrue();
        (await settings.GetByUserIdAsync(userId))!.ExecutionPaused.Should().BeTrue();

        await TextAsync(tg, "/help");
        Bot.SentTo(tg).Last().Html.Should().Contain("Сейчас: пауза");

        await TextAsync(tg, "/resume");
        (await settings.IsExecutionPausedAsync(userId)).Should().BeFalse();
        Bot.SentTo(tg).Select(m => m.Html).Should().Contain(h => h.Contains("Пауза включена")).And.Contain(h => h.Contains("Пауза снята"));
    }

    [Fact]
    public async Task T67_SameUpdateIdTwice_PlanCommandHandledOnce()
    {
        // Задача 67 (#16): Telegram прислал тот же апдейт повторно (offset не успел сохраниться до рестарта) — /withdraw и /inflow не строят второй план.
        var (_, tg) = await NewUserAsync();
        var id = Interlocked.Increment(ref _updateId);
        var update = new BotUpdate(id, new BotIncomingMessage(tg, "private", tg, "T", "/withdraw 1000"), null);

        await SendAsync(update);
        var afterFirst = Bot.SentTo(tg).Count;
        await SendAsync(update);

        afterFirst.Should().Be(1, "первый раз команда обработана (ответ — план или причина)");
        Bot.SentTo(tg).Count.Should().Be(afterFirst, "повтор того же update_id игнорируется");

        await SendAsync(new BotUpdate(Interlocked.Increment(ref _updateId), new BotIncomingMessage(tg, "private", tg, "T", "/inflow 500"), null));
        Bot.SentTo(tg).Count.Should().Be(afterFirst + 1, "новый апдейт обрабатывается");
    }

    [Fact]
    public async Task T67_Withdrawn_ReleasesCashHoldOfWithdrawPlans()
    {
        var (userId, tg) = await NewUserAsync();
        var plan = await NewPlanAsync(userId, PlanStatuses.Done);
        await using (var conn = new MySqlConnector.MySqlConnection(Conn))
        {
            await Dapper.SqlMapper.ExecuteAsync(conn, "UPDATE trade_plans SET rule = 'P5_WITHDRAW', summary_json = @J WHERE id = @Id",
                new { Id = plan.Id, J = """{"portfolio":"rub","currency":"RUB","cashHold":100000}""" });
        }

        await TextAsync(tg, "/withdrawn");

        (await ReloadAsync(plan)).CashHoldReleasedAt.Should().NotBeNull();
        Bot.SentTo(tg).Last().Html.Should().Contain("Удержание").And.Contain("снято");

        await TextAsync(tg, "/withdrawn");
        Bot.SentTo(tg).Last().Html.Should().Contain("нет");
    }

    [Fact]
    public async Task Plans_ListsOnlyOwnOpenPlans_WithButtons()
    {
        var (userId, tg) = await NewUserAsync();
        var (otherId, _) = await NewUserAsync();
        var mine = await NewPlanAsync(userId);
        var rejected = await NewPlanAsync(userId, PlanStatuses.Rejected);
        var theirs = await NewPlanAsync(otherId);

        await TextAsync(tg, "/plans");

        var message = Bot.SentTo(tg).Single();
        message.Html.Should().Contain($"№{mine.Id}").And.NotContain($"№{rejected.Id} ").And.NotContain($"№{theirs.Id} ");
        var data = message.Keyboard!.Rows.SelectMany(r => r).Select(b => b.CallbackData).ToList();
        data.Should().Contain(Approve(mine)).And.Contain(Reject(mine));
        data.Should().NotContain(d => d != null && d.Contains($":{theirs.Id}:"));
    }

    [Fact]
    public async Task Plans_WhenNone_SaysSo()
    {
        var (_, tg) = await NewUserAsync();
        await TextAsync(tg, "/plans");
        Bot.SentTo(tg).Single().Html.Should().Be("Открытых планов нет.");
    }

    [Fact]
    public async Task Status_WithoutBrokerAccounts_ReportsOpenPlansAndMode()
    {
        var (userId, tg) = await NewUserAsync();
        await NewPlanAsync(userId);

        await TextAsync(tg, "/status");

        var html = Bot.SentTo(tg).Single().Html;
        html.Should().Contain("Капитал").And.Contain("Открытых планов: 1").And.Contain("режим наблюдения: сделка не отправляется");
    }

    [Fact]
    public async Task Inflow_WithoutAccount_ExplainsWhy_AndCreatesNoPlan()
    {
        var (userId, tg) = await NewUserAsync();

        await TextAsync(tg, "/inflow 50000");

        Bot.SentTo(tg).Single().Html.Should().Contain("Не получилось построить план").And.Contain("Нет счёта T-Invest");
        (await new TradePlanRepository(Conn).ListAsync(userId, null, 10)).Should().BeEmpty();
    }

    [Fact]
    public async Task BadCommandArguments_GetHint()
    {
        var (_, tg) = await NewUserAsync();

        await TextAsync(tg, "/withdraw много");
        await TextAsync(tg, "привет");

        var messages = Bot.SentTo(tg).Select(m => m.Html).ToList();
        messages[0].Should().Contain("/withdraw");
        messages[1].Should().Contain("/help");
    }

    [Fact]
    public async Task Migration034_AddsNonceColumn_AndNoncePersistsOnPlan()
    {
        var (userId, _) = await NewUserAsync();
        var plan = await NewPlanAsync(userId);

        await using var conn = new MySqlConnection(Conn);
        (await conn.ExecuteScalarAsync<string>("SELECT bot_nonce FROM trade_plans WHERE id = @Id", new { plan.Id })).Should().Be(plan.BotNonce);
    }
}

using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Telegram;
using Bonds.Infrastructure.Telegram;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Bonds.Tests.Telegram;

/// <summary>Задача 67 (#16): сбой обработчика нажатия кнопки всё равно отвечает на callback («ошибка, попробуйте ещё раз») — «часики» не висят.</summary>
public class TelegramCallbackFailureTests
{
    [Fact]
    public async Task T67_CallbackHandlerFails_StillAnswersCallbackWithError()
    {
        var bot = new Mock<ITelegramBotClient>();
        var users = new Mock<IUserRepository>();
        users.Setup(u => u.GetByTelegramIdAsync(It.IsAny<long>())).ThrowsAsync(new InvalidOperationException("db down"));
        var handler = new TelegramBotUpdateHandler(
            bot.Object, users.Object, Mock.Of<ITradePlanRepository>(), Mock.Of<IUserSettingsRepository>(), null!, null!,
            Mock.Of<Bonds.Infrastructure.Execution.IExecutionGateResolver>(), Options.Create(new TelegramBotOptions()), TimeProvider.System,
            NullLogger<TelegramBotUpdateHandler>.Instance);

        await handler.HandleAsync(new BotUpdate(1, null, new BotIncomingCallback("cb-1", 42, 42, "private", 900, "p:1:a:nonce")));

        bot.Verify(b => b.AnswerCallbackQueryAsync("cb-1", TelegramBotUpdateHandler.CallbackFailedText, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    private static TelegramBotUpdateHandler Handler(Mock<ITelegramBotClient> bot, Mock<IUserRepository> users, IBotStateRepository? state = null) => new(
        bot.Object, users.Object, Mock.Of<ITradePlanRepository>(), Mock.Of<IUserSettingsRepository>(), null!, null!,
        Mock.Of<Bonds.Infrastructure.Execution.IExecutionGateResolver>(), Options.Create(new TelegramBotOptions()), TimeProvider.System,
        NullLogger<TelegramBotUpdateHandler>.Instance, state);

    [Fact]
    public async Task T67E_PlanCommandMarkedHandled_ThenReplyFails_SendsErrorReply()
    {
        // Minor 8: команда плана отмечена обработанной (повтор того же update_id план не строит), затем сбой (Telegram не принял ответ) — пользователь получает
        // отдельное сообщение об ошибке, а не тишину.
        var bot = new Mock<ITelegramBotClient>();
        bot.SetupSequence(b => b.SendMessageAsync(42, It.IsAny<string>(), It.IsAny<InlineKeyboard?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TelegramApiException("sendMessage", 502, "Bad Gateway"))
            .ReturnsAsync(1L);
        var users = new Mock<IUserRepository>();
        users.Setup(u => u.GetByTelegramIdAsync(42)).ReturnsAsync(new Bonds.Core.Models.User { Id = 5, TelegramId = 42 });
        var state = new Mock<IBotStateRepository>();
        state.Setup(s => s.TryAdvanceAsync(TelegramBotUpdateHandler.PlanCommandMarkKey(5), 77)).ReturnsAsync(true);

        await Handler(bot, users, state.Object).HandleAsync(new BotUpdate(77, new BotIncomingMessage(42, "private", 42, "T", "/inflow 1000"), null));

        state.Verify(s => s.TryAdvanceAsync(TelegramBotUpdateHandler.PlanCommandMarkKey(5), 77), Times.Once);
        bot.Verify(b => b.SendMessageAsync(42, TelegramBotUpdateHandler.MessageFailedText, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task T67E_MessageFailure_InGroupChat_NoReply_ErrorReplyFailure_DoesNotThrow()
    {
        var bot = new Mock<ITelegramBotClient>();
        bot.Setup(b => b.SendMessageAsync(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<InlineKeyboard?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("telegram down"));
        var users = new Mock<IUserRepository>();
        users.Setup(u => u.GetByTelegramIdAsync(It.IsAny<long>())).ThrowsAsync(new InvalidOperationException("db down"));

        var act = () => Handler(bot, users).HandleAsync(new BotUpdate(78, new BotIncomingMessage(42, "private", 42, "T", "/status"), null));
        await act.Should().NotThrowAsync();
        bot.Verify(b => b.SendMessageAsync(42, TelegramBotUpdateHandler.MessageFailedText, null, It.IsAny<CancellationToken>()), Times.Once);

        bot.Invocations.Clear();
        await Handler(bot, users).HandleAsync(new BotUpdate(79, new BotIncomingMessage(-100, "group", 42, "T", "/status"), null));
        bot.Verify(b => b.SendMessageAsync(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<InlineKeyboard?>(), It.IsAny<CancellationToken>()), Times.Never, "группы — молча");
    }

    [Fact]
    public async Task T67_ErrorAnswerItselfFails_DoesNotThrow()
    {
        var bot = new Mock<ITelegramBotClient>();
        bot.Setup(b => b.AnswerCallbackQueryAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("telegram down"));
        var users = new Mock<IUserRepository>();
        users.Setup(u => u.GetByTelegramIdAsync(It.IsAny<long>())).ThrowsAsync(new InvalidOperationException("db down"));
        var handler = new TelegramBotUpdateHandler(
            bot.Object, users.Object, Mock.Of<ITradePlanRepository>(), Mock.Of<IUserSettingsRepository>(), null!, null!,
            Mock.Of<Bonds.Infrastructure.Execution.IExecutionGateResolver>(), Options.Create(new TelegramBotOptions()), TimeProvider.System,
            NullLogger<TelegramBotUpdateHandler>.Instance);

        var act = () => handler.HandleAsync(new BotUpdate(2, null, new BotIncomingCallback("cb-2", 42, 42, "private", 900, "p:1:a:nonce")));

        await act.Should().NotThrowAsync();
    }
}

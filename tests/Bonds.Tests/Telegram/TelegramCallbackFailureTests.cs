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

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bonds.Core.Models;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Policy;
using Bonds.Core.Telegram;
using Bonds.Infrastructure.Telegram;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Bonds.Tests.Telegram;

public class TelegramSendLimitAndNotifierTests
{
    private const string Token = "123456:SECRET-TOKEN-VALUE";

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"ok":true,"result":{"message_id":1}}""", Encoding.UTF8, "application/json") };
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { BaseAddress = new Uri("https://api.telegram.org") };
    }

    private sealed class WarningLogger<T> : ILogger<T>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
    }

    private static (TelegramBotClient Client, CapturingHandler Handler, WarningLogger<TelegramBotClient> Logger) NewClient()
    {
        var handler = new CapturingHandler();
        var logger = new WarningLogger<TelegramBotClient>();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Telegram:BotToken"] = Token }).Build();
        return (new TelegramBotClient(new Factory(handler), config, logger), handler, logger);
    }

    [Theory]
    [InlineData("sendMessage")]
    [InlineData("editMessageText")]
    public async Task OversizedText_IsTruncatedTo4096_ClosesTags_AndLogsWarning(string method)
    {
        var (client, handler, logger) = NewClient();
        var html = "<b>" + string.Concat(Enumerable.Repeat("a&amp;b", 2000)) + "</b>";

        if (method == "sendMessage") await client.SendMessageAsync(1, html);
        else await client.EditMessageTextAsync(1, 2, html);

        using var doc = JsonDocument.Parse(handler.Bodies.Single());
        var text = doc.RootElement.GetProperty("text").GetString()!;
        text.Length.Should().BeLessThanOrEqualTo(4096);
        text.Should().StartWith("<b>").And.EndWith("…</b>");
        Regex.Matches(text, "&").Count.Should().Be(Regex.Matches(text, "&amp;").Count, "сущность не разорвана");
        logger.Warnings.Should().ContainSingle().Which.Should().Contain("truncated").And.NotContain(Token);
    }

    [Fact]
    public async Task TextAtLimit_IsNotModified_AndNotLogged()
    {
        var (client, handler, logger) = NewClient();

        await client.SendMessageAsync(1, new string('x', 4096));

        using var doc = JsonDocument.Parse(handler.Bodies.Single());
        doc.RootElement.GetProperty("text").GetString().Should().HaveLength(4096);
        logger.Warnings.Should().BeEmpty();
    }

    // ─── Уведомление о плане: ct передаётся, ожидание ограничено ──────────────────────────

    private static TradePlan Plan() => new()
    {
        Id = 7, UserId = 1, Rule = PlanRules.P1Sweep, Trigger = "t", Status = PlanStatuses.PendingApproval, BotNonce = "AbCdEfGh123",
        ExpiresAt = DateTime.UtcNow.AddMinutes(30),
    };

    private static TelegramPlanNotifier NewNotifier(ITelegramBotClient bot, TimeSpan timeout, ILogger<TelegramPlanNotifier>? logger = null)
    {
        var users = new Mock<IUserRepository>();
        users.Setup(u => u.GetByIdAsync(1)).ReturnsAsync(new User { Id = 1, TelegramId = 555 });
        return new TelegramPlanNotifier(
            bot, users.Object, Options.Create(new TelegramBotOptions { NotifyTimeout = timeout }), TimeProvider.System, logger ?? NullLogger<TelegramPlanNotifier>.Instance);
    }

    [Fact]
    public async Task Notifier_HungTelegram_IsCutByTimeout_WithoutThrowing()
    {
        var bot = new Mock<ITelegramBotClient>();
        bot.SetupGet(b => b.IsConfigured).Returns(true);
        bot.Setup(b => b.SendMessageAsync(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<InlineKeyboard?>(), It.IsAny<CancellationToken>()))
            .Returns<long, string, InlineKeyboard?, CancellationToken>(async (_, _, _, token) =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return null;
            });
        var logger = new WarningLogger<TelegramPlanNotifier>();

        var started = DateTime.UtcNow;
        await NewNotifier(bot.Object, TimeSpan.FromMilliseconds(100), logger).NotifyPlanCreatedAsync(Plan());

        (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(3));
        logger.Warnings.Should().ContainSingle().Which.Should().Contain("failed");
    }

    [Fact]
    public async Task Notifier_CallerCancellation_Propagates_AndTokenReachesClient()
    {
        using var cts = new CancellationTokenSource();
        var bot = new Mock<ITelegramBotClient>();
        bot.SetupGet(b => b.IsConfigured).Returns(true);
        bot.Setup(b => b.SendMessageAsync(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<InlineKeyboard?>(), It.IsAny<CancellationToken>()))
            .Returns<long, string, InlineKeyboard?, CancellationToken>(async (_, _, _, token) =>
            {
                cts.Cancel();
                await Task.Delay(Timeout.Infinite, token);
                return null;
            });

        var act = () => NewNotifier(bot.Object, TimeSpan.FromSeconds(30)).NotifyPlanCreatedAsync(Plan(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Notifier_Success_SendsOnce()
    {
        var bot = new Mock<ITelegramBotClient>();
        bot.SetupGet(b => b.IsConfigured).Returns(true);

        await NewNotifier(bot.Object, TimeSpan.FromSeconds(5)).NotifyPlanCreatedAsync(Plan());

        bot.Verify(b => b.SendMessageAsync(555, It.IsAny<string>(), It.IsAny<InlineKeyboard?>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}

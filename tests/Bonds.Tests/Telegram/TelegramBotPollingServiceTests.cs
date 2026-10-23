using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Telegram;
using Bonds.Infrastructure.Scheduling;
using Bonds.Infrastructure.Telegram;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bonds.Tests.Telegram;

public class TelegramBotPollingServiceTests
{
    private sealed class FakeBot : ITelegramBotClient
    {
        public bool IsConfigured { get; set; } = true;
        public Queue<Func<GetUpdatesResult>> Responses { get; } = new();
        public List<long> RequestedOffsets { get; } = [];

        public Task<GetUpdatesResult> GetUpdatesAsync(long offset, int timeoutSeconds, CancellationToken ct = default)
        {
            RequestedOffsets.Add(offset);
            return Task.FromResult(Responses.Count > 0 ? Responses.Dequeue()() : new GetUpdatesResult([], false));
        }

        public Task<long?> SendMessageAsync(long chatId, string html, InlineKeyboard? keyboard = null, CancellationToken ct = default) => Task.FromResult<long?>(1);
        public Task EditMessageTextAsync(long chatId, long messageId, string html, InlineKeyboard? keyboard = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task AnswerCallbackQueryAsync(string callbackQueryId, string? text = null, bool showAlert = false, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class MemoryState : IBotStateRepository
    {
        public Dictionary<string, string> Values { get; } = [];
        public Task<string?> GetAsync(string key) => Task.FromResult(Values.TryGetValue(key, out var v) ? v : null);
        public Task SetAsync(string key, string value)
        {
            Values[key] = value;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingHandler : ITelegramUpdateHandler
    {
        public List<long> Handled { get; } = [];

        public Task HandleAsync(BotUpdate update, CancellationToken ct = default)
        {
            Handled.Add(update.UpdateId);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingAlerts : ITelegramAlertSender
    {
        public List<string> Messages { get; } = [];
        public Task SendAsync(string message, CancellationToken ct = default)
        {
            Messages.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Harness
    {
        public FakeBot Bot { get; } = new();
        public MemoryState State { get; } = new();
        public RecordingHandler Handler { get; } = new();
        public RecordingAlerts Alerts { get; } = new();
        public FakeTime Time { get; } = new(new DateTimeOffset(2026, 10, 21, 9, 0, 0, TimeSpan.Zero));
        public TelegramBotOptions Options { get; } = new();

        public TelegramBotPollingService NewService()
        {
            var services = new ServiceCollection();
            services.AddSingleton<IBotStateRepository>(State);
            services.AddSingleton<ITelegramUpdateHandler>(Handler);
            var provider = services.BuildServiceProvider();
            return new TelegramBotPollingService(
                provider.GetRequiredService<IServiceScopeFactory>(), Bot, Alerts, Options.AsOptions(), Time, NullLogger<TelegramBotPollingService>.Instance);
        }
    }

    private static BotUpdate Upd(long id) => new(id, new BotIncomingMessage(1, "private", 1, null, "/help"), null);

    [Fact]
    public async Task HandlesUpdatesInOrder_AndPersistsOffsetAfterEachOne()
    {
        var h = new Harness();
        h.Bot.Responses.Enqueue(() => new GetUpdatesResult([Upd(102), Upd(100), Upd(101)], false));
        var service = h.NewService();

        var delay = await service.PollOnceAsync(CancellationToken.None);

        delay.Should().Be(TimeSpan.Zero, "long polling уже подождал — следующий запрос сразу");
        h.Handler.Handled.Should().Equal(100, 101, 102);
        h.State.Values[TelegramBotPollingService.OffsetKey].Should().Be("103");

        await service.PollOnceAsync(CancellationToken.None);
        h.Bot.RequestedOffsets.Should().Equal(0, 103);
    }

    [Fact]
    public async Task AfterRestart_StartsFromStoredOffset_AndDoesNotReprocess()
    {
        var h = new Harness();
        h.State.Values[TelegramBotPollingService.OffsetKey] = "500";

        await h.NewService().PollOnceAsync(CancellationToken.None);

        h.Bot.RequestedOffsets.Should().Equal(500);
        h.Handler.Handled.Should().BeEmpty();
    }

    [Fact]
    public async Task Conflict409_BacksOff_AndAlertsOnlyOncePerCooldown()
    {
        var h = new Harness();
        h.Options.ConflictBackoff = TimeSpan.FromSeconds(60);
        h.Options.ConflictAlertCooldown = TimeSpan.FromHours(6);
        for (var i = 0; i < 3; i++) h.Bot.Responses.Enqueue(() => new GetUpdatesResult([], Conflict: true));
        var service = h.NewService();

        (await service.PollOnceAsync(CancellationToken.None)).Should().Be(TimeSpan.FromSeconds(60));
        h.Time.Now = h.Time.Now.AddMinutes(1);
        (await service.PollOnceAsync(CancellationToken.None)).Should().Be(TimeSpan.FromSeconds(60));

        h.Alerts.Messages.Should().ContainSingle().Which.Should().Contain("409").And.Contain("другой потребитель");

        h.Time.Now = h.Time.Now.AddHours(7);
        await service.PollOnceAsync(CancellationToken.None);
        h.Alerts.Messages.Should().HaveCount(2, "после паузы cooldown алерт возможен снова");
        h.Handler.Handled.Should().BeEmpty();
    }

    [Fact]
    public async Task NotConfigured_Idles_WithoutCallingTelegram()
    {
        var h = new Harness();
        h.Bot.IsConfigured = false;
        h.Options.IdleDelay = TimeSpan.FromMinutes(3);

        var delay = await h.NewService().PollOnceAsync(CancellationToken.None);

        delay.Should().Be(TimeSpan.FromMinutes(3));
        h.Bot.RequestedOffsets.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_SurvivesFailures_AndStopsOnCancellation()
    {
        var h = new Harness();
        h.Options.ErrorDelay = TimeSpan.FromMilliseconds(5);
        h.Bot.Responses.Enqueue(() => throw new HttpRequestException("boom"));
        h.Bot.Responses.Enqueue(() => new GetUpdatesResult([Upd(7)], false));
        var service = h.NewService();

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (h.Handler.Handled.Count == 0 && DateTime.UtcNow < deadline) await Task.Delay(10);
        await service.StopAsync(CancellationToken.None);

        h.Handler.Handled.Should().Equal(7);
        h.State.Values[TelegramBotPollingService.OffsetKey].Should().Be("8");
    }
}

internal static class OptionsExtensions
{
    public static IOptions<T> AsOptions<T>(this T value) where T : class => Options.Create(value);
}

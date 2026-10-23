using System.Net;
using System.Text;
using Bonds.Infrastructure;
using Bonds.Infrastructure.Telegram;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Bonds.Tests.Telegram;

/// <summary>
/// Регресс: токен бота — часть пути запроса, а стандартный логгер HttpClient печатает путь на Information. Оба именованных клиента
/// (<c>telegram-bot</c> и <c>telegram-bot-poll</c>) обязаны быть приглушены до Warning, иначе токен утечёт в логи.
/// </summary>
public class TelegramLoggingFilterTests
{
    private const string Token = "123456:SECRET-TOKEN-VALUE";

    private sealed class CaptureProvider : ILoggerProvider
    {
        public List<(string Category, LogLevel Level, string Message)> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new CaptureLogger(categoryName, Entries);

        public void Dispose()
        {
        }

        private sealed class CaptureLogger(string category, List<(string, LogLevel, string)> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (sink) sink.Add((category, logLevel, formatter(state, exception)));
            }
        }
    }

    private sealed class OkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true,\"result\":[]}", Encoding.UTF8, "application/json") });
    }

    private static ServiceProvider Build(CaptureProvider capture)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = "Server=localhost;Database=x;User=x;Password=x;",
            ["Telegram:BotToken"] = Token,
        }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddLogging(b => b.AddProvider(capture));
        services.AddInfrastructure(config);
        // Сети нет: обработчики клиентов заменяются заглушкой, остальная цепочка (в т.ч. логирующий делегат) — настоящая.
        services.AddHttpClient(TelegramBotClient.PollHttpClientName).ConfigurePrimaryHttpMessageHandler(() => new OkHandler());
        services.AddHttpClient(Bonds.Infrastructure.Scheduling.TelegramAlertSender.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new OkHandler());
        return services.BuildServiceProvider();
    }

    [Theory]
    [InlineData("telegram-bot")]
    [InlineData("telegram-bot-poll")]
    public void HttpClientLogCategories_ForBothTelegramClients_AreWarningOnly(string clientName)
    {
        using var sp = Build(new CaptureProvider());
        var factory = sp.GetRequiredService<ILoggerFactory>();

        foreach (var suffix in new[] { "", ".LogicalHandler", ".ClientHandler" })
        {
            var logger = factory.CreateLogger($"System.Net.Http.HttpClient.{clientName}{suffix}");
            logger.IsEnabled(LogLevel.Information).Should().BeFalse($"{clientName}{suffix}: путь запроса содержит токен");
            logger.IsEnabled(LogLevel.Warning).Should().BeTrue();
        }

        // Контроль: другие клиенты и обычные категории не приглушены.
        factory.CreateLogger("System.Net.Http.HttpClient.moex.ClientHandler").IsEnabled(LogLevel.Information).Should().BeTrue();
        factory.CreateLogger("Bonds.Infrastructure.Telegram.TelegramBotClient").IsEnabled(LogLevel.Information).Should().BeTrue();
    }

    [Theory]
    [InlineData("telegram-bot")]
    [InlineData("telegram-bot-poll")]
    public async Task RealRequestThroughNamedClient_NeverLogsTheTokenPath(string clientName)
    {
        var capture = new CaptureProvider();
        using var sp = Build(capture);

        var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient(clientName);
        (await http.PostAsync($"/bot{Token}/getUpdates", new StringContent("{}"))).EnsureSuccessStatusCode();

        capture.Entries.Where(e => e.Category.StartsWith("System.Net.Http.HttpClient.telegram-bot", StringComparison.Ordinal))
            .Should().NotContain(e => e.Level < LogLevel.Warning);
        capture.Entries.Should().NotContain(e => e.Message.Contains(Token));
    }
}

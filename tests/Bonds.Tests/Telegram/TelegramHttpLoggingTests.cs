using System.Collections.Concurrent;
using System.Net;
using Bonds.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Bonds.Tests.Telegram;

/// <summary>
/// Задача 67 (#15): токен Telegram — часть пути запроса (<c>/bot&lt;token&gt;/…</c>). Защита не должна зависеть от конфигурации логирования: даже правило
/// провайдера «Default = Information» (как <c>Logging:Console:LogLevel:Default=Information</c>, оно сильнее любых общих правил по категориям) не выводит токен.
/// </summary>
public class TelegramHttpLoggingTests
{
    private const string Token = "123456789:AAH-SECRET-TOKEN-VALUE";

    [Theory]
    [InlineData("telegram-bot")]
    [InlineData("telegram-bot-poll")]
    public async Task T67_TelegramClients_DoNotLogToken_EvenWithProviderRuleDefaultInformation(string name)
    {
        var (factory, sink) = Build(name);

        await factory.CreateClient(name).GetAsync($"/bot{Token}/getMe");
        await factory.CreateClient("control").GetAsync($"https://example.test/bot{Token}/getMe");

        sink.Messages.Should().Contain(m => m.Contains("example.test"), "контроль: правило провайдера действительно включает логи HttpClient");
        sink.Messages.Where(m => !m.Contains("example.test")).Should().NotContain(m => m.Contains("SECRET"), "клиенты Telegram токен не логируют");
        sink.Messages.Should().NotContain(m => m.Contains("api.telegram.org") && m.Contains("SECRET"));
    }

    private static (IHttpClientFactory Factory, CapturingProvider Sink) Build(string name)
    {
        var sink = new CapturingProvider();
        var services = new ServiceCollection();
        services.AddLogging(b =>
        {
            b.ClearProviders();
            b.AddProvider(sink);
            b.SetMinimumLevel(LogLevel.Warning);
            b.AddFilter("System.Net.Http.HttpClient.telegram-bot", LogLevel.Warning); // прежняя защита — общее правило по категории
            b.AddFilter<CapturingProvider>(null, LogLevel.Information); // = Logging:<Provider>:LogLevel:Default = Information
        });
        DependencyInjection.AddTelegramHttpClients(services);
        services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => new OkHandler());
        services.AddHttpClient("control").ConfigurePrimaryHttpMessageHandler(() => new OkHandler());
        return (services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>(), sink);
    }

    private sealed class OkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true}") });
    }

    private sealed class CapturingProvider : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

        public void Dispose()
        {
        }

        private sealed class Logger(CapturingProvider owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                owner.Messages.Enqueue(category + ": " + formatter(state, exception));
        }
    }
}

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Bonds.IntegrationTests.Infrastructure;

/// <summary>
/// WebApplicationFactory с подключённой Testcontainers MySQL-базой.
/// Паттерн скопирован из cashpulse. Jwt:Secret/Issuer/Audience подменяются на значения
/// из JwtTestHelper, чтобы тестовые токены проверялись тем же секретом, что и в Program.cs.
/// </summary>
public class TestWebApplicationFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    /// <summary>Telegram id владельца, используемый в тестовой конфигурации Telegram:OwnerId.</summary>
    public const long TestOwnerTelegramId = 123456789;

    private readonly DatabaseFixture _dbFixture = new();

    public DatabaseFixture Database => _dbFixture;

    public async Task InitializeAsync()
    {
        await _dbFixture.InitializeAsync();
    }

    public new async Task DisposeAsync()
    {
        await _dbFixture.DisposeAsync();
        await base.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Testing-окружение: Program.cs пропускает MigrationRunner на старте,
        // т.к. DatabaseFixture уже применила миграции к контейнеру.
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = _dbFixture.ConnectionString,
                ["Jwt:Secret"] = JwtTestHelper.TestSecret,
                ["Jwt:Issuer"] = JwtTestHelper.TestIssuer,
                ["Jwt:Audience"] = JwtTestHelper.TestAudience,
                ["Telegram:BotToken"] = "test-bot-token-not-real",
                ["Telegram:BotUsername"] = "test_bot",
                ["Telegram:OwnerId"] = TestOwnerTelegramId.ToString(),
            });
        });

        builder.ConfigureServices(services =>
        {
            // Убираем все hosted-сервисы, чтобы фоновые задачи (синк, шедулер) не мешали тестам
            var hostedServiceDescriptors = services
                .Where(d => d.ServiceType == typeof(IHostedService))
                .ToList();

            foreach (var descriptor in hostedServiceDescriptors)
                services.Remove(descriptor);
        });
    }
}

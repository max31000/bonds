using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Bonds.IntegrationTests.Infrastructure;

/// <summary>
/// WebApplicationFactory с подключённой Testcontainers MySQL-базой.
/// Паттерн скопирован из cashpulse; JWT-конфиг добавится в этапе 02.
/// </summary>
public class TestWebApplicationFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
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

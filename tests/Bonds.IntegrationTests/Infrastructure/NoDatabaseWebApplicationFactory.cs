using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Bonds.IntegrationTests.Infrastructure;

/// <summary>
/// The API host without MySQL (no Testcontainers, no Docker): enough for everything that is decided before an
/// endpoint touches the database — authentication, the owner check, the Origin filter, anonymous /health.
/// Unlike <see cref="TestWebApplicationFactory"/> it keeps the production <c>IOwnerAccess</c>
/// (<c>Telegram:OwnerId</c> = <see cref="OwnerTelegramId"/>).
/// </summary>
public class NoDatabaseWebApplicationFactory : WebApplicationFactory<Program>
{
    public const long OwnerTelegramId = 123456789;

    private readonly string _dataProtectionKeysPath =
        Path.Combine(Path.GetTempPath(), $"bonds-test-dp-keys-{Guid.NewGuid():N}");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Never opened by these tests (they stop at auth); unroutable on purpose.
                ["ConnectionStrings:DefaultConnection"] = "Server=127.0.0.1;Port=1;Database=none;User=none;Password=none;",
                ["Telegram:BotToken"] = "test-bot-token-not-real",
                ["Telegram:OwnerId"] = OwnerTelegramId.ToString(),
                ["DataProtection:KeysPath"] = _dataProtectionKeysPath,
            });
        });

        builder.ConfigureServices(services =>
        {
            foreach (var hosted in services.Where(d => d.ServiceType == typeof(IHostedService)).ToList())
                services.Remove(hosted);

            Mvv42TestAuth.UseTestSigningKey(services);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(_dataProtectionKeysPath))
            Directory.Delete(_dataProtectionKeysPath, recursive: true);
    }
}

[CollectionDefinition("NoDatabase")]
public class NoDatabaseCollection : ICollectionFixture<NoDatabaseWebApplicationFactory> { }

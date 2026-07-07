using Bonds.Infrastructure.Connectors.TInvest;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Moq;
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
    private readonly string _dataProtectionKeysPath = Path.Combine(Path.GetTempPath(), $"bonds-test-dp-keys-{Guid.NewGuid():N}");

    public DatabaseFixture Database => _dbFixture;

    public async Task InitializeAsync()
    {
        await _dbFixture.InitializeAsync();
    }

    public new async Task DisposeAsync()
    {
        await _dbFixture.DisposeAsync();
        await base.DisposeAsync();

        if (Directory.Exists(_dataProtectionKeysPath))
        {
            Directory.Delete(_dataProtectionKeysPath, recursive: true);
        }
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
                // Дефолт /app/dataprotection-keys (plan/13 часть A) не существует/не доступен на
                // тестовом хосте — свой temp dir на процесс теста, чтобы PUT /api/settings/tinvest-token
                // (шифрует токен через IDataProtectionProvider) не падал 500 в тестах.
                ["DataProtection:KeysPath"] = _dataProtectionKeysPath,
                // Задача 30: RelativeValueSnapshotBuilder — singleton с in-memory кэшем ~1 час
                // (план часть B.3). TestWebApplicationFactory общий на всю коллекцию "Integration"
                // (IntegrationCollectionFixture) — без отключения кэша здесь тест, засеявший новые
                // данные ПОСЛЕ первого запроса другого теста в той же коллекции, получал бы стухший
                // снимок вместо своих собственных данных. 00:00:00 — кэш всегда считается протухшим.
                ["BondUniverse:RelativeValueCacheDuration"] = "00:00:00",
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

            // ITInvestTokenValidator реально ходит в T-Invest по gRPC (plan/13 часть C) — в тестах
            // нет сети/токена, поэтому дефолт — мок, всегда считающий токен валидным (happy path
            // большинства тестов PUT /api/settings/tinvest-token). Тесты на конкретно невалидный
            // токен переопределяют этот мок через factory.WithWebHostBuilder(...).
            services.RemoveAll<ITInvestTokenValidator>();
            var defaultValidator = new Mock<ITInvestTokenValidator>();
            defaultValidator
                .Setup(v => v.ValidateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(TInvestTokenValidationResult.Valid("TEST-BROKER-ACCOUNT-1234"));
            services.AddScoped(_ => defaultValidator.Object);
        });
    }
}

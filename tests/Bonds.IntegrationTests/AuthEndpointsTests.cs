using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bonds.Core.Interfaces.Repositories;
using Bonds.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Bonds.IntegrationTests;

[Collection("Integration")]
public class AuthEndpointsTests
{
    // Должен совпадать с TestWebApplicationFactory.ConfigureWebHost (Telegram:BotToken)
    private const string TestBotToken = "test-bot-token-not-real";

    private static readonly byte[] SecretKey = SHA256.HashData(Encoding.UTF8.GetBytes(TestBotToken));

    private readonly TestWebApplicationFactory _factory;

    public AuthEndpointsTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private static string ComputeHash(long id, long authDate, string? firstName = null, string? username = null)
    {
        var fields = new SortedDictionary<string, string>
        {
            ["id"] = id.ToString(),
            ["auth_date"] = authDate.ToString(),
        };
        if (!string.IsNullOrEmpty(firstName)) fields["first_name"] = firstName;
        if (!string.IsNullOrEmpty(username)) fields["username"] = username;

        var dataCheckString = string.Join("\n", fields.Select(kv => $"{kv.Key}={kv.Value}"));
        return Convert.ToHexString(HMACSHA256.HashData(SecretKey, Encoding.UTF8.GetBytes(dataCheckString)))
            .ToLowerInvariant();
    }

    private static long FreshAuthDate() => DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 5;

    [Fact]
    public async Task TelegramLogin_InvalidSignature_Returns401()
    {
        var client = _factory.CreateClient();

        var payload = new
        {
            id = TestWebApplicationFactory.TestOwnerTelegramId,
            firstName = "Owner",
            authDate = FreshAuthDate(),
            hash = "0000000000000000000000000000000000000000000000000000000000000000",
        };

        var response = await client.PostAsJsonAsync("/api/auth/telegram", payload);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TelegramLogin_ValidSignatureButNotOwner_Returns403()
    {
        var client = _factory.CreateClient();

        const long impostorId = 999999999;
        var authDate = FreshAuthDate();
        var hash = ComputeHash(impostorId, authDate, firstName: "Impostor");

        var payload = new
        {
            id = impostorId,
            firstName = "Impostor",
            authDate,
            hash,
        };

        var response = await client.PostAsJsonAsync("/api/auth/telegram", payload);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task TelegramLogin_ValidOwnerSignature_Returns200WithToken()
    {
        var client = _factory.CreateClient();

        var ownerId = TestWebApplicationFactory.TestOwnerTelegramId;
        var authDate = FreshAuthDate();
        var hash = ComputeHash(ownerId, authDate, firstName: "Owner", username: "owner_user");

        var payload = new
        {
            id = ownerId,
            firstName = "Owner",
            username = "owner_user",
            authDate,
            hash,
        };

        var response = await client.PostAsJsonAsync("/api/auth/telegram", payload);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("token").GetString().Should().NotBeNullOrEmpty();
        body.GetProperty("user").GetProperty("telegramId").GetInt64().Should().Be(ownerId);
    }

    [Fact]
    public async Task TelegramLogin_FirstLoginForOwner_ProvisionsAccount()
    {
        // Single-user продукт: sync/positions/cashflow/... все резолвят "единственный счёт"
        // через IAccountRepository.GetPrimaryAccountIdAsync — без авто-провижининга счёта на
        // первом логине цикл синка молча ничего не находит (см. SyncCycleService.NoAccountConfigured).
        var client = _factory.CreateClient();

        var ownerId = TestWebApplicationFactory.TestOwnerTelegramId;
        var authDate = FreshAuthDate();
        var hash = ComputeHash(ownerId, authDate, firstName: "Owner");

        var response = await client.PostAsJsonAsync("/api/auth/telegram", new
        {
            id = ownerId,
            firstName = "Owner",
            authDate,
            hash,
        });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = _factory.Services.CreateScope();
        var accountRepo = scope.ServiceProvider.GetRequiredService<IAccountRepository>();
        var accountId = await accountRepo.GetPrimaryAccountIdAsync();

        accountId.Should().NotBeNull();
    }

    [Fact]
    public async Task Me_WithoutToken_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/auth/me");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Me_WithValidOwnerToken_Returns200()
    {
        // Сначала логинимся, чтобы получить реальный токен и гарантированно
        // существующую запись пользователя в БД.
        var client = _factory.CreateClient();

        var ownerId = TestWebApplicationFactory.TestOwnerTelegramId;
        var authDate = FreshAuthDate();
        var hash = ComputeHash(ownerId, authDate, firstName: "Owner");

        var loginResponse = await client.PostAsJsonAsync("/api/auth/telegram", new
        {
            id = ownerId,
            firstName = "Owner",
            authDate,
            hash,
        });
        loginResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var loginBody = await loginResponse.Content.ReadFromJsonAsync<JsonElement>();
        var token = loginBody.GetProperty("token").GetString();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync("/api/auth/me");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("telegramId").GetInt64().Should().Be(ownerId);
    }

    [Fact]
    public async Task Me_WithTokenForUnknownUserId_Returns401()
    {
        var client = _factory.CreateClient();
        var token = JwtTestHelper.GenerateToken(userId: 999_999);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync("/api/auth/me");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ProtectedEndpoint_WithExpiredToken_Returns401()
    {
        var client = _factory.CreateClient();
        var token = JwtTestHelper.GenerateToken(userId: 1, expiry: TimeSpan.FromSeconds(-60));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync("/api/auth/me");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Health_DoesNotRequireAuthorization()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}

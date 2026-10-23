using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bonds.Core.Credentials;
using Bonds.Infrastructure.Repositories;
using Bonds.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Bonds.IntegrationTests;

/// <summary>
/// Задача 58: «Подключения» (GET/PUT/DELETE /api/credentials, GET /api/capabilities). Ключевой инвариант —
/// секретные значения не появляются в теле ни одного ответа ни одного эндпоинта.
/// </summary>
[Collection("Integration")]
public class CredentialsEndpointsTests
{
    private const string ClaudeToken = "sk-ant-oat01-SuperSecretClaudeTokenValue-Q7Z9";

    private readonly TestWebApplicationFactory _factory;

    public CredentialsEndpointsTests(TestWebApplicationFactory factory) => _factory = factory;

    private async Task<(HttpClient Client, ulong UserId)> CreateClientAsync()
    {
        var userRepo = new UserRepository(_factory.Database.ConnectionString);
        var telegramId = Random.Shared.NextInt64(1, long.MaxValue);
        var userId = await userRepo.CreateAsync(new Bonds.Core.Models.User { TelegramId = telegramId });
        var client = _factory.CreateClient();
        Mvv42TestAuth.Authorize(client, telegramId);
        return (client, userId);
    }

    private static JsonElement Row(JsonElement list, string provider) =>
        list.EnumerateArray().Single(e => e.GetProperty("provider").GetString() == provider);

    private static async Task<string> RawAsync(HttpResponseMessage r) => await r.Content.ReadAsStringAsync();

    [Fact]
    public async Task Get_Fresh_ReturnsEveryRegistryProvider_AsMissing()
    {
        var (client, _) = await CreateClientAsync();

        var response = await client.GetAsync("/api/credentials");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var list = await response.Content.ReadFromJsonAsync<JsonElement>();
        list.GetArrayLength().Should().Be(CredentialProviders.All.Count);
        foreach (var p in CredentialProviders.All)
        {
            var row = Row(list, p.Id);
            row.GetProperty("status").GetString().Should().Be("missing");
            row.GetProperty("title").GetString().Should().Be(p.Title);
            row.GetProperty("unlocks").GetString().Should().NotBeNullOrEmpty();
            row.GetProperty("fields").GetArrayLength().Should().Be(p.Fields.Count);
        }

        Row(list, "claude_code").GetProperty("category").GetString().Should().Be("ai");
        Row(list, "tinvest_read").GetProperty("category").GetString().Should().Be("broker");
        Row(list, "tinvest_trade").GetProperty("experimental").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Put_ValidClaudeToken_SavesAndReturnsRow_WithoutSecretAnywhere()
    {
        var (client, _) = await CreateClientAsync();

        var put = await client.PutAsJsonAsync("/api/credentials/claude_code", new { fields = new { oauthToken = "  " + ClaudeToken + "  " } });

        put.StatusCode.Should().Be(HttpStatusCode.OK);
        var putRaw = await RawAsync(put);
        putRaw.Should().NotContain(ClaudeToken);
        var putRow = JsonDocument.Parse(putRaw).RootElement;
        putRow.GetProperty("provider").GetString().Should().Be("claude_code");
        putRow.GetProperty("status").GetString().Should().Be("ok");
        putRow.GetProperty("last4").GetString().Should().Be("Q7Z9");
        putRow.GetProperty("verifiedAt").GetDateTime().Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(2));

        var get = await client.GetAsync("/api/credentials");
        var getRaw = await RawAsync(get);
        getRaw.Should().NotContain(ClaudeToken);
        getRaw.Should().NotContain("SuperSecretClaudeTokenValue");
        var list = JsonDocument.Parse(getRaw).RootElement;
        Row(list, "claude_code").GetProperty("status").GetString().Should().Be("ok");
        Row(list, "claude_code").GetProperty("last4").GetString().Should().Be("Q7Z9");
        Row(list, "tinvest_read").GetProperty("status").GetString().Should().Be("missing");

        var caps = await client.GetAsync("/api/capabilities");
        (await RawAsync(caps)).Should().NotContain(ClaudeToken);
    }

    [Fact]
    public async Task Put_RejectedByValidator_Returns422_AndSavesNothing_AndDoesNotEchoSecret()
    {
        var (client, _) = await CreateClientAsync();
        const string bad = "sk-ant-api03-ThisIsAnApiKeyNotASubscriptionToken";

        var put = await client.PutAsJsonAsync("/api/credentials/claude_code", new { fields = new { oauthToken = bad } });

        put.StatusCode.Should().Be((HttpStatusCode)422);
        var raw = await RawAsync(put);
        raw.Should().NotContain(bad);
        var body = JsonDocument.Parse(raw).RootElement;
        body.GetProperty("type").GetString().Should().Be("ValidationException");
        body.GetProperty("error").GetString().Should().Contain("claude setup-token");

        var list = await (await client.GetAsync("/api/credentials")).Content.ReadFromJsonAsync<JsonElement>();
        Row(list, "claude_code").GetProperty("status").GetString().Should().Be("missing", "невалидный секрет не сохраняется");
    }

    [Fact]
    public async Task Put_MissingRequiredField_Returns422()
    {
        var (client, _) = await CreateClientAsync();

        (await client.PutAsJsonAsync("/api/credentials/claude_code", new { fields = new { } })).StatusCode.Should().Be((HttpStatusCode)422);
        (await client.PutAsJsonAsync("/api/credentials/claude_code", new { fields = new { oauthToken = "   " } })).StatusCode.Should().Be((HttpStatusCode)422);
        (await client.PutAsJsonAsync("/api/credentials/claude_code", new { })).StatusCode.Should().Be((HttpStatusCode)422);
    }

    [Fact]
    public async Task PutAndDelete_UnknownProvider_Returns404()
    {
        var (client, _) = await CreateClientAsync();

        (await client.PutAsJsonAsync("/api/credentials/nope", new { fields = new { token = "x" } })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.DeleteAsync("/api/credentials/nope")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_RemovesCredential_Returns204_AndCapabilityDrops()
    {
        var (client, _) = await CreateClientAsync();
        await client.PutAsJsonAsync("/api/credentials/claude_code", new { fields = new { oauthToken = ClaudeToken } });

        var del = await client.DeleteAsync("/api/credentials/claude_code");

        del.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var list = await (await client.GetAsync("/api/credentials")).Content.ReadFromJsonAsync<JsonElement>();
        Row(list, "claude_code").GetProperty("status").GetString().Should().Be("missing");
        var caps = await (await client.GetAsync("/api/capabilities")).Content.ReadFromJsonAsync<JsonElement>();
        caps.GetProperty("aiAgents").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Capabilities_ReflectOkProviders_AiAgentsFromAnyAiProvider()
    {
        var (client, _) = await CreateClientAsync();

        var empty = await (await client.GetAsync("/api/capabilities")).Content.ReadFromJsonAsync<JsonElement>();
        foreach (var key in new[] { "tinvestRead", "tinvestTrade", "freedom", "aiAgents" })
        {
            empty.GetProperty(key).GetBoolean().Should().BeFalse(key);
        }

        await client.PutAsJsonAsync("/api/credentials/claude_code", new { fields = new { oauthToken = ClaudeToken } });
        await client.PutAsJsonAsync("/api/credentials/tinvest_read", new { fields = new { token = "t.ReadOnlyTokenForCaps" } });

        var caps = await (await client.GetAsync("/api/capabilities")).Content.ReadFromJsonAsync<JsonElement>();
        caps.GetProperty("aiAgents").GetBoolean().Should().BeTrue();
        caps.GetProperty("tinvestRead").GetBoolean().Should().BeTrue();
        caps.GetProperty("tinvestTrade").GetBoolean().Should().BeFalse();
        caps.GetProperty("freedom").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task TwoUsers_AreIsolated()
    {
        var (clientA, _) = await CreateClientAsync();
        var (clientB, _) = await CreateClientAsync();
        await clientA.PutAsJsonAsync("/api/credentials/claude_code", new { fields = new { oauthToken = ClaudeToken } });

        var listB = await (await clientB.GetAsync("/api/credentials")).Content.ReadFromJsonAsync<JsonElement>();
        Row(listB, "claude_code").GetProperty("status").GetString().Should().Be("missing");
        var capsB = await (await clientB.GetAsync("/api/capabilities")).Content.ReadFromJsonAsync<JsonElement>();
        capsB.GetProperty("aiAgents").GetBoolean().Should().BeFalse();

        // Удаление у B не трогает A.
        (await clientB.DeleteAsync("/api/credentials/claude_code")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var listA = await (await clientA.GetAsync("/api/credentials")).Content.ReadFromJsonAsync<JsonElement>();
        Row(listA, "claude_code").GetProperty("status").GetString().Should().Be("ok");
    }

    [Fact]
    public async Task LegacyPutTInvestToken_IsVisibleInCredentials_AndInSettings_WithoutSecret()
    {
        var (client, _) = await CreateClientAsync();
        const string raw = "t.LegacyFlowSecretToken9876";

        var put = await client.PutAsJsonAsync("/api/settings/tinvest-token", new { token = raw });
        put.StatusCode.Should().Be(HttpStatusCode.OK);
        (await RawAsync(put)).Should().NotContain(raw);

        var credsRaw = await RawAsync(await client.GetAsync("/api/credentials"));
        credsRaw.Should().NotContain(raw);
        var row = Row(JsonDocument.Parse(credsRaw).RootElement, "tinvest_read");
        row.GetProperty("status").GetString().Should().Be("ok");
        row.GetProperty("last4").GetString().Should().Be("9876");
        row.GetProperty("meta").GetProperty("accountIdLast4").GetString().Should().Be("1234");

        var settingsRaw = await RawAsync(await client.GetAsync("/api/settings"));
        settingsRaw.Should().NotContain(raw);
        var settings = JsonDocument.Parse(settingsRaw).RootElement;
        settings.GetProperty("tInvestTokenConfigured").GetBoolean().Should().BeTrue();
        settings.GetProperty("tInvestTokenMasked").GetString().Should().EndWith("9876");
    }

    [Fact]
    public async Task NewCredentialPutForTInvestRead_IsVisibleInSettings_AndDeleteClearsIt()
    {
        var (client, _) = await CreateClientAsync();
        const string raw = "t.NewFlowSecretToken5555";

        (await client.PutAsJsonAsync("/api/credentials/tinvest_read", new { fields = new { token = raw } })).StatusCode.Should().Be(HttpStatusCode.OK);

        var settings = await (await client.GetAsync("/api/settings")).Content.ReadFromJsonAsync<JsonElement>();
        settings.GetProperty("tInvestTokenConfigured").GetBoolean().Should().BeTrue();
        settings.GetProperty("tInvestTokenMasked").GetString().Should().EndWith("5555");

        (await client.DeleteAsync("/api/credentials/tinvest_read")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        settings = await (await client.GetAsync("/api/settings")).Content.ReadFromJsonAsync<JsonElement>();
        settings.GetProperty("tInvestTokenConfigured").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task EveryRegistryProvider_HasARegisteredValidator()
    {
        using var scope = _factory.Services.CreateScope();
        var validators = scope.ServiceProvider.GetServices<ICredentialValidator>().Select(v => v.ProviderId).ToList();

        validators.Should().BeEquivalentTo(CredentialProviders.All.Select(p => p.Id));
    }

    [Theory]
    [InlineData("GET", "/api/credentials")]
    [InlineData("GET", "/api/capabilities")]
    [InlineData("PUT", "/api/credentials/claude_code")]
    [InlineData("DELETE", "/api/credentials/claude_code")]
    public async Task Endpoints_RequireAuthentication(string method, string url)
    {
        var anonymous = _factory.CreateClient();

        var response = await anonymous.SendAsync(new HttpRequestMessage(new HttpMethod(method), url));

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }
}

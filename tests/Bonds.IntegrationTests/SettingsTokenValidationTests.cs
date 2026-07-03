using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Bonds.Infrastructure.Connectors.TInvest;
using Bonds.Infrastructure.Repositories;
using Bonds.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using Xunit;

namespace Bonds.IntegrationTests;

/// <summary>
/// Plan/13 часть C — критерий приёмки: невалидный токен не сохраняется и возвращает 422; валидный
/// сохраняется и возвращает подтверждение с маской счёта. <see cref="TestWebApplicationFactory"/>
/// по умолчанию подставляет мок <see cref="ITInvestTokenValidator"/>, всегда считающий токен
/// валидным (см. её doc-comment) — здесь тесты на конкретно "невалидный токен" переопределяют этот
/// мок через собственный <see cref="WebApplicationFactory{TEntryPoint}.WithWebHostBuilder"/>, не
/// трогая общий фикстур (без сети/реального T-Invest — как и весь остальной набор тестов).
/// </summary>
[Collection("Integration")]
public class SettingsTokenValidationTests
{
    private readonly TestWebApplicationFactory _factory;

    public SettingsTokenValidationTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private async Task<(HttpClient Client, ulong UserId)> CreateAuthorizedClientAsync(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory)
    {
        var userRepo = new UserRepository(_factory.Database.ConnectionString);
        var userId = await userRepo.CreateAsync(new Bonds.Core.Models.User { TelegramId = Random.Shared.NextInt64(1, long.MaxValue) });

        var client = factory.CreateClient();
        var token = JwtTestHelper.GenerateToken(userId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return (client, userId);
    }

    [Fact]
    public async Task PutTInvestToken_ValidatorRejectsToken_Returns422_AndDoesNotPersist()
    {
        var invalidatingFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ITInvestTokenValidator>();
                var validator = new Mock<ITInvestTokenValidator>();
                validator
                    .Setup(v => v.ValidateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(TInvestTokenValidationResult.Invalid("Токен не принят T-Invest (недействителен или отозван)."));
                services.AddScoped(_ => validator.Object);
            });
        });

        var (client, _) = await CreateAuthorizedClientAsync(invalidatingFactory);
        const string rawToken = "t.InvalidReadOnlyTokenThatBrokerRejects";

        var response = await client.PutAsJsonAsync("/api/settings/tinvest-token", new { token = rawToken });

        response.StatusCode.Should().Be((HttpStatusCode)422);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().NotBeNullOrEmpty();
        var raw = await response.Content.ReadAsStringAsync();
        raw.Should().NotContain(rawToken, "невалидный токен тоже никогда не должен эхо-иться обратно");

        var getResponse = await client.GetAsync("/api/settings");
        var getBody = await getResponse.Content.ReadFromJsonAsync<JsonElement>();
        getBody.GetProperty("tInvestTokenConfigured").GetBoolean().Should().BeFalse("невалидный токен не должен попадать в БД");
    }

    [Fact]
    public async Task PutTInvestToken_ValidatorAcceptsToken_Returns200_WithMaskedAccountConfirmation()
    {
        var validatingFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ITInvestTokenValidator>();
                var validator = new Mock<ITInvestTokenValidator>();
                validator
                    .Setup(v => v.ValidateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(TInvestTokenValidationResult.Valid("BROKER-ACCOUNT-5678"));
                services.AddScoped(_ => validator.Object);
            });
        });

        var (client, _) = await CreateAuthorizedClientAsync(validatingFactory);
        const string rawToken = "t.ValidReadOnlyTokenAcceptedByBroker";

        var response = await client.PutAsJsonAsync("/api/settings/tinvest-token", new { token = rawToken });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("tInvestTokenConfigured").GetBoolean().Should().BeTrue();
        body.GetProperty("validatedAccountIdMasked").GetString().Should().EndWith("5678");

        var getResponse = await client.GetAsync("/api/settings");
        var getBody = await getResponse.Content.ReadFromJsonAsync<JsonElement>();
        getBody.GetProperty("tInvestTokenConfigured").GetBoolean().Should().BeTrue("валидный токен должен сохраниться");
    }
}

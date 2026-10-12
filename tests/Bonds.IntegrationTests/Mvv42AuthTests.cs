using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Bonds.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Bonds.IntegrationTests;

/// <summary>
/// Shared mvv42 auth at the edge of the API (no database needed): every endpoint except /health requires the
/// access cookie, only the owner (tg == Telegram:OwnerId) with access to the <c>bonds</c> app passes, the
/// legacy Bearer header means nothing, unsafe requests with the cookie need an allowed Origin.
/// </summary>
[Collection("NoDatabase")]
public class Mvv42AuthTests
{
    private const string ProtectedPath = "/api/positions";

    private readonly NoDatabaseWebApplicationFactory _factory;

    public Mvv42AuthTests(NoDatabaseWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private static string ConcretePath(RoutePattern pattern) =>
        "/" + string.Join('/', pattern.PathSegments.Select(segment => string.Concat(segment.Parts.Select(part => part switch
        {
            RoutePatternLiteralPart literal => literal.Content,
            RoutePatternParameterPart => "1",
            RoutePatternSeparatorPart separator => separator.Content,
            _ => "",
        }))));

    private IReadOnlyList<(string Method, string Path, bool Anonymous)> Endpoints()
    {
        _ = _factory.Server; // builds the host
        return _factory.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .SelectMany(e =>
            {
                var methods = e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"];
                var anonymous = e.Metadata.GetMetadata<IAllowAnonymous>() is not null;
                return methods.Select(m => (m, ConcretePath(e.RoutePattern), anonymous));
            })
            .ToList();
    }

    [Fact]
    public void OnlyHealth_IsAnonymous()
    {
        var endpoints = Endpoints();

        endpoints.Should().HaveCountGreaterThan(30, "the whole API surface must be enumerated");
        endpoints.Where(e => e.Anonymous).Select(e => e.Path).Should().BeEquivalentTo(["/health"]);
    }

    [Fact]
    public async Task EveryEndpointExceptHealth_WithoutCookie_Returns401()
    {
        var client = _factory.CreateClient();
        var failures = new List<string>();

        foreach (var (method, path, anonymous) in Endpoints().Where(e => !e.Anonymous))
        {
            var request = new HttpRequestMessage(new HttpMethod(method), path);
            if (method is "POST" or "PUT" or "PATCH")
                request.Content = JsonContent.Create(new { });

            var response = await client.SendAsync(request);
            if (response.StatusCode != HttpStatusCode.Unauthorized)
                failures.Add($"{method} {path} → {(int)response.StatusCode}");
        }

        failures.Should().BeEmpty();
    }

    [Fact]
    public async Task Health_IsAnonymous()
    {
        var response = await _factory.CreateClient().GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task NonOwnerTelegramUser_WithBondsAccess_Returns403()
    {
        // Every platform admin has apps ∋ bonds (app access=admin); bonds still admits only Telegram:OwnerId.
        var token = Mvv42TestAuth.CreateToken(NoDatabaseWebApplicationFactory.OwnerTelegramId + 1, isAdmin: true);
        var client = Mvv42TestAuth.WithToken(_factory.CreateClient(), token);

        var response = await client.GetAsync(ProtectedPath);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Owner_WithBondsAccess_PassesAuthentication_AndAuthorization()
    {
        // No database here: getting past auth shows up as the handler's own failure (500), never 401/403.
        var client = Mvv42TestAuth.Authorize(_factory.CreateClient(), NoDatabaseWebApplicationFactory.OwnerTelegramId);

        var response = await client.GetAsync("/api/watchlist");

        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized).And.NotBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Owner_WithoutBondsAppAccess_Returns403()
    {
        var token = Mvv42TestAuth.CreateToken(NoDatabaseWebApplicationFactory.OwnerTelegramId, apps: ["cashpulse"]);
        var client = Mvv42TestAuth.WithToken(_factory.CreateClient(), token);

        var response = await client.GetAsync(ProtectedPath);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ExpiredToken_Returns401()
    {
        var token = Mvv42TestAuth.CreateToken(NoDatabaseWebApplicationFactory.OwnerTelegramId, lifetime: TimeSpan.FromMinutes(-5));
        var client = Mvv42TestAuth.WithToken(_factory.CreateClient(), token);

        var response = await client.GetAsync(ProtectedPath);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TokenSignedByUnknownKey_Returns401()
    {
        var foreignKey = new ECDsaSecurityKey(ECDsa.Create(ECCurve.NamedCurves.nistP256)) { KeyId = "foreign" };
        var token = Mvv42TestAuth.CreateToken(NoDatabaseWebApplicationFactory.OwnerTelegramId, signingKey: foreignKey);
        var client = Mvv42TestAuth.WithToken(_factory.CreateClient(), token);

        var response = await client.GetAsync(ProtectedPath);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ValidToken_InAuthorizationHeader_IsIgnored_Returns401()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", Mvv42TestAuth.CreateToken(NoDatabaseWebApplicationFactory.OwnerTelegramId));

        var response = await client.GetAsync(ProtectedPath);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task UnsafeRequest_WithCookie_FromForeignOrigin_Returns403()
    {
        var client = Mvv42TestAuth.Authorize(_factory.CreateClient(), NoDatabaseWebApplicationFactory.OwnerTelegramId);
        client.DefaultRequestHeaders.Remove("Origin");
        client.DefaultRequestHeaders.Add("Origin", "https://evil.example");

        var response = await client.PostAsJsonAsync("/api/sync", new { });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().Contain("origin_not_allowed");
    }

    [Fact]
    public async Task LegacyLoginEndpoints_AreGone()
    {
        Endpoints().Select(e => e.Path).Should().NotContain(p => p.StartsWith("/api/auth"));

        var response = await _factory.CreateClient().PostAsJsonAsync("/api/auth/telegram", new { id = 1 });
        response.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.Unauthorized);
    }
}

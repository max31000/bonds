using System.Globalization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Mvv42.Auth.Client;

namespace Bonds.IntegrationTests.Infrastructure;

/// <summary>
/// Test side of the shared mvv42 auth: an ES256 key standing in for the auth service's JWKS, access tokens
/// shaped exactly like <c>mvv42-auth</c>'s <c>AccessTokenIssuer</c> (sub = AUTH user id, tg, name, adm, apps),
/// and a helper that puts the token into the access cookie (+ an allowed Origin for unsafe methods).
/// </summary>
public static class Mvv42TestAuth
{
    public const string CookieName = "__Host-mvv42_at";
    public const string AllowedOrigin = "https://mvv42.ru";
    private const string Issuer = "https://mvv42.ru/auth";
    private const string Audience = "mvv42";

    private static readonly ECDsaSecurityKey SigningKey = new(ECDsa.Create(ECCurve.NamedCurves.nistP256)) { KeyId = "test-es256" };

    /// <summary>
    /// Replaces the JWKS-backed configuration manager of the <c>mvv42</c> scheme with a static one holding the
    /// test key (the stub JWKS). Everything else — cookie-only token, issuer/audience/alg checks, the
    /// <c>mvv42:app</c> policy, the Origin filter — is the production code path.
    /// </summary>
    public static void UseTestSigningKey(IServiceCollection services) =>
        services.PostConfigure<JwtBearerOptions>(Mvv42Auth.Scheme, o =>
        {
            var config = new OpenIdConnectConfiguration { Issuer = Issuer };
            config.SigningKeys.Add(SigningKey);
            o.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(config);
        });

    /// <param name="authUserId">The auth service's user id (<c>sub</c>) — deliberately NOT the local users.id.</param>
    public static string CreateToken(
        long telegramId,
        string authUserId = "900001",
        string name = "Test Owner",
        IReadOnlyList<string>? apps = null,
        bool isAdmin = false,
        TimeSpan? lifetime = null,
        SecurityKey? signingKey = null)
    {
        var now = DateTime.UtcNow;
        var expires = now.Add(lifetime ?? TimeSpan.FromMinutes(10));
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            IssuedAt = expires < now ? expires.AddMinutes(-10) : now,
            NotBefore = expires < now ? expires.AddMinutes(-10) : now,
            Expires = expires,
            SigningCredentials = new SigningCredentials(signingKey ?? SigningKey, SecurityAlgorithms.EcdsaSha256),
            Claims = new Dictionary<string, object>
            {
                [JwtRegisteredClaimNames.Sub] = authUserId,
                ["tg"] = telegramId.ToString(CultureInfo.InvariantCulture),
                ["name"] = name,
                ["adm"] = isAdmin ? "true" : "false",
                ["apps"] = (apps ?? ["bonds"]).ToList(),
            },
        });
    }

    /// <summary>Authenticates every request of <paramref name="client"/> as the given Telegram user.</summary>
    public static HttpClient Authorize(HttpClient client, long telegramId, string authUserId = "900001")
        => WithToken(client, CreateToken(telegramId, authUserId));

    public static HttpClient WithToken(HttpClient client, string token)
    {
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie", $"{CookieName}={token}");
        client.DefaultRequestHeaders.Remove("Origin");
        client.DefaultRequestHeaders.Add("Origin", AllowedOrigin);
        return client;
    }
}

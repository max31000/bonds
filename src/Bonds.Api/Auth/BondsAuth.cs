using System.Globalization;
using System.Security.Claims;
using Bonds.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.Extensions.Caching.Memory;
using Mvv42.Auth.Client;

namespace Bonds.Api.Auth;

/// <summary>
/// bonds is single-owner: its data (broker token, the one account) belongs to <c>Telegram:OwnerId</c>.
/// The mvv42 app policy alone (<c>apps ∋ bonds</c>) would admit every platform admin (app access=admin),
/// so every request additionally needs <c>tg == Telegram:OwnerId</c> — the rule of the former Telegram login.
/// </summary>
public interface IOwnerAccess
{
    bool IsOwner(long telegramId);
}

/// <summary>Reads <c>Telegram:OwnerId</c> lazily (tests override configuration after DI registration).</summary>
public sealed class ConfigurationOwnerAccess(IConfiguration config) : IOwnerAccess
{
    public bool IsOwner(long telegramId) =>
        long.TryParse(config["Telegram:OwnerId"], NumberStyles.None, CultureInfo.InvariantCulture, out var owner)
        && owner == telegramId;
}

/// <summary>Authorization requirement: the mvv42 user's Telegram id is the owner's.</summary>
public sealed class OwnerRequirement : IAuthorizationRequirement;

public sealed class OwnerRequirementHandler(IOwnerAccess owner) : AuthorizationHandler<OwnerRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, OwnerRequirement requirement)
    {
        if (context.User.GetMvv42User() is { } me && owner.IsOwner(me.TelegramId))
            context.Succeed(requirement);
        return Task.CompletedTask;
    }
}

/// <summary>
/// The local user of the current request. Never derived from the auth service's <c>sub</c>: the local
/// <c>users</c> row is found (or created, with Account onboarding) by the <c>tg</c> claim only.
/// </summary>
public interface ICurrentUser
{
    /// <summary>
    /// Local <c>users.id</c>; provisions the row on first use. <c>null</c> when the request is not
    /// authenticated via mvv42 or is not the owner (the fallback policy already rejects both).
    /// </summary>
    Task<ulong?> GetUserIdAsync();
}

public sealed class CurrentUser(
    IHttpContextAccessor http,
    IOwnerAccess owner,
    UserProvisioning provisioning,
    IMemoryCache cache) : ICurrentUser
{
    private ulong? _resolved;

    public async Task<ulong?> GetUserIdAsync()
    {
        if (_resolved is { } done)
            return done;

        var principal = http.HttpContext?.User ?? new ClaimsPrincipal();
        if (principal.GetMvv42User() is not { } me || !owner.IsOwner(me.TelegramId))
            return null;

        var ct = http.HttpContext?.RequestAborted ?? CancellationToken.None;
        var id = await cache.GetOrCreateAsync($"bonds-local-uid:{me.TelegramId}", entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10);
            return provisioning.GetOrCreateAsync(me.TelegramId, me.Name, ct);
        });

        _resolved = id;
        return id;
    }
}

/// <summary>
/// Authorization failures as JSON the frontend can tell apart: a forbidden request (authenticated, but not the
/// owner or without the bonds app) → 403 <c>{"error":"access_denied"}</c> → global «Нет доступа» screen.
/// Other 403s (the SDK's <c>origin_not_allowed</c>, business rules) keep their own codes. 401 stays as is.
/// </summary>
public sealed class AccessDeniedResultHandler : IAuthorizationMiddlewareResultHandler
{
    public const string Code = "access_denied";

    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Forbidden)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = Code });
            return;
        }
        await _default.HandleAsync(next, context, policy, authorizeResult);
    }
}

public static class BondsAuthExtensions
{
    public const string AppId = "bonds";

    /// <summary>
    /// Shared mvv42 auth (cookie JWT, JWKS) as the only scheme; fallback/default policy = the mvv42 app
    /// policy + owner check. Unauthenticated → 401, authenticated non-owner → 403.
    /// </summary>
    public static IServiceCollection AddBondsAuth(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddAuthentication(Mvv42Auth.Scheme);
        services.AddMvv42Auth(AppId, o => configuration.GetSection("Mvv42Auth").Bind(o));
        services.AddSingleton<IOwnerAccess, ConfigurationOwnerAccess>();
        services.AddSingleton<IAuthorizationHandler, OwnerRequirementHandler>();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, AccessDeniedResultHandler>();
        services.AddAuthorization(o =>
        {
            var policy = new AuthorizationPolicyBuilder()
                .Combine(o.GetPolicy(Mvv42Auth.Policy)
                         ?? throw new InvalidOperationException($"{Mvv42Auth.Policy} policy is not registered"))
                .AddRequirements(new OwnerRequirement())
                .Build();
            o.FallbackPolicy = policy;
            o.DefaultPolicy = policy;
        });

        services.AddHttpContextAccessor();
        services.AddMemoryCache();
        services.AddScoped<ICurrentUser, CurrentUser>();
        return services;
    }
}

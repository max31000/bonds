using System.Net;
using System.Text.Json;
using Bonds.Infrastructure.Agents.Mcp;

namespace Bonds.Api.Endpoints;

/// <summary>
/// MCP-сервер bonds для процесса ИИ-агента (задача 68): <c>POST /internal/mcp/{runId}</c> (streamable HTTP, ответ — один JSON). Вне cookie-авторизации пользователей
/// (<c>AllowAnonymous</c>) — своя защита:
/// <list type="number">
/// <item>только loopback: <c>RemoteIpAddress</c> 127.0.0.1/::1 и нет заголовков прокси (<c>X-Forwarded-For</c>/<c>Forwarded</c>), иначе 404 — снаружи (nginx, docker-сеть)
/// эндпоинта «нет»; nginx его и не проксирует (только <c>/bonds/api/</c>);</item>
/// <item><c>Origin</c> в запросе — 403 (защита от DNS-rebinding из браузера, требование спецификации MCP);</item>
/// <item><c>Authorization: Bearer</c> — одноразовый токен запуска (<see cref="AgentMcpTokens"/>), привязанный к пользователю и <c>runId</c>: нет, неверный, истёкший,
/// отозванный или чужого запуска — 401;</item>
/// <item>тело ≤ 1 МБ (413).</item>
/// </list>
/// Пользователь — только из токена. <c>GET</c> (SSE-поток) не поддерживается — 405.
/// </summary>
public static class McpEndpoints
{
    public const string Route = "/internal/mcp/{runId}";
    private const long MaxBodyBytes = 1024 * 1024;

    public static void MapMcpEndpoints(this WebApplication app)
    {
        app.MapPost(Route, Handle).AllowAnonymous().ExcludeFromDescription();
        app.MapMethods(Route, ["GET", "DELETE"], (HttpContext ctx) => IsLoopbackRequest(ctx) ? Results.StatusCode(StatusCodes.Status405MethodNotAllowed) : Results.NotFound())
            .AllowAnonymous().ExcludeFromDescription();
    }

    /// <summary>Запрос пришёл с loopback и не через прокси.</summary>
    internal static bool IsLoopbackRequest(HttpContext ctx) =>
        ctx.Connection.RemoteIpAddress is { } ip && IPAddress.IsLoopback(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip)
        && !ctx.Request.Headers.ContainsKey("X-Forwarded-For") && !ctx.Request.Headers.ContainsKey("Forwarded") && !ctx.Request.Headers.ContainsKey("X-Real-IP");

    private static async Task<IResult> Handle(HttpContext ctx, ulong runId, AgentMcpTokens tokens, BondsMcpServer server, CancellationToken ct)
    {
        if (!IsLoopbackRequest(ctx)) return Results.NotFound();
        if (ctx.Request.Headers.ContainsKey("Origin")) return Results.StatusCode(StatusCodes.Status403Forbidden);

        var header = ctx.Request.Headers.Authorization.ToString();
        var bearer = header.StartsWith("Bearer ", StringComparison.Ordinal) ? header["Bearer ".Length..].Trim() : null;
        if (tokens.Validate(bearer, runId) is not { } userId)
        {
            ctx.Response.Headers.WWWAuthenticate = "Bearer";
            return Results.Unauthorized();
        }

        if (ctx.Request.ContentLength is > MaxBodyBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

        JsonDocument doc;
        try
        {
            using var limited = new MemoryStream();
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = await ctx.Request.Body.ReadAsync(buffer, ct)) > 0)
            {
                if (limited.Length + read > MaxBodyBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                limited.Write(buffer, 0, read);
            }

            doc = JsonDocument.Parse(limited.ToArray());
        }
        catch (JsonException)
        {
            return Results.Json(BondsMcpServer.Error(null, -32700, "Parse error"), statusCode: StatusCodes.Status400BadRequest);
        }

        using (doc)
        {
            var response = await server.HandleAsync(doc.RootElement, userId, ct, runId);
            return response is null ? Results.StatusCode(StatusCodes.Status202Accepted) : Results.Text(response.ToJsonString(), "application/json");
        }
    }
}

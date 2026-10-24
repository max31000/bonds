using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Bonds.Infrastructure.Agents.Mcp;

/// <summary>
/// Минимальный MCP-сервер bonds поверх JSON-RPC 2.0 (задача 68; транспорт — streamable HTTP, ответ одним JSON без SSE): <c>initialize</c>, <c>ping</c>,
/// <c>tools/list</c>, <c>tools/call</c>; уведомления (без <c>id</c>) — без ответа. Пакеты (массивы) не поддерживаются — спецификация 2025-06-18 их убрала.
/// Пользователь берётся ТОЛЬКО из bearer-токена запуска (<see cref="AgentMcpTokens"/>), не из параметров запроса.
/// </summary>
public sealed class BondsMcpServer
{
    public const string LatestProtocolVersion = "2025-06-18";
    private static readonly string[] SupportedVersions = [LatestProtocolVersion, "2025-03-26", "2024-11-05"];

    private readonly BondsMcpTools _tools;
    private readonly ILogger<BondsMcpServer> _logger;

    public BondsMcpServer(BondsMcpTools tools, ILogger<BondsMcpServer> logger)
    {
        _tools = tools;
        _logger = logger;
    }

    /// <summary>Ответ на сообщение или null (уведомление — HTTP 202 без тела). <paramref name="runId"/> — запуск агента (лимит <c>propose_plan</c> на запуск).</summary>
    public async Task<JsonObject?> HandleAsync(JsonElement message, ulong userId, CancellationToken ct = default, ulong runId = 0)
    {
        if (message.ValueKind != JsonValueKind.Object) return Error(null, -32600, "Invalid Request: ожидается один JSON-RPC объект");

        JsonNode? id = message.TryGetProperty("id", out var idEl) && idEl.ValueKind is JsonValueKind.String or JsonValueKind.Number ? JsonNode.Parse(idEl.GetRawText()) : null;
        var method = message.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
        if (method is null) return id is null ? null : Error(id, -32600, "Invalid Request: нет method");
        if (id is null) return null; // уведомление (notifications/initialized, notifications/cancelled …)

        var @params = message.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Object ? p : default;
        switch (method)
        {
            case "initialize":
            {
                var requested = @params.ValueKind == JsonValueKind.Object && @params.TryGetProperty("protocolVersion", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                var version = requested is not null && SupportedVersions.Contains(requested, StringComparer.Ordinal) ? requested : LatestProtocolVersion;
                return Result(id, new JsonObject
                {
                    ["protocolVersion"] = version,
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                    ["serverInfo"] = new JsonObject { ["name"] = "bonds", ["version"] = "1.0.0" },
                    ["instructions"] = "Данные портфеля пользователя — только чтение. Единственное действие — propose_plan: план ждёт решения пользователя.",
                });
            }

            case "ping":
                return Result(id, new JsonObject());

            case "tools/list":
                return Result(id, new JsonObject { ["tools"] = BondsMcpTools.Definitions() });

            case "tools/call":
            {
                var name = @params.ValueKind == JsonValueKind.Object && @params.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                if (name is null || !BondsMcpTools.Names.Contains(name)) return Error(id, -32602, "Unknown tool");
                var args = @params.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.Object ? a : default;

                var (text, isError) = await _tools.CallAsync(name, args, userId, ct, runId);
                return Result(id, new JsonObject
                {
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
                    ["isError"] = isError,
                });
            }

            default:
                _logger.LogInformation("MCP: unsupported method {Method}", method.Length > 64 ? method[..64] : method);
                return Error(id, -32601, "Method not found");
        }
    }

    private static JsonObject Result(JsonNode? id, JsonObject result) => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    public static JsonObject Error(JsonNode? id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };
}

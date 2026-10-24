using Bonds.Api.Auth;
using Bonds.Core.Credentials;
using Bonds.Core.Execution;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Telegram;
using Bonds.Infrastructure.Execution;

namespace Bonds.Api.Endpoints;

/// <summary>
/// Режим исполнения пользователя (задача 65). Эндпоинты брокера не вызывают.
/// <list type="bullet">
/// <item><c>GET /api/settings/execution</c> — режим, условия ворот и итог (<c>live</c>) с причинами; задача 66 — ворота ПО БРОКЕРУ в <c>brokers.tinvest</c> /
/// <c>brokers.freedom</c>; верхний уровень — сводка: <c>live</c>/<c>tradeCredentialOk</c> = хотя бы у одного брокера, <c>reasons</c> — причины закрытых брокеров.</item>
/// <item><c>PUT /api/settings/execution-mode {mode, confirmation}</c> — <c>live</c> требует проверенный торговый токен (status ok + сетевая проверка) и
/// <c>confirmation == "LIVE"</c> (повторный ввод слова в UI); включение — сообщение в бот «Исполнение включено». <c>observe</c> — без условий.</item>
/// <item><c>PUT /api/settings/execution-pause {paused}</c> — то же, что <c>/stop</c>/<c>/resume</c> в боте (кнопка «Остановить» в вебе).</item>
/// </list>
/// Глобальный <c>Execution__Enabled</c> отсюда не меняется (только env на сервере, владельцем).
/// </summary>
public static class ExecutionSettingsEndpoints
{
    public const string LiveConfirmationWord = "LIVE";

    public static void MapExecutionSettingsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/settings/execution", GetExecution);
        app.MapPut("/api/settings/execution-mode", PutMode);
        app.MapPut("/api/settings/execution-pause", PutPause);
    }

    private static IResult ValidationFailure(string error) =>
        Results.Json(new { error, type = "ValidationException" }, statusCode: StatusCodes.Status422UnprocessableEntity);

    private static async Task<IResult> GetExecution(ICurrentUser currentUser, IExecutionGateResolver gate, ITradeOrderRepository orders)
    {
        if (await currentUser.GetUserIdAsync() is not { } userId) return Results.NotFound();
        return Results.Ok(await StateAsync(gate, userId) with { ActiveOrders = (await orders.ListActiveByUserAsync(userId)).Count });
    }

    private static async Task<IResult> PutMode(
        ExecutionModeRequestDto? request, ICurrentUser currentUser, IUserSettingsRepository settings, IExecutionGateResolver gate, IExecutionNotifier notifier,
        CancellationToken ct)
    {
        if (await currentUser.GetUserIdAsync() is not { } userId) return Results.NotFound();
        var mode = request?.Mode?.Trim().ToLowerInvariant();
        if (!ExecutionModes.IsValid(mode)) return ValidationFailure("mode: допустимо observe или live.");

        if (mode == ExecutionModes.Live)
        {
            if (!string.Equals(request!.Confirmation, LiveConfirmationWord, StringComparison.Ordinal))
                return ValidationFailure($"Для включения исполнения введите слово «{LiveConfirmationWord}».");
            // Режим общий для брокеров (задача 66): нужен хотя бы один проверенный торговый ключ — T-Invest или Freedom; ворота каждого брокера всё равно проверяются отдельно.
            var before = await gate.ResolveAsync(userId);
            var freedom = await gate.ResolveForBrokerAsync(userId, Bonds.Core.Models.Brokers.Freedom);
            if (!before.TradeCredentialOk && !freedom.TradeCredentialOk)
                return ValidationFailure("Нужен проверенный торговый токен T-Invest или торговый ключ Freedom (Настройки → Подключения → «T-Invest (торговля)» / «Freedom (торговля)»).");
        }

        var previous = await settings.GetExecutionModeAsync(userId);
        await settings.SetExecutionModeAsync(userId, mode!);
        var result = await StateAsync(gate, userId);
        if (previous != mode)
            await notifier.NotifyAsync(userId, new BotMessage(mode == ExecutionModes.Live ? ExecutionMessages.Enabled(result.Reasons) : ExecutionMessages.ExecutionDisabled), ct);
        return Results.Ok(result);
    }

    private static async Task<IResult> PutPause(ExecutionPauseRequestDto? request, ICurrentUser currentUser, IUserSettingsRepository settings, IExecutionGateResolver gate)
    {
        if (await currentUser.GetUserIdAsync() is not { } userId) return Results.NotFound();
        if (request?.Paused is not { } paused) return ValidationFailure("paused: true или false.");
        await settings.SetExecutionPausedAsync(userId, paused);
        return Results.Ok(await StateAsync(gate, userId));
    }

    /// <summary>Ворота обоих брокеров (задача 66) и сводка: открыт хотя бы один — <c>live</c>; причины — только закрытых брокеров, с префиксом брокера.</summary>
    internal static async Task<ExecutionStateDto> StateAsync(IExecutionGateResolver gate, ulong userId)
    {
        var tinvest = await gate.ResolveAsync(userId);
        var freedom = await gate.ResolveForBrokerAsync(userId, Bonds.Core.Models.Brokers.Freedom);
        var reasons = new List<string>();
        if (!tinvest.Live) reasons.AddRange(tinvest.Reasons.Select(r => "T-Invest: " + r));
        if (!freedom.Live) reasons.AddRange(freedom.Reasons.Select(r => "Freedom: " + r));
        return new ExecutionStateDto
        {
            Mode = tinvest.Mode,
            GlobalEnabled = tinvest.GlobalEnabled,
            TradeCredentialOk = tinvest.TradeCredentialOk || freedom.TradeCredentialOk,
            Paused = tinvest.Paused,
            Live = tinvest.Live || freedom.Live,
            Reasons = reasons,
            Brokers = new BrokerExecutionStatesDto { TInvest = BrokerDto(tinvest), Freedom = BrokerDto(freedom) },
        };
    }

    private static BrokerExecutionStateDto BrokerDto(ExecutionGateResult g) => new() { Live = g.Live, TradeCredentialOk = g.TradeCredentialOk, Reasons = g.Reasons };
}

public sealed record ExecutionModeRequestDto
{
    /// <summary>"observe" | "live".</summary>
    public string? Mode { get; init; }

    /// <summary>Для live — ровно «LIVE» (повторный ввод в UI).</summary>
    public string? Confirmation { get; init; }
}

public sealed record ExecutionPauseRequestDto
{
    public bool? Paused { get; init; }
}

/// <summary>
/// Состояние ворот исполнения. Общие условия (<see cref="Mode"/>, <see cref="GlobalEnabled"/>, <see cref="Paused"/>) — одни на всех брокеров; ключ и итог — по брокеру
/// (<see cref="Brokers"/>). <see cref="Live"/> / <see cref="TradeCredentialOk"/> — хотя бы у одного брокера; <see cref="Reasons"/> — причины закрытых брокеров
/// («T-Invest: …», «Freedom: …»).
/// </summary>
public sealed record ExecutionStateDto
{
    public required string Mode { get; init; }
    public required bool GlobalEnabled { get; init; }
    public required bool TradeCredentialOk { get; init; }
    public required bool Paused { get; init; }
    public required bool Live { get; init; }
    public required IReadOnlyList<string> Reasons { get; init; }

    /// <summary>Активные заявки пользователя у брокера — независимо от режима и ворот (только в <c>GET</c>; в ответах PUT — 0).</summary>
    public int ActiveOrders { get; init; }

    public required BrokerExecutionStatesDto Brokers { get; init; }
}

/// <summary>Ворота по брокерам (задача 66).</summary>
public sealed record BrokerExecutionStatesDto
{
    [System.Text.Json.Serialization.JsonPropertyName("tinvest")]
    public required BrokerExecutionStateDto TInvest { get; init; }
    public required BrokerExecutionStateDto Freedom { get; init; }
}

/// <summary>Ворота одного брокера: <see cref="Live"/> — заявки этого брокера отправляются; ключ — <c>tinvest_trade</c> / <c>freedom_trade</c>.</summary>
public sealed record BrokerExecutionStateDto
{
    public required bool Live { get; init; }
    public required bool TradeCredentialOk { get; init; }
    public required IReadOnlyList<string> Reasons { get; init; }
}

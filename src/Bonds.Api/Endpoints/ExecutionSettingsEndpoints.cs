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
/// <item><c>GET /api/settings/execution</c> — режим, все четыре условия ворот и итог (<c>live</c>) с причинами.</item>
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
        return Results.Ok(ToDto(await gate.ResolveAsync(userId)) with { ActiveOrders = (await orders.ListActiveByUserAsync(userId)).Count });
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
            var before = await gate.ResolveAsync(userId);
            if (!before.TradeCredentialOk)
                return ValidationFailure("Нужен проверенный торговый токен T-Invest (Настройки → Подключения → «T-Invest (торговля)»).");
        }

        var previous = await settings.GetExecutionModeAsync(userId);
        await settings.SetExecutionModeAsync(userId, mode!);
        var result = await gate.ResolveAsync(userId);
        if (previous != mode)
            await notifier.NotifyAsync(userId, new BotMessage(mode == ExecutionModes.Live ? ExecutionMessages.Enabled(result.Reasons) : ExecutionMessages.ExecutionDisabled), ct);
        return Results.Ok(ToDto(result));
    }

    private static async Task<IResult> PutPause(ExecutionPauseRequestDto? request, ICurrentUser currentUser, IUserSettingsRepository settings, IExecutionGateResolver gate)
    {
        if (await currentUser.GetUserIdAsync() is not { } userId) return Results.NotFound();
        if (request?.Paused is not { } paused) return ValidationFailure("paused: true или false.");
        await settings.SetExecutionPausedAsync(userId, paused);
        return Results.Ok(ToDto(await gate.ResolveAsync(userId)));
    }

    private static ExecutionStateDto ToDto(ExecutionGateResult g) => new()
    {
        Mode = g.Mode,
        GlobalEnabled = g.GlobalEnabled,
        TradeCredentialOk = g.TradeCredentialOk,
        Paused = g.Paused,
        Live = g.Live,
        Reasons = g.Reasons,
    };
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

/// <summary>Состояние ворот исполнения. <see cref="Live"/> = true только при всех условиях; иначе причины в <see cref="Reasons"/>.</summary>
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
}

using Bonds.Api.Auth;
using Bonds.Api.Middleware;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;

namespace Bonds.Api.Endpoints;

/// <summary>
/// «События» портфеля (spec §8; маршрут исторически /api/signals):
/// GET /api/signals — АКТИВНЫЕ события счёта (по умолчанию), <c>?include=resolved</c> — вместе с историей;
/// POST /api/signals/{id}/read — подтвердить («прочитано») событие;
/// POST /api/signals/read-all — подтвердить все активные.
/// </summary>
public static class SignalsEndpoints
{
    public static void MapSignalsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/signals", GetSignals);
        app.MapPost("/api/signals/read-all", MarkAllRead);
        app.MapPost("/api/signals/{id}/read", MarkRead);
    }

    private static async Task<IResult> GetSignals(
        ICurrentUser currentUser,
        IAccountRepository accountRepo,
        ISignalRepository signalRepo,
        string? include)
    {
        var accountId = await PositionsEndpoints.ResolveAccountIdAsync(currentUser, accountRepo);
        if (accountId is null) return Results.Ok(new SignalsResponseDto { Signals = [] });

        var includeResolved = string.Equals(include, "resolved", StringComparison.OrdinalIgnoreCase);
        var signals = await signalRepo.GetByAccountIdAsync(accountId.Value, includeResolved);

        // Сначала самое важное (Critical → Info), внутри — ближайшая дата события; «состояния» (без даты) в конце группы.
        var ordered = signals
            .OrderBy(s => s.IsResolved)
            .ThenByDescending(s => s.Severity)
            .ThenBy(s => s.EventDate is null)
            .ThenBy(s => s.EventDate)
            .ThenBy(s => s.Id);

        return Results.Ok(new SignalsResponseDto { Signals = ordered.Select(ToDto).ToList() });
    }

    private static async Task<IResult> MarkRead(
        ulong id,
        ICurrentUser currentUser,
        IAccountRepository accountRepo,
        ISignalRepository signalRepo)
    {
        var accountId = await PositionsEndpoints.ResolveAccountIdAsync(currentUser, accountRepo);
        if (accountId is null) throw new NotFoundException("Событие не найдено");

        var found = await signalRepo.AcknowledgeAsync(id, accountId.Value, DateTime.UtcNow);
        if (!found) throw new NotFoundException("Событие не найдено");

        return Results.Ok(new { id, isRead = true });
    }

    private static async Task<IResult> MarkAllRead(
        ICurrentUser currentUser,
        IAccountRepository accountRepo,
        ISignalRepository signalRepo)
    {
        var accountId = await PositionsEndpoints.ResolveAccountIdAsync(currentUser, accountRepo);
        if (accountId is null) return Results.Ok(new { acknowledged = 0 });

        var count = await signalRepo.AcknowledgeAllAsync(accountId.Value, DateTime.UtcNow);
        return Results.Ok(new { acknowledged = count });
    }

    private static SignalDto ToDto(Signal s) => new()
    {
        Id = s.Id,
        Type = s.Type.ToString(),
        Severity = s.Severity.ToString(),
        InstrumentId = s.InstrumentId,
        Subject = s.Subject,
        SuggestedAction = s.SuggestedAction,
        EventDate = s.EventDate,
        // DATETIME без пояса пишется из UtcNow — помечаем UTC, чтобы JSON нёс «Z» и фронт не сдвигал время.
        FirstSeenAt = DateTime.SpecifyKind(s.FirstSeenAt, DateTimeKind.Utc),
        IsRead = s.IsAcknowledged,
        IsResolved = s.IsResolved,
        ResolvedAt = s.ResolvedAt is { } resolved ? DateTime.SpecifyKind(resolved, DateTimeKind.Utc) : null,
    };
}

public sealed record SignalsResponseDto
{
    public required IReadOnlyList<SignalDto> Signals { get; init; }
}

/// <summary>
/// Событие портфеля. <c>Type</c>/<c>Severity</c> — строки enum бэка (<c>SignalType</c>/<c>SignalSeverity</c>:
/// Info|Warning|Critical). <c>EventDate</c> — null для «состояний» (концентрация, свободные деньги).
/// Даты-время — UTC.
/// </summary>
public sealed record SignalDto
{
    public required ulong Id { get; init; }
    public required string Type { get; init; }
    public required string Severity { get; init; }
    public ulong? InstrumentId { get; init; }
    public string? Subject { get; init; }
    public string? SuggestedAction { get; init; }
    public DateOnly? EventDate { get; init; }
    public required DateTime FirstSeenAt { get; init; }
    public required bool IsRead { get; init; }
    public required bool IsResolved { get; init; }
    public DateTime? ResolvedAt { get; init; }
}

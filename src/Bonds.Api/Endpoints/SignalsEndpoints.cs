using System.Security.Claims;
using Bonds.Api.Middleware;
using Bonds.Core.Interfaces.Repositories;

namespace Bonds.Api.Endpoints;

/// <summary>
/// GET /api/signals — активные (непрочитанные) сигналы счёта (plan/08, spec §8).
/// POST /api/signals/{id}/read — помечает сигнал прочитанным/закрытым.
/// </summary>
public static class SignalsEndpoints
{
    public static void MapSignalsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/signals", GetSignals);
        app.MapPost("/api/signals/{id}/read", MarkRead);
    }

    private static async Task<IResult> GetSignals(
        ClaimsPrincipal principal,
        IAccountRepository accountRepo,
        ISignalRepository signalRepo,
        bool? unreadOnly)
    {
        var accountId = await PositionsEndpoints.ResolveAccountIdAsync(principal, accountRepo);
        if (accountId is null) return Results.Ok(new SignalsResponseDto { Signals = [] });

        // unreadOnly=false (по умолчанию не задан) -> показываем все; unreadOnly=true -> только непрочитанные.
        var isReadFilter = (unreadOnly == true) ? (bool?)false : null;
        var signals = (await signalRepo.GetByAccountIdAsync(accountId.Value, isReadFilter))
            .OrderByDescending(s => s.Date)
            .ToList();

        var dto = new SignalsResponseDto
        {
            Signals = signals.Select(s => new SignalDto
            {
                Id = s.Id,
                Type = s.Type.ToString(),
                Severity = s.Severity.ToString(),
                PositionId = s.PositionId,
                InstrumentId = s.InstrumentId,
                SuggestedAction = s.SuggestedAction,
                Date = s.Date,
                IsRead = s.IsRead,
            }).ToList(),
        };

        return Results.Ok(dto);
    }

    private static async Task<IResult> MarkRead(
        ulong id,
        ClaimsPrincipal principal,
        IAccountRepository accountRepo,
        ISignalRepository signalRepo)
    {
        var accountId = await PositionsEndpoints.ResolveAccountIdAsync(principal, accountRepo);
        if (accountId is null) throw new NotFoundException("Сигнал не найден");

        await signalRepo.MarkReadAsync(id, accountId.Value);
        return Results.Ok(new { id, isRead = true });
    }
}

public sealed record SignalsResponseDto
{
    public required IReadOnlyList<SignalDto> Signals { get; init; }
}

public sealed record SignalDto
{
    public required ulong Id { get; init; }
    public required string Type { get; init; }
    public required string Severity { get; init; }
    public ulong? PositionId { get; init; }
    public ulong? InstrumentId { get; init; }
    public string? SuggestedAction { get; init; }
    public required DateOnly Date { get; init; }
    public required bool IsRead { get; init; }
}

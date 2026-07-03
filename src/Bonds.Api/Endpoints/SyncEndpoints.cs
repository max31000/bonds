using Bonds.Infrastructure.Scheduling;

namespace Bonds.Api.Endpoints;

/// <summary>
/// POST /api/sync — форс-обновление (запуск цикла этапа 07, <see cref="ISyncCycleRunner.RunCycleAsync"/>).
/// GET /api/sync/status — статус последнего/текущего синка.
/// Single-user продукт — единственный счёт, нет необходимости передавать AccountId с фронта
/// (см. doc-comment <see cref="ISyncCycleRunner"/>: цикл сам резолвит единственный Account).
/// </summary>
public static class SyncEndpoints
{
    public static void MapSyncEndpoints(this WebApplication app)
    {
        app.MapPost("/api/sync", PostSync);
        app.MapGet("/api/sync/status", GetSyncStatus);
    }

    private static async Task<IResult> PostSync(ISyncCycleRunner syncRunner, CancellationToken ct)
    {
        var result = await syncRunner.RunCycleAsync(ct);

        var dto = new SyncRunResultDto
        {
            AlreadyRunning = result.AlreadyRunning,
            NoAccountConfigured = result.NoAccountConfigured,
            TokenMissingOrInvalid = result.TokenMissingOrInvalid,
            InstrumentsSynced = result.InstrumentsSynced,
            OperationsUpserted = result.OperationsUpserted,
            YieldCurveUpdated = result.YieldCurveUpdated,
            PositionsProjected = result.PositionsProjected,
            FlowsWritten = result.FlowsWritten,
            SnapshotStored = result.SnapshotStored,
            SignalsCreated = result.SignalsCreated,
            Errors = result.Errors,
            HasErrors = result.HasErrors,
        };

        // Частичный сбой одного из шагов цикла — не 500: вызывающий уже получил частичный
        // результат с конкретными ошибками в Errors (spec §4.4 "деградация с пометками").
        return Results.Ok(dto);
    }

    private static IResult GetSyncStatus(ISyncCycleRunner syncRunner)
    {
        var status = syncRunner.GetStatus();

        var dto = new SyncStatusDto
        {
            IsRunning = status.IsRunning,
            LastRunStartedAtUtc = status.LastRunStartedAtUtc,
            LastSuccessAtUtc = status.LastSuccessAtUtc,
            LastFailureAtUtc = status.LastFailureAtUtc,
            LastRunErrors = status.LastRunErrors,
            TokenMissingOrInvalid = status.TokenMissingOrInvalid,
        };

        return Results.Ok(dto);
    }
}

public sealed record SyncRunResultDto
{
    public required bool AlreadyRunning { get; init; }
    public required bool NoAccountConfigured { get; init; }
    public required bool TokenMissingOrInvalid { get; init; }
    public required int InstrumentsSynced { get; init; }
    public required int OperationsUpserted { get; init; }
    public required bool YieldCurveUpdated { get; init; }
    public required int PositionsProjected { get; init; }
    public required int FlowsWritten { get; init; }
    public required bool SnapshotStored { get; init; }
    public required int SignalsCreated { get; init; }
    public required IReadOnlyList<string> Errors { get; init; }
    public required bool HasErrors { get; init; }
}

public sealed record SyncStatusDto
{
    public required bool IsRunning { get; init; }
    public DateTime? LastRunStartedAtUtc { get; init; }
    public DateTime? LastSuccessAtUtc { get; init; }
    public DateTime? LastFailureAtUtc { get; init; }
    public required IReadOnlyList<string> LastRunErrors { get; init; }
    public required bool TokenMissingOrInvalid { get; init; }
}

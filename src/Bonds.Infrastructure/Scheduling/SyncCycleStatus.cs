namespace Bonds.Infrastructure.Scheduling;

/// <summary>
/// Снимок состояния последнего/текущего цикла синка (plan/07 Часть B) — потокобезопасное
/// in-memory чтение для будущего <c>GET /api/sync/status</c> (этап 08). Immutable record:
/// <see cref="SyncCycleService.GetStatus"/> возвращает копию текущего внутреннего состояния, а не
/// ссылку на мутируемый объект, поэтому читатель не увидит частично обновлённое состояние даже
/// без блокировки на своей стороне.
/// </summary>
public sealed record SyncCycleStatus
{
    public required bool IsRunning { get; init; }
    public DateTime? LastRunStartedAtUtc { get; init; }
    public DateTime? LastSuccessAtUtc { get; init; }
    public DateTime? LastFailureAtUtc { get; init; }
    public IReadOnlyList<string> LastRunErrors { get; init; } = Array.Empty<string>();
}

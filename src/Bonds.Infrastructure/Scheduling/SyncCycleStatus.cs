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

    /// <summary>
    /// Plan/13 часть B: true, если у заведённого пользователя токен T-Invest не задан или не
    /// расшифровался (см. <see cref="Bonds.Infrastructure.Services.TInvestTokenStatus"/>) —
    /// выставляется циклом синка (<see cref="SyncCycleService"/>) по результату
    /// <c>ITInvestTokenProvider.GetTokenStatusAsync</c>, чтобы фронт мог показать явный бейдж
    /// вместо того, чтобы пользователь узнавал о протухшем токене по пустым данным.
    /// </summary>
    public bool TokenMissingOrInvalid { get; init; }
}

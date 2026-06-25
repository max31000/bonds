using Bonds.Core.Analytics;
using Bonds.Core.Models;

namespace Bonds.Core.Signals;

/// <summary>
/// Один holding-инструмент с привязанными расписаниями — вход правил приближающихся событий
/// (купон/амортизация/погашение/оферта/пересчёт флоатера). Аналог <see cref="PortfolioHolding"/>,
/// но несёт сырые расписания (а не только посчитанные метрики), потому что правилам нужны
/// конкретные даты, а не агрегаты.
/// </summary>
public sealed record SignalPositionContext
{
    public required ulong PositionId { get; init; }
    public required ulong InstrumentId { get; init; }
    public required string? Issuer { get; init; }
    public required DateOnly MaturityDate { get; init; }

    public IReadOnlyList<CouponSchedule> Coupons { get; init; } = Array.Empty<CouponSchedule>();
    public IReadOnlyList<AmortizationSchedule> Amortizations { get; init; } = Array.Empty<AmortizationSchedule>();
    public IReadOnlyList<OfferSchedule> Offers { get; init; } = Array.Empty<OfferSchedule>();
}

/// <summary>
/// Вход для <see cref="SignalsEngine.Evaluate"/> — все данные, нужные правилам, переданные как
/// плоские модели/DTO (plan/07 Часть A: "никаких репозиториев/БД/HTTP в этом слое"). Сборка этого
/// объекта из репозиториев — ответственность Infrastructure (см. <c>SyncCycleService</c>).
/// </summary>
public sealed record SignalEvaluationInput
{
    public required ulong AccountId { get; init; }
    public required DateOnly AsOf { get; init; }

    /// <summary>Контексты позиций для правил приближающихся событий (1-3 правил из §8).</summary>
    public IReadOnlyList<SignalPositionContext> Positions { get; init; } = Array.Empty<SignalPositionContext>();

    /// <summary>
    /// Holdings с уже посчитанными метриками (BondMetrics inline, см. doc-comment SyncCycleService) —
    /// вход для правил концентрации/дюрации/доходности (правила 5/6/7).
    /// </summary>
    public IReadOnlyList<PortfolioHolding> Holdings { get; init; } = Array.Empty<PortfolioHolding>();

    /// <summary>Операции счёта (за разумный лукбэк) — вход для правила незаинвестированного кэша (правило 4).</summary>
    public IReadOnlyList<Operation> Operations { get; init; } = Array.Empty<Operation>();

    /// <summary>TargetAllocation счёта (может быть пустым — это нормально, не ошибка). Вход для правил 6/7.</summary>
    public IReadOnlyList<TargetAllocation> TargetAllocations { get; init; } = Array.Empty<TargetAllocation>();

    /// <summary>
    /// Уже существующие непрочитанные сигналы счёта — вход для дедупликации (см. doc-comment
    /// <see cref="SignalDeduplicator"/>). Пустой список — все кандидаты считаются новыми.
    /// </summary>
    public IReadOnlyList<Signal> ExistingUnreadSignals { get; init; } = Array.Empty<Signal>();

    public SignalEngineOptions Options { get; init; } = new();
}

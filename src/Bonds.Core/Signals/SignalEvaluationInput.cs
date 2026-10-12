using Bonds.Core.Analytics;
using Bonds.Core.Models;

namespace Bonds.Core.Signals;

/// <summary>
/// Одна ОТКРЫТАЯ позиция с привязанными расписаниями — вход правил приближающихся событий
/// (оферта/погашение/амортизация/пересчёт ставки флоатера). Несёт сырые расписания (а не только
/// метрики), потому что правилам нужны конкретные даты.
/// </summary>
public sealed record SignalPositionContext
{
    public required ulong PositionId { get; init; }
    public required ulong InstrumentId { get; init; }
    public required string? Issuer { get; init; }
    public string? Name { get; init; }
    public required DateOnly MaturityDate { get; init; }

    /// <summary>Штук бумаг в позиции. Позиция с Quantity &lt;= 0 (закрытая) не порождает событий.</summary>
    public decimal Quantity { get; init; } = 1m;

    /// <summary>Тип купона инструмента. Пересчёт ставки (FloaterRateReset) — только для <see cref="CouponType.Floating"/>.</summary>
    public CouponType CouponType { get; init; } = CouponType.Fixed;

    public IReadOnlyList<CouponSchedule> Coupons { get; init; } = Array.Empty<CouponSchedule>();
    public IReadOnlyList<AmortizationSchedule> Amortizations { get; init; } = Array.Empty<AmortizationSchedule>();
    public IReadOnlyList<OfferSchedule> Offers { get; init; } = Array.Empty<OfferSchedule>();
}

/// <summary>
/// Вход для <see cref="SignalsEngine.Evaluate"/> — плоские модели, никаких репозиториев/БД/HTTP.
/// Сборка — ответственность Infrastructure (<c>SyncCycleService</c>), holdings — через
/// <c>PortfolioHoldingsBuilder</c>.
/// </summary>
public sealed record SignalEvaluationInput
{
    public required ulong AccountId { get; init; }
    public required DateOnly AsOf { get; init; }

    /// <summary>Открытые позиции с расписаниями (правила оферт/погашения/амортизации/ресета).</summary>
    public IReadOnlyList<SignalPositionContext> Positions { get; init; } = Array.Empty<SignalPositionContext>();

    /// <summary>Holdings (рыночная стоимость + эмитент) — вход правила концентрации.</summary>
    public IReadOnlyList<PortfolioHolding> Holdings { get; init; } = Array.Empty<PortfolioHolding>();

    /// <summary>
    /// Реальные свободные деньги счёта в РУБЛЯХ (<c>accounts.cash_rub</c> по данным брокера). Null —
    /// баланс ещё не синкался: правило молчит (а не выдумывает остаток по журналу).
    /// </summary>
    public decimal? CashRub { get; init; }

    /// <summary>TargetAllocation счёта (может быть пустым). Используется только для персональных лимитов концентрации.</summary>
    public IReadOnlyList<TargetAllocation> TargetAllocations { get; init; } = Array.Empty<TargetAllocation>();

    public SignalEngineOptions Options { get; init; } = new();
}

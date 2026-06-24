using Bonds.Core.Models;

namespace Bonds.Core.CashFlow;

/// <summary>
/// Вход для проекции денежного потока одной позиции (plan/06 Часть A) — агрегирует всё,
/// что <see cref="CashFlowProjectionService"/> нужно знать о позиции и её инструменте.
/// Сборка из репозиториев — ответственность вызывающего слоя (Infrastructure/API), не Core
/// (тот же принцип, что у <see cref="Bonds.Core.Calculation.BondMetricsCalculatorInput"/>).
/// </summary>
public sealed record PositionCashFlowInput
{
    public required ulong PositionId { get; init; }
    public required ulong InstrumentId { get; init; }
    public required decimal Quantity { get; init; }
    public required decimal FaceValue { get; init; }
    public required DateOnly AsOf { get; init; }
    public required DateOnly MaturityDate { get; init; }
    public required CouponType CouponType { get; init; }
    public required bool IsOutOfScopeCurrency { get; init; }

    /// <summary>
    /// Зеркалит <see cref="Instrument.DataIncomplete"/> (spec §4.4 — MOEX bondization мог вернуть
    /// не все купоны). Пересмотрено при ревью этапов 04-06: до этого исправления флаг не
    /// прокидывался в проекцию вовсе — позиция с неполным графиком купонов проецировалась как
    /// полностью надёжная (молчаливый пропуск пропавших платежей в календаре, тот самый класс
    /// ошибок, который spec §4.4 явно требует не допускать). Если true — все потоки результата
    /// помечаются <see cref="Bonds.Core.Models.ProjectedCashFlow.IsEstimated"/>=true, т.к. в графике
    /// могут отсутствовать платежи, на наличие которых пользователь не должен полагаться.
    /// </summary>
    public required bool DataIncomplete { get; init; }

    public required IReadOnlyList<CouponSchedule> Coupons { get; init; }
    public IReadOnlyList<AmortizationSchedule> Amortizations { get; init; } = Array.Empty<AmortizationSchedule>();
    public IReadOnlyList<OfferSchedule> Offers { get; init; } = Array.Empty<OfferSchedule>();

    /// <summary>
    /// Горизонт проекции: "к погашению" или "к оферте" (spec §7.3). По умолчанию —
    /// <see cref="CashFlowHorizonMode.ToNearestOffer"/>: для бумаги с офертой контрактный поток
    /// "после" put-оферты не является надёжным предположением (инвестор может предъявить бумагу
    /// к выкупу и выйти из позиции) — это та же логика, что и у движка метрик (spec §6 «Краевые
    /// случаи»: «бумага с офертой — все метрики к ближайшей оферте, не к погашению»), применённая
    /// к календарю поступлений. Если оферт нет/все исполнены/слишком близко (&lt;14 дней,
    /// см. <see cref="Bonds.Core.Calculation.OfferCutoffResolver"/>) — резолвится в погашение
    /// автоматически, так что для бумаги без оферты оба режима дают одинаковый результат.
    /// Вызывающий слой может явно запросить <see cref="CashFlowHorizonMode.ToMaturity"/>, чтобы
    /// игнорировать оферту и увидеть полный контрактный график до конца жизни бумаги.
    /// </summary>
    public CashFlowHorizonMode HorizonMode { get; init; } = CashFlowHorizonMode.ToNearestOffer;
}

/// <summary>Режим выбора горизонта проекции потока (spec §7.1, §7.3).</summary>
public enum CashFlowHorizonMode
{
    /// <summary>До даты погашения — полный поток, который рассчитан/известен на сегодня.</summary>
    ToMaturity,

    /// <summary>До ближайшей неисполненной оферты с отсечкой ≥14 дней (см. <see cref="Bonds.Core.Calculation.OfferCutoffResolver"/>); если оферт нет — до погашения.</summary>
    ToNearestOffer,
}

using Bonds.Core.Models;

namespace Bonds.Core.Calculation;

/// <summary>
/// Вход для <see cref="BondMetricsCalculator"/> — value-объект, агрегирующий всё, что движку
/// нужно знать про инструмент и его рыночную котировку на дату расчёта (plan/05: "вход —
/// модели/DTO, переданные аргументами"). Сборка этого объекта из репозиториев — ответственность
/// вызывающего слоя (этап 06/07), не движка.
/// </summary>
public sealed record BondMetricsCalculatorInput
{
    public required ulong InstrumentId { get; init; }
    public required DateOnly AsOf { get; init; }
    public required decimal FaceValue { get; init; }
    public required DateOnly MaturityDate { get; init; }
    public required CouponType CouponType { get; init; }
    public required bool HasAmortization { get; init; }
    public required bool HasOffers { get; init; }
    public required bool DataIncomplete { get; init; }

    /// <summary>Чистая цена на дату расчёта. Null, если котировка недоступна.</summary>
    public decimal? CleanPrice { get; init; }

    /// <summary>НКД из первичного источника (T-Invest/MOEX). Null — движок посчитает фолбэком.</summary>
    public decimal? AccruedInterestFromSource { get; init; }

    public required IReadOnlyList<CouponSchedule> Coupons { get; init; }
    public IReadOnlyList<AmortizationSchedule> Amortizations { get; init; } = Array.Empty<AmortizationSchedule>();
    public IReadOnlyList<OfferSchedule> Offers { get; init; } = Array.Empty<OfferSchedule>();

    /// <summary>Снимок безрисковой кривой для G-спреда. Null — G-спред не считается.</summary>
    public YieldCurveSnapshot? CurveSnapshot { get; init; }
}

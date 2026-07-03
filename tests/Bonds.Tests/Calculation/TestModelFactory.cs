using Bonds.Core.Models;

namespace Bonds.Tests.Calculation;

/// <summary>
/// Фабрика моделей для тестов движка расчётов (этап 05). Не содержит логики — только
/// упрощённое создание value-объектов модели с разумными умолчаниями, чтобы тесты
/// калькуляторов оставались короткими и читаемыми.
/// </summary>
internal static class TestModelFactory
{
    public static CouponSchedule Coupon(ulong instrumentId, DateOnly date, decimal? value, int? periodDays = null, bool isKnown = true) => new()
    {
        InstrumentId = instrumentId,
        CouponDate = date,
        ValueRub = value,
        PeriodDays = periodDays,
        IsKnown = isKnown,
    };

    public static AmortizationSchedule Amortization(ulong instrumentId, DateOnly date, decimal amount, bool isKnown = true) => new()
    {
        InstrumentId = instrumentId,
        Date = date,
        AmountRub = amount,
        IsKnown = isKnown,
    };

    public static OfferSchedule Offer(ulong instrumentId, DateOnly date, OfferType type, bool isExecuted = false) => new()
    {
        InstrumentId = instrumentId,
        Date = date,
        OfferType = type,
        IsExecuted = isExecuted,
    };

    public static YieldCurveSnapshot CurveSnapshot(
        decimal b1, decimal b2, decimal b3, decimal t1,
        decimal g1 = 0, decimal g2 = 0, decimal g3 = 0, decimal g4 = 0, decimal g5 = 0,
        decimal g6 = 0, decimal g7 = 0, decimal g8 = 0, decimal g9 = 0) => new()
    {
        B1 = b1,
        B2 = b2,
        B3 = b3,
        T1 = t1,
        G1 = g1,
        G2 = g2,
        G3 = g3,
        G4 = g4,
        G5 = g5,
        G6 = g6,
        G7 = g7,
        G8 = g8,
        G9 = g9,
    };
}

using Bonds.Core.Models;

namespace Bonds.Core.CashFlow;

/// <summary>
/// Агрегации спроецированного денежного потока для виджета-календаря (plan/06 A4, spec §7.4):
/// по месяцам и по позициям (брутто/налог/нетто), плюс отдельный список дат освобождения тела
/// (погашение/крупная амортизация) — когда в портфеле появится свободная ликвидность. Чистые
/// функции над уже построенными <see cref="ProjectedCashFlow"/> — не знает, откуда они взялись
/// (БД или <see cref="CashFlowProjectionService"/> напрямую), поэтому одинаково применим и к
/// одной позиции, и к полному портфелю счёта.
/// </summary>
public static class CashFlowAggregator
{
    /// <summary>Месячная агрегация (год+месяц) — брутто/налог/нетто, по всем переданным потокам.</summary>
    public static IReadOnlyList<MonthlyCashFlowSummary> ByMonth(IEnumerable<ProjectedCashFlow> flows)
    {
        return flows
            .GroupBy(f => new DateOnly(f.Date.Year, f.Date.Month, 1))
            .OrderBy(g => g.Key)
            .Select(g => new MonthlyCashFlowSummary
            {
                Month = g.Key,
                GrossRub = g.Sum(f => f.GrossRub),
                TaxRub = g.Sum(f => f.TaxRub),
                NetRub = g.Sum(f => f.NetRub),
                CouponGrossRub = g.Where(f => f.FlowType == CashFlowType.Coupon).Sum(f => f.GrossRub),
                PrincipalGrossRub = g.Where(f => f.FlowType != CashFlowType.Coupon).Sum(f => f.GrossRub),
                HasEstimatedFlows = g.Any(f => f.IsEstimated),
            })
            .ToList();
    }

    /// <summary>Агрегация по позициям — суммарно брутто/налог/нетто за весь горизонт проекции каждой позиции.</summary>
    public static IReadOnlyList<PositionCashFlowSummary> ByPosition(IEnumerable<ProjectedCashFlow> flows)
    {
        return flows
            .GroupBy(f => (f.PositionId, f.InstrumentId))
            .Select(g => new PositionCashFlowSummary
            {
                PositionId = g.Key.PositionId,
                InstrumentId = g.Key.InstrumentId,
                GrossRub = g.Sum(f => f.GrossRub),
                TaxRub = g.Sum(f => f.TaxRub),
                NetRub = g.Sum(f => f.NetRub),
                HasEstimatedFlows = g.Any(f => f.IsEstimated),
            })
            .OrderByDescending(s => s.NetRub)
            .ToList();
    }

    /// <summary>
    /// Даты освобождения тела (spec §7.4): погашение и амортизации, отдельно от купонов — момент,
    /// когда в портфеле появляется свободная ликвидность, которую можно реинвестировать.
    /// <paramref name="minAmountRub"/> фильтрует мелкие/частые амортизации, если нужно показать
    /// только "крупные" события (например, на сводном виджете) — по умолчанию 0 = показать все.
    /// </summary>
    public static IReadOnlyList<PrincipalReleaseEvent> PrincipalReleases(
        IEnumerable<ProjectedCashFlow> flows,
        decimal minAmountRub = 0m)
    {
        return flows
            .Where(f => f.FlowType is CashFlowType.Amortization or CashFlowType.Redemption)
            .Where(f => f.GrossRub >= minAmountRub)
            .OrderBy(f => f.Date)
            .Select(f => new PrincipalReleaseEvent
            {
                Date = f.Date,
                PositionId = f.PositionId,
                InstrumentId = f.InstrumentId,
                FlowType = f.FlowType,
                AmountRub = f.GrossRub,
                IsEstimated = f.IsEstimated,
            })
            .ToList();
    }
}

/// <summary>Сумма потоков за календарный месяц (spec §7.4, §9 «Календарь поступлений»).</summary>
public sealed record MonthlyCashFlowSummary
{
    /// <summary>Первое число месяца — ключ группировки.</summary>
    public required DateOnly Month { get; init; }
    public required decimal GrossRub { get; init; }
    public required decimal TaxRub { get; init; }
    public required decimal NetRub { get; init; }

    /// <summary>Брутто только по купонам (для разбивки виджета "купоны vs тело").</summary>
    public required decimal CouponGrossRub { get; init; }

    /// <summary>Брутто по амортизации+погашению (необлагаемая часть).</summary>
    public required decimal PrincipalGrossRub { get; init; }

    /// <summary>true — в месяце есть хотя бы один оценочный поток (флоатер/неизвестный купон).</summary>
    public required bool HasEstimatedFlows { get; init; }
}

/// <summary>Сумма потоков по одной позиции за весь горизонт проекции.</summary>
public sealed record PositionCashFlowSummary
{
    public required ulong PositionId { get; init; }
    public required ulong InstrumentId { get; init; }
    public required decimal GrossRub { get; init; }
    public required decimal TaxRub { get; init; }
    public required decimal NetRub { get; init; }
    public required bool HasEstimatedFlows { get; init; }
}

/// <summary>Дата освобождения тела (амортизация или погашение) — событие появления ликвидности.</summary>
public sealed record PrincipalReleaseEvent
{
    public required DateOnly Date { get; init; }
    public required ulong PositionId { get; init; }
    public required ulong InstrumentId { get; init; }
    public required CashFlowType FlowType { get; init; }
    public required decimal AmountRub { get; init; }
    public required bool IsEstimated { get; init; }
}

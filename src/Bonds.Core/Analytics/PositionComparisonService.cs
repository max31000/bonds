using Bonds.Core.Models;

namespace Bonds.Core.Analytics;

/// <summary>
/// Сравнение/сортировка позиций (plan/06 B3, spec §9 «Сравнение позиций (MVP — простой режим)»):
/// таблица с расчётным YTM (или текущей доходностью для флоатера/индексируемой бумаги — spec §6
/// «Краевые случаи»), дюрацией, G-спредом, днями до погашения/оферты, типом купона; сортировка
/// по доходности. Несёт обязательный дисклеймер о том, что более низкая доходность ≠ «хуже»
/// без учёта срока и риска (spec §9, §6) — отдаётся вместе с результатом, а не только в UI-тексте,
/// чтобы любой потребитель API (этап 08) получил его программно.
/// </summary>
public static class PositionComparisonService
{
    public const string YieldDisclaimer =
        "Сортировка по доходности не учитывает срок до погашения/оферты и кредитный риск эмитента. " +
        "Более низкая доходность не означает «хуже» — короткая дюрация или более надёжный эмитент " +
        "могут оправдывать меньшую доходность. Все расчёты — аналитические оценки, не инвестиционные рекомендации.";

    /// <summary>
    /// Строит таблицу сравнения, отсортированную по убыванию эффективной доходности
    /// (<see cref="ComparisonRow.EffectiveYield"/> — это YTM для обычной бумаги либо текущая
    /// купонная доходность для флоатера/индексируемой, см. <see cref="ResolveEffectiveYield"/>).
    /// Позиции без какой-либо доходности (расчёт не сошёлся/данных недостаточно) уходят в конец
    /// списка, а не отбрасываются — потребитель должен видеть, что позиция существует, но
    /// несравнима по доходности (spec §4.4 "не подставлять нули молча, не падать").
    /// </summary>
    public static PositionComparisonResult Compare(IReadOnlyList<PortfolioHolding> holdings, DateOnly asOf)
    {
        var rows = holdings
            .Select(h => ToRow(h, asOf))
            .OrderByDescending(r => r.EffectiveYield.HasValue)
            .ThenByDescending(r => r.EffectiveYield)
            .ToList();

        return new PositionComparisonResult
        {
            Rows = rows,
            Disclaimer = YieldDisclaimer,
        };
    }

    private static ComparisonRow ToRow(PortfolioHolding h, DateOnly asOf)
    {
        var isFloaterLike = h.IsFloater || h.IsIndexed;
        var effectiveYield = ResolveEffectiveYield(h);
        var daysToHorizon = h.HorizonDate.DayNumber - asOf.DayNumber;

        return new ComparisonRow
        {
            PositionId = h.PositionId,
            InstrumentId = h.InstrumentId,
            Issuer = h.Issuer,
            EffectiveYield = effectiveYield,
            YieldKind = isFloaterLike ? YieldKind.CurrentYield : YieldKind.Ytm,
            ModifiedDuration = h.ModifiedDuration,
            GSpread = h.GSpread,
            DaysToHorizon = daysToHorizon,
            HorizonDate = h.HorizonDate,
            IsCalculatedToOffer = h.IsCalculatedToOffer,
            CouponType = h.CouponType,
            IsEstimated = h.IsEstimated,
            DataIncomplete = h.DataIncomplete,
        };
    }

    /// <summary>
    /// Доходность для сортировки/сравнения: YTM для обычной бумаги, текущая купонная доходность
    /// для флоатера/индексируемой (spec §6 — YTM для них принципиально не считается движком,
    /// поэтому подстановка CurrentYield здесь не deградация, а единственно верный выбор).
    /// </summary>
    private static decimal? ResolveEffectiveYield(PortfolioHolding h) =>
        (h.IsFloater || h.IsIndexed) ? h.CurrentYield : h.YtmEffective;
}

/// <summary>Какая доходность стоит в <see cref="ComparisonRow.EffectiveYield"/> — для явной пометки в UI (spec §6 «Краевые случаи»).</summary>
public enum YieldKind
{
    Ytm,
    CurrentYield,
}

/// <summary>Итог сравнения позиций — таблица + обязательный дисклеймер (spec §9).</summary>
public sealed record PositionComparisonResult
{
    public required IReadOnlyList<ComparisonRow> Rows { get; init; }
    public required string Disclaimer { get; init; }
}

/// <summary>Одна строка таблицы сравнения позиций (spec §9, колонки: доходность/дюрация/G-спред/срок/тип купона).</summary>
public sealed record ComparisonRow
{
    public required ulong PositionId { get; init; }
    public required ulong InstrumentId { get; init; }
    public string? Issuer { get; init; }

    /// <summary>YTM или CurrentYield — см. <see cref="YieldKind"/> для различения в UI.</summary>
    public decimal? EffectiveYield { get; init; }
    public required YieldKind YieldKind { get; init; }

    public decimal? ModifiedDuration { get; init; }
    public decimal? GSpread { get; init; }

    public required int DaysToHorizon { get; init; }
    public required DateOnly HorizonDate { get; init; }
    public required bool IsCalculatedToOffer { get; init; }

    public required CouponType CouponType { get; init; }
    public required bool IsEstimated { get; init; }
    public required bool DataIncomplete { get; init; }
}

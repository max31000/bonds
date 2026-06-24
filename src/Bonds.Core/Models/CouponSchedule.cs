namespace Bonds.Core.Models;

/// <summary>
/// График купонных выплат по инструменту. Источник истины — MOEX ISS bondization
/// (spec §4.2, §5). Для флоатеров будущие купоны после ближайшего пересчёта неизвестны —
/// такие записи не создаются заранее, либо создаются с <see cref="IsKnown"/> = false
/// и без точного значения (см. spec §4.4, §6 — "Краевые случаи").
/// </summary>
public class CouponSchedule
{
    public ulong Id { get; set; }
    public ulong InstrumentId { get; set; }

    public DateOnly CouponDate { get; set; }

    /// <summary>Размер купона в рублях на один номинал. Null, если неизвестен (флоатер, далёкий горизонт).</summary>
    public decimal? ValueRub { get; set; }

    /// <summary>Длительность купонного периода в днях (нужна для НКД, этап 05).</summary>
    public int? PeriodDays { get; set; }

    /// <summary>
    /// Известно ли точное значение купона. Для фиксированного купона — всегда true.
    /// Для флоатера — true только до ближайшего пересчёта ставки.
    /// </summary>
    public bool IsKnown { get; set; } = true;

    public DateTime CreatedAt { get; set; }
}

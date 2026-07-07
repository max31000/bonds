namespace Bonds.Core.Models;

/// <summary>
/// Задача 26 часть B — дневной срез снимка вселенной облигаций (одна строка на secid в день).
/// Пишется идемпотентно один раз в день (первый тик после закрытия торгов, ~19:00 МСК) хостед-
/// сервисом <c>BondUniverseRefreshService</c>. Основа для медиан по корзинам/трендов (задача 30 B3).
/// Retention ~400 дней — старые строки чистятся при каждой записи. Единицы — те же, что
/// <see cref="BondUniverseEntry"/> (YieldFraction — доля, DurationYears — годы).
/// </summary>
public class BondUniverseHistoryPoint
{
    public DateOnly SnapshotDate { get; set; }
    public string Secid { get; set; } = string.Empty;

    public decimal? YieldFraction { get; set; }
    public decimal? DurationYears { get; set; }
    public decimal? GspreadApproxFraction { get; set; }
    public decimal? TurnoverRub { get; set; }
    public decimal? PricePercent { get; set; }
}

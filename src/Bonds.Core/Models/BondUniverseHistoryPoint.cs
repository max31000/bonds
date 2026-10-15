namespace Bonds.Core.Models;

/// <summary>
/// Дневной срез снимка вселенной облигаций (одна строка на secid в день).
/// Пишется идемпотентно один раз в день (первый тик после закрытия торгов, ~19:00 МСК) хостед-
/// сервисом <c>BondUniverseRefreshService</c>. Основа для медиан по корзинам/трендов.
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

    /// <summary>Задача 42: лучшая цена покупки, % от номинала (как <see cref="BondUniverseEntry.BidPercent"/>).</summary>
    public decimal? Bid { get; set; }

    /// <summary>Задача 42: лучшая цена продажи, % от номинала (как <see cref="BondUniverseEntry.OfferPercent"/>).</summary>
    public decimal? Offer { get; set; }

    /// <summary>Задача 42: уровень листинга на дату среза (1/2/3).</summary>
    public int? ListLevel { get; set; }

    /// <summary>Задача 42: <see cref="BondUniverseEntry.EffectiveGspreadFraction"/> на момент среза
    /// (биржевой G-спред, а при его отсутствии — приближение), ДОЛЯ. Null в строках, записанных до
    /// миграции 017 — для них читать <see cref="GspreadApproxFraction"/>.</summary>
    public decimal? GspreadEffectiveFraction { get; set; }

    /// <summary>G-спред дня для статистики корзин: <see cref="GspreadEffectiveFraction"/>, а для старых
    /// строк (до миграции 017) — <see cref="GspreadApproxFraction"/>. ДОЛЯ.</summary>
    public decimal? ResolvedGspreadFraction => GspreadEffectiveFraction ?? GspreadApproxFraction;
}

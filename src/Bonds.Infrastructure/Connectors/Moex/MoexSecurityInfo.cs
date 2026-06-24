using Bonds.Core.Models;

namespace Bonds.Infrastructure.Connectors.Moex;

/// <summary>
/// Параметры выпуска, разобранные из MOEX ISS
/// <c>/iss/engines/stock/markets/bonds/securities/{SECID}.json</c> (plan/04 Часть A, §4.2 таблица).
/// Один SECID может торговаться на нескольких BOARDID одновременно (например, основной режим
/// торгов и режим переговорных сделок) — резолвер берёт строку с самым "торговым" board
/// (см. <see cref="MoexIssClient"/>), здесь хранится уже выбранная строка.
/// </summary>
public sealed class MoexSecurityInfo
{
    public required string Secid { get; init; }
    public required string BoardId { get; init; }
    public string? Isin { get; init; }
    public string? ShortName { get; init; }
    public string? SecName { get; init; }

    public decimal? FaceValue { get; init; }
    public string? FaceUnit { get; init; }
    public DateOnly? MatDate { get; init; }
    public decimal? CouponPercent { get; init; }
    public int? CouponPeriod { get; init; }
    public DateOnly? NextCoupon { get; init; }
    public decimal? AccruedInterest { get; init; }
    public decimal? PrevPrice { get; init; }
    public decimal? PrevWaPrice { get; init; }

    /// <summary>
    /// Человекочитаемая классификация ISS, например "Фикс с известным купоном", "Флоатер",
    /// "Амортизируемые облигации". Используется как один из сигналов для <see cref="CouponType"/> /
    /// <see cref="HasAmortizationHint"/> наряду с фактическим bondization-графиком (последний приоритетнее).
    /// </summary>
    public string? BondType { get; init; }

    public DateOnly? OfferDate { get; init; }
    public DateOnly? CallOptionDate { get; init; }
    public DateOnly? PutOptionDate { get; init; }

    /// <summary>Эвристика по полю BONDTYPE/COUPONPERCENT — финальное решение принимает резолвер
    /// на основании bondization (фактического графика), это лишь подсказка.</summary>
    public bool LooksLikeFloater => (BondType?.Contains("Флоатер", StringComparison.OrdinalIgnoreCase) ?? false)
        || (CouponPercent is null && NextCoupon is not null);

    public bool HasAmortizationHint => BondType?.Contains("Амортиз", StringComparison.OrdinalIgnoreCase) ?? false;
}

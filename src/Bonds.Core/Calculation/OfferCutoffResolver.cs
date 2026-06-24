using Bonds.Core.Models;

namespace Bonds.Core.Calculation;

/// <summary>
/// Определение горизонта расчёта метрик: ближайшая НЕисполненная оферта (с отсечкой по сроку,
/// spec §7.3) или погашение, если подходящих оферт нет. Вынесено в отдельный реюзабельный класс,
/// т.к. логика отсечки нужна не только этапу 05 (YTM/дюрация к оферте), но и этапу 06
/// (проекция денежного потока — plan/05 Часть A.4, явно требует переиспользования).
/// </summary>
public static class OfferCutoffResolver
{
    /// <summary>
    /// Минимальное число календарных дней до оферты, чтобы она считалась релевантным горизонтом
    /// расчёта (spec §7.3: "конвенция, как в профильных калькуляторах"). Оферты ближе этого порога
    /// игнорируются — берётся следующая неисполненная оферта или погашение.
    /// </summary>
    public const int MinDaysToOffer = 14;

    /// <summary>
    /// Результат разрешения горизонта: дата отсечки и признак того, что это оферта (а не погашение).
    /// </summary>
    public readonly record struct Horizon(DateOnly Date, bool IsOffer, OfferType? OfferType);

    /// <summary>
    /// Выбирает горизонт расчёта на дату <paramref name="asOf"/>: ближайшую неисполненную оферту,
    /// до которой остаётся не менее <see cref="MinDaysToOffer"/> календарных дней, иначе — следующую
    /// по очереди неисполненную оферту, удовлетворяющую отсечке, иначе — дату погашения.
    /// </summary>
    /// <param name="asOf">Дата, на которую считается метрика (обычно — дата котировки).</param>
    /// <param name="maturityDate">Дата погашения инструмента.</param>
    /// <param name="offers">График оферт инструмента (может быть пустым/null).</param>
    public static Horizon Resolve(DateOnly asOf, DateOnly maturityDate, IEnumerable<OfferSchedule>? offers)
    {
        var candidate = offers?
            .Where(o => !o.IsExecuted)
            .Where(o => o.Date >= asOf)
            .Where(o => (o.Date.ToDateTime(TimeOnly.MinValue) - asOf.ToDateTime(TimeOnly.MinValue)).Days >= MinDaysToOffer)
            .OrderBy(o => o.Date)
            .FirstOrDefault();

        if (candidate is { } offer)
        {
            return new Horizon(offer.Date, true, offer.OfferType);
        }

        return new Horizon(maturityDate, false, null);
    }
}

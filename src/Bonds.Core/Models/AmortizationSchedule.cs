namespace Bonds.Core.Models;

/// <summary>
/// График амортизационных выплат (частичный возврат номинала). Источник истины — MOEX ISS
/// (T-Invest по облигациям обычно отдаёт только флаг наличия амортизации, без полного
/// графика — spec §4.1). Убывающий номинал учитывается дальше движком расчётов (этап 05).
/// </summary>
public class AmortizationSchedule
{
    public ulong Id { get; set; }
    public ulong InstrumentId { get; set; }

    public DateOnly Date { get; set; }

    /// <summary>
    /// Сумма погашения номинала в рублях на одну облигацию. Если <see cref="IsKnown"/> = false
    /// (см. ниже), значение не является достоверным — обычно 0m-заглушка, не подставляем
    /// молча реальное число (spec §4.4, Audit(engine) E-1).
    /// </summary>
    public decimal AmountRub { get; set; }

    /// <summary>
    /// Известна ли точная сумма амортизации на эту дату. False — MOEX вернул дату
    /// (<c>amortdate</c>), но не сумму (<c>value_rub=null</c>) — реалистично для ипотечных
    /// агентов/MBS-подобных бумаг, где будущая амортизация зависит от досрочных погашений
    /// пулов и непредсказуема (Audit(engine) E-1). Зеркалит <see cref="CouponSchedule.IsKnown"/>.
    /// Для купонных/муниципальных облигаций с полностью известным графиком — всегда true.
    /// </summary>
    public bool IsKnown { get; set; } = true;

    public DateTime CreatedAt { get; set; }
}

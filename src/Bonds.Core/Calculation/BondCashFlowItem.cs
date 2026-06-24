namespace Bonds.Core.Calculation;

/// <summary>
/// Один денежный поток будущего расчётного горизонта бумаги (купон и/или возврат номинала
/// на дату). Value-объект — вход/выход чистых калькуляторов этапа 05 (plan/05 Часть A/C),
/// без ссылок на репозитории.
/// </summary>
/// <param name="Date">Дата поступления.</param>
/// <param name="CouponAmount">Купон на одну облигацию (0, если в эту дату купона нет).</param>
/// <param name="PrincipalAmount">Возврат номинала на одну облигацию: амортизация и/или погашение/оферта (0, если нет).</param>
/// <param name="IsKnown">Известна ли точная сумма (false — будущий купон флоатера/индексируемой бумаги).</param>
public readonly record struct BondCashFlowItem(
    DateOnly Date,
    decimal CouponAmount,
    decimal PrincipalAmount,
    bool IsKnown)
{
    /// <summary>Суммарный поток на дату (купон + номинал).</summary>
    public decimal TotalAmount => CouponAmount + PrincipalAmount;
}

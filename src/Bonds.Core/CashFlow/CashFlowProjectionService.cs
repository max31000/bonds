using Bonds.Core.Calculation;
using Bonds.Core.Models;

namespace Bonds.Core.CashFlow;

/// <summary>
/// Cash-Flow Projection (plan/06 Часть A, spec §7) — строит персональный календарь будущих
/// поступлений по позиции: купоны × количество, амортизации, погашение тела, с НДФЛ 13% на
/// купонный доход. Чистый сервис без I/O — переиспользует <see cref="BondCashFlowBuilder"/> и
/// <see cref="OfferCutoffResolver"/> движка (этап 05), не дублирует их логику. Сборка входа из
/// репозиториев и персистентность результата (<see cref="Bonds.Core.Interfaces.Repositories.IProjectedCashFlowRepository"/>)
/// — ответственность вызывающего слоя (Infrastructure/API), не этого класса.
/// </summary>
public static class CashFlowProjectionService
{
    /// <summary>НДФЛ на купонный доход (spec §7.2). Применяется только к <see cref="CashFlowType.Coupon"/>.</summary>
    public const decimal CouponTaxRate = 0.13m;

    /// <summary>
    /// Строит проекцию по одной позиции. Возвращает пустой список для бумаг вне скоупа валюты
    /// (<see cref="Instrument.IsOutOfScopeCurrency"/>, spec §11/§3 "вне скоупа") — такие позиции
    /// не участвуют в рублёвой проекции, но не падают и не искажают агрегаты молчаливыми нулями.
    /// </summary>
    public static IReadOnlyList<ProjectedCashFlow> Project(PositionCashFlowInput input)
    {
        if (input.IsOutOfScopeCurrency)
        {
            return Array.Empty<ProjectedCashFlow>();
        }

        var horizon = ResolveHorizon(input);

        var items = BondCashFlowBuilder.Build(
            input.FaceValue,
            input.AsOf,
            horizon,
            input.Coupons,
            input.Amortizations);

        var isFloaterLike = input.CouponType is CouponType.Floating or CouponType.Indexed;

        var result = new List<ProjectedCashFlow>();
        var now = DateTime.UtcNow;

        foreach (var item in items)
        {
            // Флоатер/индексируемая бумага — оценка по текущей ставке (spec §6/§7.1); купон с
            // неизвестной точной суммой (IsKnown=false) тоже помечается оценочным независимо
            // от типа купона — это та же логика деградации, что у движка этапа 05.
            var isEstimated = isFloaterLike || !item.IsKnown;

            if (item.CouponAmount != 0m)
            {
                var gross = item.CouponAmount * input.Quantity;
                var tax = Math.Round(gross * CouponTaxRate, 2, MidpointRounding.AwayFromZero);
                var net = gross - tax;

                result.Add(new ProjectedCashFlow
                {
                    PositionId = input.PositionId,
                    InstrumentId = input.InstrumentId,
                    Date = item.Date,
                    FlowType = CashFlowType.Coupon,
                    GrossRub = gross,
                    TaxRub = tax,
                    NetRub = net,
                    IsEstimated = isEstimated,
                    CreatedAt = now,
                });
            }

            if (item.PrincipalAmount != 0m)
            {
                // Амортизация/погашение тела НЕ облагается купонным НДФЛ (spec §7.2) — налог = 0,
                // нетто = брутто. Различаем амортизацию (промежуточная дата) от погашения/оферты
                // (дата горизонта) по совпадению с датой горизонта; на дату горизонта всегда
                // финальный возврат остатка (см. doc-comment BondCashFlowBuilder).
                var flowType = item.Date == horizon ? CashFlowType.Redemption : CashFlowType.Amortization;
                var gross = item.PrincipalAmount * input.Quantity;

                result.Add(new ProjectedCashFlow
                {
                    PositionId = input.PositionId,
                    InstrumentId = input.InstrumentId,
                    Date = item.Date,
                    FlowType = flowType,
                    GrossRub = gross,
                    TaxRub = 0m,
                    NetRub = gross,
                    IsEstimated = isEstimated,
                    CreatedAt = now,
                });
            }
        }

        return result;
    }

    /// <summary>
    /// Резолвит дату верхней границы горизонта проекции согласно <see cref="PositionCashFlowInput.HorizonMode"/>.
    /// "К погашению" — формально horizon = MaturityDate, без учёта оферт ("полный контрактный
    /// график", не "точно произойдёт"). "К оферте" (дефолт) — использует <see cref="OfferCutoffResolver"/>,
    /// не дублируя логику отсечки §7.3; при отсутствии релевантной оферты резолвится в погашение.
    /// </summary>
    private static DateOnly ResolveHorizon(PositionCashFlowInput input)
    {
        if (input.HorizonMode == CashFlowHorizonMode.ToMaturity)
        {
            return input.MaturityDate;
        }

        var resolved = OfferCutoffResolver.Resolve(input.AsOf, input.MaturityDate, input.Offers);
        return resolved.Date;
    }
}

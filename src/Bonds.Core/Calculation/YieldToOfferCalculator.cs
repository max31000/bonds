using Bonds.Core.Models;

namespace Bonds.Core.Calculation;

/// <summary>
/// Доходность к ближайшей неисполненной оферте (spec §6.4, §7.3). Переиспользует
/// <see cref="OfferCutoffResolver"/> для выбора горизонта и <see cref="YtmCalculator"/> для
/// собственно решения IRR на потоке до этого горизонта (поток для бумаги с офертой обрезается
/// на дате оферты, а "номинал" на эту дату — это сумма выкупа, по конвенции MVP равная номиналу
/// одной облигации, т.е. оферта трактуется как полное погашение по номиналу на дату оферты —
/// общепринятое упрощение, реальная оферта почти всегда "по номиналу"; если в будущем появятся
/// данные о выкупе не по номиналу, потребуется доработка модели OfferSchedule).
/// </summary>
public static class YieldToOfferCalculator
{
    public readonly record struct Result(
        YtmCalculator.YtmResult Yield,
        OfferCutoffResolver.Horizon Horizon,
        List<BondCashFlowItem> CashFlow);

    /// <summary>
    /// Считает доходность к оферте. Возвращает null, если у бумаги нет неисполненных оферт,
    /// удовлетворяющих отсечке §7.3 (в этом случае горизонт — погашение, и вызывающий слой должен
    /// использовать обычный <see cref="YtmCalculator"/>/<see cref="OfferCutoffResolver"/> сам —
    /// этот метод явно работает только когда горизонт оказался офертой).
    /// </summary>
    public static Result? Calculate(
        decimal faceValue,
        decimal dirtyPrice,
        DateOnly asOf,
        DateOnly maturityDate,
        IEnumerable<CouponSchedule> coupons,
        IEnumerable<AmortizationSchedule>? amortizations,
        IEnumerable<OfferSchedule>? offers)
    {
        var horizon = OfferCutoffResolver.Resolve(asOf, maturityDate, offers);
        if (!horizon.IsOffer) return null;

        var cashFlow = BondCashFlowBuilder.Build(faceValue, asOf, horizon.Date, coupons, amortizations);
        var ytm = YtmCalculator.Calculate(dirtyPrice, asOf, cashFlow);
        if (ytm is null) return null;

        return new Result(ytm.Value, horizon, cashFlow);
    }
}

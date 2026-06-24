using Bonds.Core.CashFlow;
using Bonds.Core.Models;
using Bonds.Tests.Calculation;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.CashFlow;

/// <summary>
/// Тесты проекции денежного потока по позиции (plan/06 Часть A, spec §7). Переиспользует
/// <see cref="TestModelFactory"/> из тестов движка этапа 05 — те же value-объекты модели.
/// </summary>
public class CashFlowProjectionServiceTests
{
    private const ulong InstrumentId = 1;
    private const ulong PositionId = 100;
    private static readonly DateOnly AsOf = new(2025, 1, 1);

    private static PositionCashFlowInput BaseInput(
        decimal quantity,
        decimal faceValue,
        DateOnly maturity,
        IReadOnlyList<CouponSchedule> coupons,
        IReadOnlyList<AmortizationSchedule>? amortizations = null,
        IReadOnlyList<OfferSchedule>? offers = null,
        CouponType couponType = CouponType.Fixed,
        bool isOutOfScopeCurrency = false,
        bool dataIncomplete = false,
        CashFlowHorizonMode horizonMode = CashFlowHorizonMode.ToNearestOffer) => new()
    {
        PositionId = PositionId,
        InstrumentId = InstrumentId,
        Quantity = quantity,
        FaceValue = faceValue,
        AsOf = AsOf,
        MaturityDate = maturity,
        CouponType = couponType,
        IsOutOfScopeCurrency = isOutOfScopeCurrency,
        DataIncomplete = dataIncomplete,
        Coupons = coupons,
        Amortizations = amortizations ?? Array.Empty<AmortizationSchedule>(),
        Offers = offers ?? Array.Empty<OfferSchedule>(),
        HorizonMode = horizonMode,
    };

    [Fact]
    public void Project_FixedCouponBond_MultipliesByQuantityAndAppliesCouponTaxOnly()
    {
        var maturity = AsOf.AddDays(365);
        var coupons = new[] { TestModelFactory.Coupon(InstrumentId, maturity, 100m) };

        var input = BaseInput(quantity: 10, faceValue: 1000m, maturity, coupons);
        var flows = CashFlowProjectionService.Project(input);

        flows.Should().HaveCount(2, "купон и погашение тела на дату погашения — два отдельных потока");

        var coupon = flows.Single(f => f.FlowType == CashFlowType.Coupon);
        coupon.GrossRub.Should().Be(1000m, "100 * 10 облигаций");
        coupon.TaxRub.Should().Be(130m, "13% НДФЛ на купон");
        coupon.NetRub.Should().Be(870m);
        coupon.IsEstimated.Should().BeFalse();

        var redemption = flows.Single(f => f.FlowType == CashFlowType.Redemption);
        redemption.GrossRub.Should().Be(10000m, "1000 номинал * 10 облигаций");
        redemption.TaxRub.Should().Be(0m, "возврат номинала не облагается НДФЛ (spec §7.2)");
        redemption.NetRub.Should().Be(10000m);
    }

    [Fact]
    public void Project_AmortizingBond_TaxesOnlyCouponNotPrincipalReturn()
    {
        var maturity = AsOf.AddDays(730);
        var amortDate = AsOf.AddDays(365);

        var coupons = new[]
        {
            TestModelFactory.Coupon(InstrumentId, amortDate, 100m),
            TestModelFactory.Coupon(InstrumentId, maturity, 50m),
        };
        var amortizations = new[] { TestModelFactory.Amortization(InstrumentId, amortDate, 500m) };

        var input = BaseInput(quantity: 5, faceValue: 1000m, maturity, coupons, amortizations);
        var flows = CashFlowProjectionService.Project(input);

        // 4 потока: купон+амортизация на amortDate, купон+погашение остатка на maturity.
        flows.Should().HaveCount(4);

        var amortFlow = flows.Single(f => f.Date == amortDate && f.FlowType == CashFlowType.Amortization);
        amortFlow.GrossRub.Should().Be(2500m, "500 * 5 облигаций");
        amortFlow.TaxRub.Should().Be(0m, "амортизация (возврат номинала) не облагается НДФЛ");
        amortFlow.NetRub.Should().Be(2500m);

        var couponAtAmortDate = flows.Single(f => f.Date == amortDate && f.FlowType == CashFlowType.Coupon);
        couponAtAmortDate.GrossRub.Should().Be(500m, "100 * 5");
        couponAtAmortDate.TaxRub.Should().Be(65m, "13% от 500");

        var redemption = flows.Single(f => f.Date == maturity && f.FlowType == CashFlowType.Redemption);
        redemption.GrossRub.Should().Be(2500m, "остаток номинала 500 * 5 облигаций");
        redemption.TaxRub.Should().Be(0m);

        var couponAtMaturity = flows.Single(f => f.Date == maturity && f.FlowType == CashFlowType.Coupon);
        couponAtMaturity.TaxRub.Should().Be(32.5m, "13% от 250 (50 * 5)");
    }

    [Fact]
    public void Project_Floater_MarksAllFlowsAsEstimated()
    {
        var maturity = AsOf.AddDays(365);
        var coupons = new[] { TestModelFactory.Coupon(InstrumentId, maturity, 80m) };

        var input = BaseInput(quantity: 1, faceValue: 1000m, maturity, coupons, couponType: CouponType.Floating);
        var flows = CashFlowProjectionService.Project(input);

        flows.Should().NotBeEmpty();
        flows.Should().OnlyContain(f => f.IsEstimated, "флоатер — все потоки оценочные (spec §7.1)");
    }

    [Fact]
    public void Project_InstrumentDataIncomplete_MarksAllFlowsAsEstimated()
    {
        // Регрессия для бага, найденного при ревью этапов 04-06: Instrument.DataIncomplete
        // (spec §4.4 — MOEX bondization мог вернуть не все купоны) раньше не прокидывался в
        // проекцию вовсе. Пропавший в "дырке" графика купон отсутствует в Coupons целиком (не
        // присутствует с IsKnown=false), поэтому сам по себе не помечает поток оценочным — без
        // явного DataIncomplete календарь выглядел бы полным там, где платежи могут отсутствовать.
        var maturity = AsOf.AddDays(365);
        var coupons = new[] { TestModelFactory.Coupon(InstrumentId, maturity, 80m) };

        var input = BaseInput(quantity: 1, faceValue: 1000m, maturity, coupons, dataIncomplete: true);
        var flows = CashFlowProjectionService.Project(input);

        flows.Should().NotBeEmpty();
        flows.Should().OnlyContain(f => f.IsEstimated,
            "инструмент помечен DataIncomplete — календарь может быть неполным, доверять нельзя");
    }

    [Fact]
    public void Project_UnknownFutureCoupon_MarksItemAsEstimatedEvenForFixedCoupon()
    {
        var maturity = AsOf.AddDays(365);
        var coupons = new[] { TestModelFactory.Coupon(InstrumentId, maturity, null, isKnown: false) };

        var input = BaseInput(quantity: 1, faceValue: 0m, maturity, coupons);
        var flows = CashFlowProjectionService.Project(input);

        // Купон с неизвестной суммой (ValueRub=null) даёт нулевой денежный поток у движка —
        // в проекции это означает отсутствие потока на эту дату (FaceValue=0 => нет погашения).
        flows.Should().BeEmpty();
    }

    [Fact]
    public void Project_BondWithEligibleOffer_CutsOffAtOfferNotMaturity()
    {
        var maturity = AsOf.AddYears(10);
        var offerDate = AsOf.AddDays(180);
        var coupons = new[]
        {
            TestModelFactory.Coupon(InstrumentId, offerDate, 40m),
            TestModelFactory.Coupon(InstrumentId, maturity, 40m), // далеко за горизонтом оферты — не должен попасть в проекцию
        };
        var offers = new[] { TestModelFactory.Offer(InstrumentId, offerDate, OfferType.Put) };

        var input = BaseInput(quantity: 1, faceValue: 1000m, maturity, coupons, offers: offers);
        var flows = CashFlowProjectionService.Project(input);

        flows.Should().OnlyContain(f => f.Date <= offerDate, "отсечка по оферте §7.3 — потоки после оферты не проецируются");
        flows.Should().Contain(f => f.FlowType == CashFlowType.Redemption && f.Date == offerDate,
            "на дате оферты — возврат номинала, как если бы бумага была предъявлена к выкупу");
    }

    [Fact]
    public void Project_OfferTooCloseToCutoff_FallsBackToMaturity()
    {
        var maturity = AsOf.AddDays(365);
        var tooCloseOffer = AsOf.AddDays(10); // < 14 дней — отсечка игнорирует (spec §7.3)
        var coupons = new[] { TestModelFactory.Coupon(InstrumentId, maturity, 50m) };
        var offers = new[] { TestModelFactory.Offer(InstrumentId, tooCloseOffer, OfferType.Put) };

        var input = BaseInput(quantity: 1, faceValue: 1000m, maturity, coupons, offers: offers);
        var flows = CashFlowProjectionService.Project(input);

        flows.Should().Contain(f => f.Date == maturity && f.FlowType == CashFlowType.Redemption);
    }

    [Fact]
    public void Project_OutOfScopeCurrency_ReturnsEmpty()
    {
        var maturity = AsOf.AddDays(365);
        var coupons = new[] { TestModelFactory.Coupon(InstrumentId, maturity, 100m) };

        var input = BaseInput(quantity: 10, faceValue: 1000m, maturity, coupons, isOutOfScopeCurrency: true);
        var flows = CashFlowProjectionService.Project(input);

        flows.Should().BeEmpty("бумаги вне скоупа валюты исключаются из рублёвой проекции (spec §3/§11)");
    }

    [Fact]
    public void Project_ToMaturityMode_IgnoresOfferAndProjectsToMaturity()
    {
        var maturity = AsOf.AddYears(5);
        var offerDate = AsOf.AddDays(180);
        var coupons = new[]
        {
            TestModelFactory.Coupon(InstrumentId, offerDate, 40m),
            TestModelFactory.Coupon(InstrumentId, maturity, 40m),
        };
        var offers = new[] { TestModelFactory.Offer(InstrumentId, offerDate, OfferType.Put) };

        var input = BaseInput(quantity: 1, faceValue: 1000m, maturity, coupons, offers: offers,
            horizonMode: CashFlowHorizonMode.ToMaturity);
        var flows = CashFlowProjectionService.Project(input);

        flows.Should().Contain(f => f.Date == maturity, "ToMaturity явно игнорирует оферту");
    }
}

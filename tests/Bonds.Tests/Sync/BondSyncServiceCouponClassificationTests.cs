using Bonds.Core.Models;
using Bonds.Infrastructure.Sync;
using Bonds.Tests.Calculation;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Sync;

/// <summary>
/// Задача 36: правило классификации <see cref="CouponType.Floating"/> в
/// <see cref="BondSyncService.HasFloatingCoupon"/>. Эталоны — синтетические расписания, посчитаны
/// вручную (сеть не нужна). Кейс 1 воспроизводит живой баг из прода: Брус 2Р06 (RU000A10EC63) —
/// фикс-купон, put-оферта, неизвестные купоны только ПОСЛЕ оферты (ставка пересматривается на
/// оферте) ложно классифицировались как Floating. См. doc-comment
/// <see cref="BondSyncService.HasFloatingCoupon"/> и plan/36.
/// </summary>
public class BondSyncServiceCouponClassificationTests
{
    private const ulong InstrumentId = 1;
    private static readonly DateOnly AsOf = new(2026, 7, 10);
    private static readonly DateOnly Maturity = new(2031, 7, 10);

    [Fact]
    public void FixedBond_WithOffer_UnknownCouponsOnlyAfterOffer_IsFixed()
    {
        // Кейс Брус 2Р06: известные купоны до оферты включительно (в т.ч. купон РОВНО на дату
        // оферты), после оферты — неизвестны (ставка будет пересмотрена на оферте, стандарт для
        // put-оферт). Это НЕ флоатер.
        var offerDate = AsOf.AddMonths(6);
        var offers = new[] { TestModelFactory.Offer(InstrumentId, offerDate, OfferType.Put) };
        var coupons = new[]
        {
            TestModelFactory.Coupon(InstrumentId, AsOf.AddMonths(3), 35m),
            TestModelFactory.Coupon(InstrumentId, offerDate, 35m),
            TestModelFactory.Coupon(InstrumentId, offerDate.AddMonths(3), null, isKnown: false),
            TestModelFactory.Coupon(InstrumentId, offerDate.AddMonths(6), null, isKnown: false),
        };

        var result = BondSyncService.HasFloatingCoupon(coupons, offers, AsOf, Maturity, looksLikeFloater: false);

        result.Should().BeFalse("неизвестные купоны строго после ближайшей оферты — норма пересмотра ставки, не флоатер");
    }

    [Fact]
    public void UnknownCoupon_BeforeNearestOffer_IsFloating()
    {
        var offerDate = AsOf.AddMonths(6);
        var offers = new[] { TestModelFactory.Offer(InstrumentId, offerDate, OfferType.Put) };
        var coupons = new[]
        {
            TestModelFactory.Coupon(InstrumentId, AsOf.AddMonths(3), null, isKnown: false),
            TestModelFactory.Coupon(InstrumentId, offerDate.AddMonths(3), null, isKnown: false),
        };

        var result = BondSyncService.HasFloatingCoupon(coupons, offers, AsOf, Maturity, looksLikeFloater: false);

        result.Should().BeTrue("неизвестный купон ДО оферты — настоящий флоатер, ставка не зафиксирована заранее");
    }

    [Fact]
    public void UnknownCoupon_OnOfferDate_IsFloating_BoundaryInclusive()
    {
        // Граница: купон на дату самой оферты ещё обязан быть известен (это последний купон
        // текущего периода). Если он неизвестен — это флоатер, а не "норма после оферты".
        var offerDate = AsOf.AddMonths(6);
        var offers = new[] { TestModelFactory.Offer(InstrumentId, offerDate, OfferType.Put) };
        var coupons = new[]
        {
            TestModelFactory.Coupon(InstrumentId, offerDate, null, isKnown: false),
        };

        var result = BondSyncService.HasFloatingCoupon(coupons, offers, AsOf, Maturity, looksLikeFloater: false);

        result.Should().BeTrue("купон РОВНО на дату оферты ещё обязан быть известен — граница включительная");
    }

    [Fact]
    public void NoOffers_UnknownCoupon_IsFloating_PreviousBehaviourRegression()
    {
        var coupons = new[]
        {
            TestModelFactory.Coupon(InstrumentId, AsOf.AddMonths(3), 35m),
            TestModelFactory.Coupon(InstrumentId, AsOf.AddMonths(6), null, isKnown: false),
        };

        var result = BondSyncService.HasFloatingCoupon(coupons, offers: [], AsOf, Maturity, looksLikeFloater: false);

        result.Should().BeTrue("без оферт неизвестный купон — прежнее поведение, регресс недопустим");
    }

    [Fact]
    public void AllCouponsKnown_WithOffer_IsFixed()
    {
        var offerDate = AsOf.AddMonths(6);
        var offers = new[] { TestModelFactory.Offer(InstrumentId, offerDate, OfferType.Put) };
        var coupons = new[]
        {
            TestModelFactory.Coupon(InstrumentId, AsOf.AddMonths(3), 35m),
            TestModelFactory.Coupon(InstrumentId, offerDate, 35m),
            TestModelFactory.Coupon(InstrumentId, offerDate.AddMonths(3), 35m),
        };

        var result = BondSyncService.HasFloatingCoupon(coupons, offers, AsOf, Maturity, looksLikeFloater: false);

        result.Should().BeFalse();
    }

    [Fact]
    public void LooksLikeFloaterHeuristic_AllCouponsKnown_StillFloating()
    {
        // BONDTYPE-сигнал (securities.json "Флоатер" / отсутствие COUPONPERCENT) остаётся
        // самостоятельным триггером — единственный способ узнать настоящий флоатер, у которого
        // ближайшие купоны ещё известны (горизонт пересчёта ставки далеко впереди).
        var coupons = new[]
        {
            TestModelFactory.Coupon(InstrumentId, AsOf.AddMonths(3), 35m),
        };

        var result = BondSyncService.HasFloatingCoupon(coupons, offers: [], AsOf, Maturity, looksLikeFloater: true);

        result.Should().BeTrue("BONDTYPE-эвристика остаётся сигналом сама по себе, OR с расписанием купонов");
    }

    [Fact]
    public void PastOffer_UnknownCouponsAfter_TreatedAsNoOffer_IsFloating()
    {
        // Оферта уже прошла (OfferCutoffResolver её не выберет — Date < asOf) — резолвер
        // возвращает горизонт погашения (IsOffer=false), правило откатывается к "оферты нет":
        // прошедшая оферта не оправдывает неизвестные купоны после неё.
        var pastOfferDate = AsOf.AddMonths(-1);
        var offers = new[] { TestModelFactory.Offer(InstrumentId, pastOfferDate, OfferType.Put) };
        var coupons = new[]
        {
            TestModelFactory.Coupon(InstrumentId, AsOf.AddMonths(3), null, isKnown: false),
        };

        var result = BondSyncService.HasFloatingCoupon(coupons, offers, AsOf, Maturity, looksLikeFloater: false);

        result.Should().BeTrue("прошедшая оферта не оправдывает неизвестные купоны после неё — трактуется как отсутствие оферты");
    }
}

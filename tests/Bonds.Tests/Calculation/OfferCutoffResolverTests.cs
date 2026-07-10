using Bonds.Core.Calculation;
using Bonds.Core.Models;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Calculation;

/// <summary>
/// Тесты отсечки по оферте (spec §7.3, plan/05 Часть A.4/B/D). Этот класс переиспользуется
/// этапом 06 для проекции денежного потока — критично проверить граничные случаи отсечки
/// в 14 календарных дней явно.
/// </summary>
public class OfferCutoffResolverTests
{
    private const ulong InstrumentId = 1;
    private static readonly DateOnly AsOf = new(2025, 1, 1);
    private static readonly DateOnly Maturity = new(2030, 1, 1);

    [Fact]
    public void Resolve_NoOffers_ReturnsMaturity()
    {
        var horizon = OfferCutoffResolver.Resolve(AsOf, Maturity, offers: null);

        horizon.IsOffer.Should().BeFalse();
        horizon.Date.Should().Be(Maturity);
    }

    [Fact]
    public void Resolve_ExecutedOffer_IsIgnored()
    {
        var executed = TestModelFactory.Offer(InstrumentId, AsOf.AddDays(30), OfferType.Put, isExecuted: true);

        var horizon = OfferCutoffResolver.Resolve(AsOf, Maturity, new[] { executed });

        horizon.IsOffer.Should().BeFalse();
        horizon.Date.Should().Be(Maturity);
    }

    [Fact]
    public void Resolve_OfferExactlyAtCutoffBoundary_IsIncluded()
    {
        // Ровно 14 дней — граница включается (">= MinDaysToOffer").
        var offer = TestModelFactory.Offer(InstrumentId, AsOf.AddDays(OfferCutoffResolver.MinDaysToOffer), OfferType.Put);

        var horizon = OfferCutoffResolver.Resolve(AsOf, Maturity, new[] { offer });

        horizon.IsOffer.Should().BeTrue();
        horizon.Date.Should().Be(offer.Date);
    }

    [Fact]
    public void Resolve_OfferCloserThan14Days_IsIgnored_FallsBackToMaturityWhenNoOtherOffer()
    {
        var tooClose = TestModelFactory.Offer(InstrumentId, AsOf.AddDays(13), OfferType.Put);

        var horizon = OfferCutoffResolver.Resolve(AsOf, Maturity, new[] { tooClose });

        horizon.IsOffer.Should().BeFalse("оферта ближе 14 дней должна игнорироваться (spec §7.3)");
        horizon.Date.Should().Be(Maturity);
    }

    [Fact]
    public void Resolve_OfferCloserThan14Days_SkipsToNextEligibleOffer()
    {
        var tooClose = TestModelFactory.Offer(InstrumentId, AsOf.AddDays(5), OfferType.Put);
        var eligible = TestModelFactory.Offer(InstrumentId, AsOf.AddDays(180), OfferType.Call);

        var horizon = OfferCutoffResolver.Resolve(AsOf, Maturity, new[] { tooClose, eligible });

        horizon.IsOffer.Should().BeTrue();
        horizon.Date.Should().Be(eligible.Date);
        horizon.OfferType.Should().Be(OfferType.Call);
    }

    [Fact]
    public void Resolve_MultipleEligibleOffers_PicksClosestOne()
    {
        var nearer = TestModelFactory.Offer(InstrumentId, AsOf.AddDays(90), OfferType.Put);
        var farther = TestModelFactory.Offer(InstrumentId, AsOf.AddDays(365), OfferType.Put);

        var horizon = OfferCutoffResolver.Resolve(AsOf, Maturity, new[] { farther, nearer });

        horizon.Date.Should().Be(nearer.Date);
    }

    // Ревью T-36: ResolveNearestOfferDate — "ближайшая оферта по календарю", без отсечки
    // MinDaysToOffer (в отличие от Resolve, чья 14-дневная отсечка уместна для горизонта YTM, но
    // не для классификации купонов). См. doc-comment OfferCutoffResolver.ResolveNearestOfferDate и
    // BondSyncService.HasFloatingCoupon.

    [Fact]
    public void ResolveNearestOfferDate_NoOffers_ReturnsNull()
    {
        var result = OfferCutoffResolver.ResolveNearestOfferDate(AsOf, offers: null);

        result.Should().BeNull();
    }

    [Fact]
    public void ResolveNearestOfferDate_EmptyOffers_ReturnsNull()
    {
        var result = OfferCutoffResolver.ResolveNearestOfferDate(AsOf, offers: []);

        result.Should().BeNull();
    }

    [Fact]
    public void ResolveNearestOfferDate_PastOffer_IsIgnored_ReturnsNull()
    {
        var past = TestModelFactory.Offer(InstrumentId, AsOf.AddDays(-1), OfferType.Put);

        var result = OfferCutoffResolver.ResolveNearestOfferDate(AsOf, new[] { past });

        result.Should().BeNull("прошедшая оферта не должна учитываться — даже без 14-дневной отсечки Resolve");
    }

    [Fact]
    public void ResolveNearestOfferDate_ExecutedOffer_IsIgnored()
    {
        var executed = TestModelFactory.Offer(InstrumentId, AsOf.AddDays(30), OfferType.Put, isExecuted: true);

        var result = OfferCutoffResolver.ResolveNearestOfferDate(AsOf, new[] { executed });

        result.Should().BeNull();
    }

    [Fact]
    public void ResolveNearestOfferDate_OfferCloserThan14Days_IsStillReturned()
    {
        // Ключевое отличие от Resolve: оферта через 7 дней (< MinDaysToOffer) НЕ отбрасывается.
        var close = TestModelFactory.Offer(InstrumentId, AsOf.AddDays(7), OfferType.Put);

        var result = OfferCutoffResolver.ResolveNearestOfferDate(AsOf, new[] { close });

        result.Should().Be(close.Date);
    }

    [Fact]
    public void ResolveNearestOfferDate_OfferToday_IsIncluded()
    {
        var today = TestModelFactory.Offer(InstrumentId, AsOf, OfferType.Put);

        var result = OfferCutoffResolver.ResolveNearestOfferDate(AsOf, new[] { today });

        result.Should().Be(AsOf);
    }

    [Fact]
    public void ResolveNearestOfferDate_MultipleOffers_PicksClosestFutureOne()
    {
        var past = TestModelFactory.Offer(InstrumentId, AsOf.AddDays(-10), OfferType.Put);
        var nearer = TestModelFactory.Offer(InstrumentId, AsOf.AddDays(7), OfferType.Call);
        var farther = TestModelFactory.Offer(InstrumentId, AsOf.AddDays(365), OfferType.Put);

        var result = OfferCutoffResolver.ResolveNearestOfferDate(AsOf, new[] { farther, past, nearer });

        result.Should().Be(nearer.Date);
    }
}

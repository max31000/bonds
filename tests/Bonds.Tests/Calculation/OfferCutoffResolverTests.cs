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
}

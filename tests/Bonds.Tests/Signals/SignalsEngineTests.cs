using Bonds.Core.Analytics;
using Bonds.Core.Models;
using Bonds.Core.Signals;
using Bonds.Core.Time;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Signals;

/// <summary>
/// Тесты правил событий (plan/40): каждое правило — позитив/негатив; только открытые позиции;
/// ресет ставки только у настоящих флоатеров; кэш — по accounts.cash_rub; детерминированные ключи.
/// Даты — относительно «сегодня» (никаких абсолютных дат).
/// </summary>
public class SignalsEngineTests
{
    private static readonly DateOnly AsOf = BusinessClock.MoscowToday();

    private static SignalPositionContext Position(
        ulong instrumentId = 1,
        string issuer = "Эмитент А",
        string? name = "Бумага А",
        DateOnly? maturityDate = null,
        decimal quantity = 10m,
        CouponType couponType = CouponType.Fixed,
        IReadOnlyList<CouponSchedule>? coupons = null,
        IReadOnlyList<AmortizationSchedule>? amortizations = null,
        IReadOnlyList<OfferSchedule>? offers = null) => new()
    {
        PositionId = instrumentId,
        InstrumentId = instrumentId,
        Issuer = issuer,
        Name = name,
        MaturityDate = maturityDate ?? AsOf.AddYears(5),
        Quantity = quantity,
        CouponType = couponType,
        Coupons = coupons ?? Array.Empty<CouponSchedule>(),
        Amortizations = amortizations ?? Array.Empty<AmortizationSchedule>(),
        Offers = offers ?? Array.Empty<OfferSchedule>(),
    };

    private static PortfolioHolding Holding(ulong instrumentId, decimal marketValue, string issuer, decimal quantity = 1m) => new()
    {
        PositionId = instrumentId,
        InstrumentId = instrumentId,
        Quantity = quantity,
        MarketValueRub = marketValue,
        Issuer = issuer,
        Sector = "Финансы",
        CouponType = CouponType.Fixed,
        MaturityDate = AsOf.AddYears(5),
        HorizonDate = AsOf.AddYears(5),
        IsCalculatedToOffer = false,
        IsFloater = false,
        IsIndexed = false,
        IsEstimated = false,
        DataIncomplete = false,
    };

    private static SignalEvaluationInput Input(
        IReadOnlyList<SignalPositionContext>? positions = null,
        IReadOnlyList<PortfolioHolding>? holdings = null,
        decimal? cashRub = null,
        SignalEngineOptions? options = null) => new()
    {
        AccountId = 1,
        AsOf = AsOf,
        Positions = positions ?? Array.Empty<SignalPositionContext>(),
        Holdings = holdings ?? Array.Empty<PortfolioHolding>(),
        CashRub = cashRub,
        Options = options ?? new SignalEngineOptions(),
    };

    // ─── Оферты ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void PutOffer_InWindow_IsCritical_WithBondNameAndEventDate()
    {
        var date = AsOf.AddDays(5);
        var offers = new[] { new OfferSchedule { InstrumentId = 1, Date = date, OfferType = OfferType.Put } };

        var signals = SignalsEngine.Evaluate(Input(positions: [Position(offers: offers)]));

        var s = signals.Should().ContainSingle(x => x.Type == SignalType.UpcomingOffer).Subject;
        s.Severity.Should().Be(SignalSeverity.Critical);
        s.Subject.Should().Be("Бумага А");
        s.EventDate.Should().Be(date);
        s.InstrumentId.Should().Be(1ul);
        s.SuggestedAction.Should().Contain("решите до");
    }

    [Fact]
    public void CallOffer_InWindow_IsWarning()
    {
        var offers = new[] { new OfferSchedule { InstrumentId = 1, Date = AsOf.AddDays(5), OfferType = OfferType.Call } };

        var signals = SignalsEngine.Evaluate(Input(positions: [Position(offers: offers)]));

        signals.Should().ContainSingle(x => x.Type == SignalType.UpcomingOffer)
            .Which.Severity.Should().Be(SignalSeverity.Warning);
    }

    [Fact]
    public void Offer_ExecutedOrOutsideWindow_NoEvent()
    {
        var offers = new[]
        {
            new OfferSchedule { InstrumentId = 1, Date = AsOf.AddDays(3), OfferType = OfferType.Put, IsExecuted = true },
            new OfferSchedule { InstrumentId = 1, Date = AsOf.AddDays(60), OfferType = OfferType.Put },
            new OfferSchedule { InstrumentId = 1, Date = AsOf.AddDays(-2), OfferType = OfferType.Put },
        };

        SignalsEngine.Evaluate(Input(positions: [Position(offers: offers)])).Should().BeEmpty();
    }

    [Fact]
    public void PutAndCallOnSameDate_Collapse_ToSingleMoreSevereEvent()
    {
        var date = AsOf.AddDays(4);
        var offers = new[]
        {
            new OfferSchedule { InstrumentId = 1, Date = date, OfferType = OfferType.Call },
            new OfferSchedule { InstrumentId = 1, Date = date, OfferType = OfferType.Put },
        };

        var signals = SignalsEngine.Evaluate(Input(positions: [Position(offers: offers)]));

        signals.Should().ContainSingle().Which.Severity.Should().Be(SignalSeverity.Critical);
    }

    // ─── Погашение / амортизация ───────────────────────────────────────────────────────────

    [Fact]
    public void Redemption_InWindow_IsInfo()
    {
        var maturity = AsOf.AddDays(7);
        var signals = SignalsEngine.Evaluate(Input(positions: [Position(maturityDate: maturity)]));

        var s = signals.Should().ContainSingle(x => x.Type == SignalType.UpcomingRedemption).Subject;
        s.Severity.Should().Be(SignalSeverity.Info);
        s.EventDate.Should().Be(maturity);
    }

    [Fact]
    public void Redemption_AlreadyMatured_NoEvent()
    {
        SignalsEngine.Evaluate(Input(positions: [Position(maturityDate: AsOf.AddDays(-1))])).Should().BeEmpty();
    }

    [Fact]
    public void ClosedPosition_QuantityZero_ProducesNothing()
    {
        var offers = new[] { new OfferSchedule { InstrumentId = 1, Date = AsOf.AddDays(3), OfferType = OfferType.Put } };
        var signals = SignalsEngine.Evaluate(Input(positions: [Position(maturityDate: AsOf.AddDays(2), quantity: 0m, offers: offers)]));
        signals.Should().BeEmpty();
    }

    [Fact]
    public void Amortization_InWindow_Info_UnknownAmountHasNoFakeZero()
    {
        var amort = new[]
        {
            new AmortizationSchedule { InstrumentId = 1, Date = AsOf.AddDays(6), AmountRub = 0m, IsKnown = false },
        };

        var signals = SignalsEngine.Evaluate(Input(positions: [Position(amortizations: amort)]));

        var s = signals.Should().ContainSingle(x => x.Type == SignalType.UpcomingAmortization).Subject;
        s.Severity.Should().Be(SignalSeverity.Info);
        s.SuggestedAction.Should().NotContain("0.00").And.Contain("не известна");
    }

    [Fact]
    public void Coupons_AreNotEvents()
    {
        var coupons = new[] { new CouponSchedule { InstrumentId = 1, CouponDate = AsOf.AddDays(3), ValueRub = 40m, IsKnown = true } };
        SignalsEngine.Evaluate(Input(positions: [Position(coupons: coupons)])).Should().BeEmpty();
    }

    // ─── Пересчёт ставки: только настоящие флоатеры ────────────────────────────────────────

    [Fact]
    public void FloaterRateReset_RealFloater_WithUnknownCouponInWindow_Info()
    {
        var reset = AsOf.AddDays(8);
        var coupons = new[] { new CouponSchedule { InstrumentId = 1, CouponDate = reset, IsKnown = false } };

        var signals = SignalsEngine.Evaluate(Input(positions: [Position(couponType: CouponType.Floating, coupons: coupons)]));

        signals.Should().ContainSingle(x => x.Type == SignalType.FloaterRateReset)
            .Which.EventDate.Should().Be(reset);
    }

    [Theory]
    [InlineData(CouponType.Fixed)]
    [InlineData(CouponType.Indexed)]
    public void FloaterRateReset_NotAFloater_FixedWithOfferOrUnknownCoupon_NoEvent(CouponType type)
    {
        var coupons = new[] { new CouponSchedule { InstrumentId = 1, CouponDate = AsOf.AddDays(8), IsKnown = false } };
        SignalsEngine.Evaluate(Input(positions: [Position(couponType: type, coupons: coupons)]))
            .Should().NotContain(s => s.Type == SignalType.FloaterRateReset);
    }

    // ─── Концентрация ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Concentration_AboveDefaultLimit_Warning_StateWithoutDate()
    {
        var holdings = new[] { Holding(1, 800_000m, "Эмитент А"), Holding(2, 200_000m, "Эмитент Б") };

        var signals = SignalsEngine.Evaluate(Input(holdings: holdings));

        var s = signals.Should().ContainSingle(x => x.Type == SignalType.ConcentrationLimitBreached).Subject;
        s.Severity.Should().Be(SignalSeverity.Warning);
        s.Subject.Should().Be("Эмитент А");
        s.EventDate.Should().BeNull();
    }

    [Fact]
    public void Concentration_UsesConfiguredLimit_FromOptions()
    {
        var holdings = new[] { Holding(1, 300_000m, "А"), Holding(2, 300_000m, "Б"), Holding(3, 400_000m, "В") };

        SignalsEngine.Evaluate(Input(holdings: holdings, options: new SignalEngineOptions { DefaultMaxConcentrationPercent = 35m }))
            .Should().ContainSingle(s => s.Subject == "В");
        SignalsEngine.Evaluate(Input(holdings: holdings, options: new SignalEngineOptions { DefaultMaxConcentrationPercent = 45m }))
            .Should().BeEmpty();
    }

    [Fact]
    public void Concentration_ClosedHoldingsIgnored()
    {
        var holdings = new[] { Holding(1, 900_000m, "А", quantity: 0m), Holding(2, 100_000m, "Б") };
        // Единственный открытый холдинг — 100% эмитента Б (над лимитом), а закрытый «А» не учитывается.
        var signals = SignalsEngine.Evaluate(Input(holdings: holdings));
        signals.Should().ContainSingle().Which.Subject.Should().Be("Б");
    }

    // ─── Свободные деньги (accounts.cash_rub) ──────────────────────────────────────────────

    [Fact]
    public void UninvestedCash_AboveThreshold_Info_State()
    {
        var signals = SignalsEngine.Evaluate(Input(cashRub: 50_000m));

        var s = signals.Should().ContainSingle(x => x.Type == SignalType.UninvestedCashThreshold).Subject;
        s.Severity.Should().Be(SignalSeverity.Info);
        s.EventDate.Should().BeNull();
        s.SuggestedAction.Should().Contain("50000");
    }

    [Fact]
    public void UninvestedCash_BelowThresholdOrUnknownBalance_NoEvent()
    {
        SignalsEngine.Evaluate(Input(cashRub: 5_000m)).Should().BeEmpty();
        SignalsEngine.Evaluate(Input(cashRub: null)).Should().BeEmpty();
    }

    [Fact]
    public void UninvestedCash_ThresholdFromOptions_IsRespected()
    {
        SignalsEngine.Evaluate(Input(cashRub: 5_000m, options: new SignalEngineOptions { UninvestedCashThresholdRub = 1_000m }))
            .Should().ContainSingle(s => s.Type == SignalType.UninvestedCashThreshold);
    }

    // ─── Окно дней и ключи ─────────────────────────────────────────────────────────────────

    [Fact]
    public void WindowDays_FromOptions_ControlsInclusion()
    {
        var position = Position(maturityDate: AsOf.AddDays(20));

        SignalsEngine.Evaluate(Input(positions: [position])).Should().BeEmpty();
        SignalsEngine.Evaluate(Input(positions: [position], options: new SignalEngineOptions { UpcomingEventDaysThreshold = 30 }))
            .Should().ContainSingle(s => s.Type == SignalType.UpcomingRedemption);
    }

    [Fact]
    public void EventKeys_AreStable_AcrossEvaluations_AndDistinctBetweenEvents()
    {
        var position = Position(maturityDate: AsOf.AddDays(5), offers:
            [new OfferSchedule { InstrumentId = 1, Date = AsOf.AddDays(9), OfferType = OfferType.Put }]);
        var input = Input(positions: [position], cashRub: 99_999m);

        var first = SignalsEngine.Evaluate(input).Select(s => s.EventKey).ToList();
        var second = SignalsEngine.Evaluate(input).Select(s => s.EventKey).ToList();

        second.Should().BeEquivalentTo(first);
        first.Should().OnlyHaveUniqueItems().And.HaveCount(3);
    }

    [Fact]
    public void WithOverrides_NullKeepsDefault_ValueReplaces_AndDoesNotMutateSource()
    {
        var defaults = new SignalEngineOptions();
        var merged = defaults.WithOverrides(upcomingEventDays: 30, uninvestedCashRub: null, maxConcentrationPercent: 40m);

        merged.UpcomingEventDaysThreshold.Should().Be(30);
        merged.UninvestedCashThresholdRub.Should().Be(defaults.UninvestedCashThresholdRub);
        merged.DefaultMaxConcentrationPercent.Should().Be(40m);
        defaults.UpcomingEventDaysThreshold.Should().Be(14);
    }
}

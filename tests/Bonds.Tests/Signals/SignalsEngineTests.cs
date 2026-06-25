using Bonds.Core.Analytics;
using Bonds.Core.Models;
using Bonds.Core.Signals;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Signals;

/// <summary>
/// Тесты Signals Engine (plan/07 Часть A, spec §8) — каждое правило: позитивный кейс (триггер
/// срабатывает) и негативный (не срабатывает), плюс приоритет put-оферты (Critical), Critical для
/// концентрации, дедупликация и поведение без TargetAllocation. Чистые входы — никаких репозиториев/БД.
/// </summary>
public class SignalsEngineTests
{
    private static readonly DateOnly AsOf = new(2026, 6, 25);

    private static SignalPositionContext Position(
        ulong positionId = 1,
        ulong instrumentId = 1,
        string issuer = "Эмитент А",
        DateOnly? maturityDate = null,
        IReadOnlyList<CouponSchedule>? coupons = null,
        IReadOnlyList<AmortizationSchedule>? amortizations = null,
        IReadOnlyList<OfferSchedule>? offers = null) => new()
    {
        PositionId = positionId,
        InstrumentId = instrumentId,
        Issuer = issuer,
        MaturityDate = maturityDate ?? new DateOnly(2030, 1, 1),
        Coupons = coupons ?? Array.Empty<CouponSchedule>(),
        Amortizations = amortizations ?? Array.Empty<AmortizationSchedule>(),
        Offers = offers ?? Array.Empty<OfferSchedule>(),
    };

    private static PortfolioHolding Holding(
        ulong positionId = 1,
        ulong instrumentId = 1,
        decimal marketValue = 100_000m,
        string issuer = "Эмитент А",
        decimal? duration = 2m,
        decimal? ytm = 0.15m,
        DateOnly? horizonDate = null) => new()
    {
        PositionId = positionId,
        InstrumentId = instrumentId,
        Quantity = 1,
        MarketValueRub = marketValue,
        Issuer = issuer,
        Sector = "Финансы",
        CouponType = CouponType.Fixed,
        MaturityDate = horizonDate ?? new DateOnly(2030, 1, 1),
        HorizonDate = horizonDate ?? new DateOnly(2030, 1, 1),
        IsCalculatedToOffer = false,
        ModifiedDuration = duration,
        YtmEffective = ytm,
        IsFloater = false,
        IsIndexed = false,
        IsEstimated = false,
        DataIncomplete = false,
    };

    private static SignalEvaluationInput Input(
        IReadOnlyList<SignalPositionContext>? positions = null,
        IReadOnlyList<PortfolioHolding>? holdings = null,
        IReadOnlyList<Operation>? operations = null,
        IReadOnlyList<TargetAllocation>? targetAllocations = null,
        IReadOnlyList<Signal>? existingUnreadSignals = null,
        SignalEngineOptions? options = null) => new()
    {
        AccountId = 1,
        AsOf = AsOf,
        Positions = positions ?? Array.Empty<SignalPositionContext>(),
        Holdings = holdings ?? Array.Empty<PortfolioHolding>(),
        Operations = operations ?? Array.Empty<Operation>(),
        TargetAllocations = targetAllocations ?? Array.Empty<TargetAllocation>(),
        ExistingUnreadSignals = existingUnreadSignals ?? Array.Empty<Signal>(),
        Options = options ?? new SignalEngineOptions(),
    };

    // ─── 1a. Приближается купон ──────────────────────────────────────────────────────────

    [Fact]
    public void UpcomingCoupon_WithinThreshold_GeneratesSignal()
    {
        var coupon = new CouponSchedule { CouponDate = AsOf.AddDays(7), ValueRub = 35m, IsKnown = true };
        var input = Input(positions: [Position(coupons: [coupon])]);

        var signals = SignalsEngine.Evaluate(input);

        signals.Should().ContainSingle(s => s.Type == SignalType.UpcomingCoupon && s.Date == coupon.CouponDate);
    }

    [Fact]
    public void UpcomingCoupon_BeyondThreshold_NoSignal()
    {
        var coupon = new CouponSchedule { CouponDate = AsOf.AddDays(30), ValueRub = 35m, IsKnown = true };
        var input = Input(positions: [Position(coupons: [coupon])]);

        var signals = SignalsEngine.Evaluate(input);

        signals.Should().NotContain(s => s.Type == SignalType.UpcomingCoupon);
    }

    // ─── 1b. Приближается амортизация ────────────────────────────────────────────────────

    [Fact]
    public void UpcomingAmortization_WithinThreshold_GeneratesSignal()
    {
        var amortization = new AmortizationSchedule { Date = AsOf.AddDays(5), AmountRub = 250m };
        var input = Input(positions: [Position(amortizations: [amortization])]);

        var signals = SignalsEngine.Evaluate(input);

        signals.Should().ContainSingle(s => s.Type == SignalType.UpcomingAmortization && s.Date == amortization.Date);
    }

    [Fact]
    public void UpcomingAmortization_BeyondThreshold_NoSignal()
    {
        var amortization = new AmortizationSchedule { Date = AsOf.AddDays(60), AmountRub = 250m };
        var input = Input(positions: [Position(amortizations: [amortization])]);

        var signals = SignalsEngine.Evaluate(input);

        signals.Should().NotContain(s => s.Type == SignalType.UpcomingAmortization);
    }

    // ─── 1c. Приближается погашение ──────────────────────────────────────────────────────

    [Fact]
    public void UpcomingRedemption_WithinThreshold_GeneratesSignal()
    {
        var maturity = AsOf.AddDays(10);
        var input = Input(positions: [Position(maturityDate: maturity)]);

        var signals = SignalsEngine.Evaluate(input);

        signals.Should().ContainSingle(s => s.Type == SignalType.UpcomingRedemption && s.Date == maturity);
    }

    [Fact]
    public void UpcomingRedemption_FarInFuture_NoSignal()
    {
        var input = Input(positions: [Position(maturityDate: AsOf.AddYears(5))]);

        var signals = SignalsEngine.Evaluate(input);

        signals.Should().NotContain(s => s.Type == SignalType.UpcomingRedemption);
    }

    // ─── 2. Приближается оферта put/call — приоритетный, Critical ───────────────────────

    [Fact]
    public void UpcomingPutOffer_WithinThreshold_GeneratesCriticalSignal()
    {
        var offer = new OfferSchedule { Date = AsOf.AddDays(10), OfferType = OfferType.Put, IsExecuted = false };
        var input = Input(positions: [Position(offers: [offer])]);

        var signals = SignalsEngine.Evaluate(input);

        var signal = signals.Should().ContainSingle(s => s.Type == SignalType.UpcomingOffer).Subject;
        signal.Severity.Should().Be(SignalSeverity.Critical);
    }

    [Fact]
    public void UpcomingCallOffer_WithinThreshold_GeneratesCriticalSignal()
    {
        var offer = new OfferSchedule { Date = AsOf.AddDays(10), OfferType = OfferType.Call, IsExecuted = false };
        var input = Input(positions: [Position(offers: [offer])]);

        var signals = SignalsEngine.Evaluate(input);

        var signal = signals.Should().ContainSingle(s => s.Type == SignalType.UpcomingOffer).Subject;
        signal.Severity.Should().Be(SignalSeverity.Critical);
    }

    [Fact]
    public void UpcomingOffer_AlreadyExecuted_NoSignal()
    {
        var offer = new OfferSchedule { Date = AsOf.AddDays(10), OfferType = OfferType.Put, IsExecuted = true };
        var input = Input(positions: [Position(offers: [offer])]);

        var signals = SignalsEngine.Evaluate(input);

        signals.Should().NotContain(s => s.Type == SignalType.UpcomingOffer);
    }

    [Fact]
    public void UpcomingOffer_BeyondThreshold_NoSignal()
    {
        var offer = new OfferSchedule { Date = AsOf.AddDays(60), OfferType = OfferType.Put, IsExecuted = false };
        var input = Input(positions: [Position(offers: [offer])]);

        var signals = SignalsEngine.Evaluate(input);

        signals.Should().NotContain(s => s.Type == SignalType.UpcomingOffer);
    }

    // ─── 3. Пересчёт купона флоатера ──────────────────────────────────────────────────────

    [Fact]
    public void FloaterRateReset_NextUnknownCouponWithinThreshold_GeneratesSignal()
    {
        var coupons = new List<CouponSchedule>
        {
            new() { CouponDate = AsOf.AddDays(-5), ValueRub = 30m, IsKnown = true },
            new() { CouponDate = AsOf.AddDays(8), ValueRub = null, IsKnown = false },
        };
        var input = Input(positions: [Position(coupons: coupons)]);

        var signals = SignalsEngine.Evaluate(input);

        signals.Should().ContainSingle(s => s.Type == SignalType.FloaterRateReset && s.Date == AsOf.AddDays(8));
    }

    [Fact]
    public void FloaterRateReset_NoUnknownCoupon_NoSignal()
    {
        var coupons = new List<CouponSchedule>
        {
            new() { CouponDate = AsOf.AddDays(8), ValueRub = 30m, IsKnown = true },
        };
        var input = Input(positions: [Position(coupons: coupons)]);

        var signals = SignalsEngine.Evaluate(input);

        signals.Should().NotContain(s => s.Type == SignalType.FloaterRateReset);
    }

    // ─── 4. Незаинвестированный кэш выше порога ──────────────────────────────────────────

    [Fact]
    public void UninvestedCash_AboveThreshold_GeneratesSignal()
    {
        var operations = new List<Operation>
        {
            new() { Type = OperationType.Coupon, Date = AsOf.AddDays(-10).ToDateTime(TimeOnly.MinValue), AmountRub = 20_000m },
        };
        var options = new SignalEngineOptions { UninvestedCashThresholdRub = 10_000m };
        var input = Input(operations: operations, options: options);

        var signals = SignalsEngine.Evaluate(input);

        signals.Should().ContainSingle(s => s.Type == SignalType.UninvestedCashThreshold);
    }

    [Fact]
    public void UninvestedCash_ReinvestedByBuy_NoSignal()
    {
        var operations = new List<Operation>
        {
            new() { Type = OperationType.Coupon, Date = AsOf.AddDays(-10).ToDateTime(TimeOnly.MinValue), AmountRub = 20_000m },
            new() { Type = OperationType.Buy, Date = AsOf.AddDays(-5).ToDateTime(TimeOnly.MinValue), AmountRub = -19_000m },
        };
        var options = new SignalEngineOptions { UninvestedCashThresholdRub = 10_000m };
        var input = Input(operations: operations, options: options);

        var signals = SignalsEngine.Evaluate(input);

        signals.Should().NotContain(s => s.Type == SignalType.UninvestedCashThreshold);
    }

    [Fact]
    public void UninvestedCash_BelowThreshold_NoSignal()
    {
        var operations = new List<Operation>
        {
            new() { Type = OperationType.Coupon, Date = AsOf.AddDays(-10).ToDateTime(TimeOnly.MinValue), AmountRub = 5_000m },
        };
        var options = new SignalEngineOptions { UninvestedCashThresholdRub = 10_000m };
        var input = Input(operations: operations, options: options);

        var signals = SignalsEngine.Evaluate(input);

        signals.Should().NotContain(s => s.Type == SignalType.UninvestedCashThreshold);
    }

    // ─── 5. Доходность ниже сопоставимой по сроку альтернативы ──────────────────────────

    [Fact]
    public void YieldBelowAlternative_GapAboveThreshold_GeneratesSignal()
    {
        var horizon = new DateOnly(2030, 1, 1);
        var holdings = new List<PortfolioHolding>
        {
            Holding(positionId: 1, instrumentId: 1, ytm: 0.10m, horizonDate: horizon),
            Holding(positionId: 2, instrumentId: 2, ytm: 0.16m, horizonDate: horizon.AddDays(30)), // в окне ±180 дней, доходность выше на 600 б.п.
        };
        var options = new SignalEngineOptions { YieldBelowAlternativeBpsThreshold = 50 };
        var input = Input(holdings: holdings, options: options);

        var signals = SignalsEngine.Evaluate(input);

        signals.Should().ContainSingle(s => s.Type == SignalType.YieldBelowAlternative && s.PositionId == 1);
    }

    [Fact]
    public void YieldBelowAlternative_GapBelowThreshold_NoSignal()
    {
        var horizon = new DateOnly(2030, 1, 1);
        var holdings = new List<PortfolioHolding>
        {
            Holding(positionId: 1, instrumentId: 1, ytm: 0.150m, horizonDate: horizon),
            Holding(positionId: 2, instrumentId: 2, ytm: 0.151m, horizonDate: horizon.AddDays(30)), // разница 10 б.п. — ниже порога 50 б.п.
        };
        var options = new SignalEngineOptions { YieldBelowAlternativeBpsThreshold = 50 };
        var input = Input(holdings: holdings, options: options);

        var signals = SignalsEngine.Evaluate(input);

        signals.Should().NotContain(s => s.Type == SignalType.YieldBelowAlternative);
    }

    [Fact]
    public void YieldBelowAlternative_AlternativeOutsideMaturityWindow_NoSignal()
    {
        var horizon = new DateOnly(2030, 1, 1);
        var holdings = new List<PortfolioHolding>
        {
            Holding(positionId: 1, instrumentId: 1, ytm: 0.05m, horizonDate: horizon),
            Holding(positionId: 2, instrumentId: 2, ytm: 0.20m, horizonDate: horizon.AddYears(5)), // далеко за окном сопоставимости
        };
        var options = new SignalEngineOptions { MaturityWindowDaysForAlternativeComparison = 180 };
        var input = Input(holdings: holdings, options: options);

        var signals = SignalsEngine.Evaluate(input);

        signals.Should().NotContain(s => s.Type == SignalType.YieldBelowAlternative);
    }

    // ─── 6. Нарушение лимита концентрации по эмитенту — Critical ─────────────────────────

    [Fact]
    public void ConcentrationLimit_DefaultThresholdBreached_GeneratesCriticalSignal()
    {
        var holdings = new List<PortfolioHolding>
        {
            Holding(positionId: 1, instrumentId: 1, marketValue: 80_000m, issuer: "Эмитент А"),
            Holding(positionId: 2, instrumentId: 2, marketValue: 20_000m, issuer: "Эмитент Б"),
        };
        var options = new SignalEngineOptions { DefaultMaxConcentrationPercent = 25m };
        var input = Input(holdings: holdings, options: options);

        var signals = SignalsEngine.Evaluate(input);

        var signal = signals.Should().ContainSingle(s => s.Type == SignalType.ConcentrationLimitBreached).Subject;
        signal.Severity.Should().Be(SignalSeverity.Critical);
    }

    [Fact]
    public void ConcentrationLimit_SpecificTargetAllocationOverridesDefault()
    {
        var holdings = new List<PortfolioHolding>
        {
            Holding(positionId: 1, instrumentId: 1, marketValue: 40_000m, issuer: "Эмитент А"),
            Holding(positionId: 2, instrumentId: 2, marketValue: 60_000m, issuer: "Эмитент Б"),
        };
        // 40% доля у "Эмитент А" — выше дефолта 25%, но в рамках персонального лимита 50%.
        var targetAllocations = new List<TargetAllocation> { new() { Issuer = "Эмитент А", MaxConcentrationPercent = 50m } };
        var options = new SignalEngineOptions { DefaultMaxConcentrationPercent = 25m };
        var input = Input(holdings: holdings, targetAllocations: targetAllocations, options: options);

        var signals = SignalsEngine.Evaluate(input);

        signals.Should().NotContain(s => s.Type == SignalType.ConcentrationLimitBreached && s.SuggestedAction!.Contains("Эмитент А"));
    }

    [Fact]
    public void ConcentrationLimit_BelowThreshold_NoSignal()
    {
        var holdings = new List<PortfolioHolding>
        {
            Holding(positionId: 1, instrumentId: 1, marketValue: 50_000m, issuer: "Эмитент А"),
            Holding(positionId: 2, instrumentId: 2, marketValue: 50_000m, issuer: "Эмитент Б"),
        };
        var options = new SignalEngineOptions { DefaultMaxConcentrationPercent = 60m };
        var input = Input(holdings: holdings, options: options);

        var signals = SignalsEngine.Evaluate(input);

        signals.Should().NotContain(s => s.Type == SignalType.ConcentrationLimitBreached);
    }

    // ─── 7. Дрейф дюрации портфеля от целевой ─────────────────────────────────────────────

    [Fact]
    public void DurationDrift_AboveTolerance_WithTargetAllocation_GeneratesSignal()
    {
        var holdings = new List<PortfolioHolding>
        {
            Holding(positionId: 1, instrumentId: 1, marketValue: 100_000m, duration: 5m),
        };
        var targetAllocations = new List<TargetAllocation> { new() { Issuer = null, TargetDurationYears = 2m } };
        var options = new SignalEngineOptions { DurationDriftToleranceYears = 1.0m };
        var input = Input(holdings: holdings, targetAllocations: targetAllocations, options: options);

        var signals = SignalsEngine.Evaluate(input);

        signals.Should().ContainSingle(s => s.Type == SignalType.DurationDriftFromTarget);
    }

    [Fact]
    public void DurationDrift_NoTargetAllocation_NoSignal_DoesNotThrow()
    {
        var holdings = new List<PortfolioHolding>
        {
            Holding(positionId: 1, instrumentId: 1, marketValue: 100_000m, duration: 5m),
        };
        var input = Input(holdings: holdings); // нет ни одной TargetAllocation

        var act = () => SignalsEngine.Evaluate(input);

        act.Should().NotThrow();
        act().Should().NotContain(s => s.Type == SignalType.DurationDriftFromTarget);
    }

    [Fact]
    public void DurationDrift_NoHoldingsWithDuration_NoSignal_DoesNotThrow()
    {
        var holdings = new List<PortfolioHolding>
        {
            Holding(positionId: 1, instrumentId: 1, marketValue: 100_000m, duration: null),
        };
        var targetAllocations = new List<TargetAllocation> { new() { Issuer = null, TargetDurationYears = 2m } };
        var input = Input(holdings: holdings, targetAllocations: targetAllocations);

        var act = () => SignalsEngine.Evaluate(input);

        act.Should().NotThrow();
        act().Should().NotContain(s => s.Type == SignalType.DurationDriftFromTarget);
    }

    [Fact]
    public void DurationDrift_WithinTolerance_NoSignal()
    {
        var holdings = new List<PortfolioHolding>
        {
            Holding(positionId: 1, instrumentId: 1, marketValue: 100_000m, duration: 2.5m),
        };
        var targetAllocations = new List<TargetAllocation> { new() { Issuer = null, TargetDurationYears = 2m } };
        var options = new SignalEngineOptions { DurationDriftToleranceYears = 1.0m };
        var input = Input(holdings: holdings, targetAllocations: targetAllocations, options: options);

        var signals = SignalsEngine.Evaluate(input);

        signals.Should().NotContain(s => s.Type == SignalType.DurationDriftFromTarget);
    }

    // ─── 8. Низкая ликвидность — заглушка ────────────────────────────────────────────────

    [Fact]
    public void LowLiquidityWarning_NullBidAsk_ReturnsEmptyAndDoesNotThrow()
    {
        var positions = new List<(ulong PositionId, ulong InstrumentId, decimal? BestBid, decimal? BestAsk)>
        {
            (1, 1, null, null),
        };

        var act = () => SignalsEngine.LowLiquidityWarningRule(1, AsOf, positions);

        act.Should().NotThrow();
        act().Should().BeEmpty();
    }

    [Fact]
    public void LowLiquidityWarning_WideSpreadProvided_GeneratesSignal()
    {
        // Документирует, что заглушка готова к расширению, если bid/ask когда-нибудь появятся на входе.
        var positions = new List<(ulong PositionId, ulong InstrumentId, decimal? BestBid, decimal? BestAsk)>
        {
            (1, 1, 100m, 110m), // 10% спред — широкий
        };

        var signals = SignalsEngine.LowLiquidityWarningRule(1, AsOf, positions);

        signals.Should().ContainSingle(s => s.Type == SignalType.LowLiquidityWarning);
    }

    // ─── Дедупликация ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Evaluate_ExistingUnreadSignalWithSameKey_DoesNotCreateDuplicate()
    {
        var coupon = new CouponSchedule { CouponDate = AsOf.AddDays(7), ValueRub = 35m, IsKnown = true };
        var existing = new Signal
        {
            Type = SignalType.UpcomingCoupon,
            PositionId = 1,
            InstrumentId = 1,
            Date = coupon.CouponDate,
            IsRead = false,
        };
        var input = Input(positions: [Position(coupons: [coupon])], existingUnreadSignals: [existing]);

        var signals = SignalsEngine.Evaluate(input);

        signals.Should().NotContain(s => s.Type == SignalType.UpcomingCoupon);
    }

    [Fact]
    public void Evaluate_ExistingSignalWithDifferentDate_StillCreatesNewSignal()
    {
        var coupon = new CouponSchedule { CouponDate = AsOf.AddDays(7), ValueRub = 35m, IsKnown = true };
        var existing = new Signal
        {
            Type = SignalType.UpcomingCoupon,
            PositionId = 1,
            InstrumentId = 1,
            Date = coupon.CouponDate.AddDays(-100), // другая дата — другое событие
            IsRead = false,
        };
        var input = Input(positions: [Position(coupons: [coupon])], existingUnreadSignals: [existing]);

        var signals = SignalsEngine.Evaluate(input);

        signals.Should().ContainSingle(s => s.Type == SignalType.UpcomingCoupon && s.Date == coupon.CouponDate);
    }

    [Fact]
    public void Evaluate_NoExistingSignals_CreatesAllCandidates()
    {
        var coupon = new CouponSchedule { CouponDate = AsOf.AddDays(7), ValueRub = 35m, IsKnown = true };
        var input = Input(positions: [Position(coupons: [coupon])]);

        var signals = SignalsEngine.Evaluate(input);

        signals.Should().ContainSingle(s => s.Type == SignalType.UpcomingCoupon);
    }
}

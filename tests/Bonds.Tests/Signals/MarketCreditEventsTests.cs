using System.Globalization;
using Bonds.Core.Analytics;
using Bonds.Core.Models;
using Bonds.Core.Signals;
using Bonds.Core.Time;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Signals;

/// <summary>
/// Задача 49 часть B: рыночные (SpreadWidening, PriceDrawdown) и кредитное (IssuerDefaultEvent) события.
/// Единицы: G-спред и порог — ДОЛИ (0.0030 = 30 б.п. по умолчанию), просадка — ДОЛЯ (0.05 = 5%), цена — % от номинала.
/// Даты — относительно «сегодня».
/// </summary>
public class MarketCreditEventsTests
{
    private static readonly DateOnly AsOf = BusinessClock.MoscowToday();

    private static SignalPositionContext Position(ulong instrumentId = 1, decimal quantity = 10m, string issuer = "Эмитент А") => new()
    {
        PositionId = instrumentId,
        InstrumentId = instrumentId,
        Issuer = issuer,
        Name = $"Бумага {instrumentId}",
        MaturityDate = AsOf.AddYears(5),
        Quantity = quantity,
    };

    private static IReadOnlyList<decimal> Flat(decimal value, int days = 20) => Enumerable.Repeat(value, days).ToList();

    private static PositionMarketContext Market(
        ulong instrumentId = 1,
        string? isin = "RU000A0TEST1",
        decimal? gSpread = 0.0200m,
        IReadOnlyList<decimal>? gSpreadHistory = null,
        decimal? price = 100m,
        IReadOnlyList<decimal>? priceHistory = null,
        bool isFloater = false) => new()
    {
        InstrumentId = instrumentId,
        Isin = isin,
        IsFloater = isFloater,
        GSpreadFraction = gSpread,
        GSpreadHistory = gSpreadHistory ?? Flat(0.0200m),
        PricePercent = price,
        PriceHistory = priceHistory ?? Flat(100m),
    };

    private static SignalEvaluationInput Input(
        IReadOnlyList<SignalPositionContext>? positions = null,
        IReadOnlyList<PositionMarketContext>? market = null,
        IReadOnlyList<IssuerDefaultContext>? defaults = null,
        DateOnly? asOf = null,
        SignalEngineOptions? options = null) => new()
    {
        AccountId = 1,
        AsOf = asOf ?? AsOf,
        Positions = positions ?? [Position()],
        MarketContexts = market ?? [],
        IssuerDefaults = defaults ?? [],
        Options = options ?? new SignalEngineOptions(),
    };

    private static IReadOnlyList<Signal> Of(IReadOnlyList<Signal> all, SignalType type) => all.Where(s => s.Type == type).ToList();

    // ─── SpreadWidening ───────────────────────────────────────────────────────────────────

    [Fact]
    public void SpreadWidening_AboveThreshold_IsWarning_WithIsinAndIsoWeekKey()
    {
        // медиана 0.0200, сегодня 0.0260 → +60 б.п. > 30 б.п.
        var signals = SignalsEngine.Evaluate(Input(market: [Market(gSpread: 0.0260m)]));

        var signal = Of(signals, SignalType.SpreadWidening).Should().ContainSingle().Subject;
        signal.Severity.Should().Be(SignalSeverity.Warning);
        signal.InstrumentId.Should().Be(1ul);
        signal.Subject.Should().Be("Бумага 1");
        signal.EventKey.Should().Contain("RU000A0TEST1").And.Contain(IsoWeek(AsOf));
        signal.SuggestedAction.Should().Contain("60 б.п.");
    }

    [Fact]
    public void SpreadWidening_ExactlyAtThreshold_DoesNotFire()
    {
        // сдвиг ровно 30 б.п. (дефолт): условие строгое «>», граница не срабатывает
        var signals = SignalsEngine.Evaluate(Input(market: [Market(gSpread: 0.0230m)]));

        Of(signals, SignalType.SpreadWidening).Should().BeEmpty();
    }

    [Fact]
    public void SpreadWidening_JustAboveThreshold_Fires()
    {
        var signals = SignalsEngine.Evaluate(Input(market: [Market(gSpread: 0.023001m)]));

        Of(signals, SignalType.SpreadWidening).Should().ContainSingle();
    }

    [Fact]
    public void SpreadWidening_UsesMedianOfHistory_NotMeanOrLast()
    {
        // 19 дней по 0.0200 и один выброс 0.0000: медиана 0.0200, среднее 0.0190; сегодня 0.0225 (+25 б.п. к медиане < 30 б.п.,
        // но +35 б.п. к среднему) — по медиане не срабатывает
        var history = Flat(0.0200m, 19).Append(0.0000m).ToList();

        var signals = SignalsEngine.Evaluate(Input(market: [Market(gSpread: 0.0225m, gSpreadHistory: history)]));

        Of(signals, SignalType.SpreadWidening).Should().BeEmpty();
    }

    [Fact]
    public void SpreadWidening_LessThanTenHistoryDays_DoesNotFire()
    {
        var signals = SignalsEngine.Evaluate(Input(market: [Market(gSpread: 0.0900m, gSpreadHistory: Flat(0.0200m, 9))]));

        Of(signals, SignalType.SpreadWidening).Should().BeEmpty();
    }

    [Fact]
    public void SpreadWidening_ExactlyTenHistoryDays_Fires()
    {
        var signals = SignalsEngine.Evaluate(Input(market: [Market(gSpread: 0.0900m, gSpreadHistory: Flat(0.0200m, 10))]));

        Of(signals, SignalType.SpreadWidening).Should().ContainSingle();
    }

    [Fact]
    public void SpreadWidening_NoMarketContext_PositionOutsideBank_IsSkipped()
    {
        var signals = SignalsEngine.Evaluate(Input(market: []));

        signals.Should().BeEmpty();
    }

    [Fact]
    public void SpreadWidening_FloaterOrUnknownSpread_IsSkipped()
    {
        SignalsEngine.Evaluate(Input(market: [Market(gSpread: 0.0900m, isFloater: true)]))
            .Should().NotContain(s => s.Type == SignalType.SpreadWidening);
        SignalsEngine.Evaluate(Input(market: [Market(gSpread: null)]))
            .Should().NotContain(s => s.Type == SignalType.SpreadWidening);
    }

    [Fact]
    public void SpreadWidening_ClosedPosition_IsSkipped()
    {
        var signals = SignalsEngine.Evaluate(Input(positions: [Position(quantity: 0m)], market: [Market(gSpread: 0.0900m)]));

        signals.Should().BeEmpty();
    }

    [Fact]
    public void SpreadWidening_Narrowing_DoesNotFire()
    {
        var signals = SignalsEngine.Evaluate(Input(market: [Market(gSpread: 0.0050m)]));

        Of(signals, SignalType.SpreadWidening).Should().BeEmpty();
    }

    [Fact]
    public void SpreadWidening_CustomThresholdFromOptions_IsRespected()
    {
        var options = new SignalEngineOptions { SpreadWideningThresholdFraction = 0.0100m };

        // +60 б.п. не превышает 100 б.п.
        SignalsEngine.Evaluate(Input(market: [Market(gSpread: 0.0260m)], options: options))
            .Should().NotContain(s => s.Type == SignalType.SpreadWidening);
        SignalsEngine.Evaluate(Input(market: [Market(gSpread: 0.0310m)], options: options))
            .Should().Contain(s => s.Type == SignalType.SpreadWidening);
    }

    [Fact]
    public void SpreadWidening_Key_IsStableInsideIsoWeek_AndChangesNextWeek()
    {
        var monday = StartOfIsoWeek(AsOf);
        var keys = Enumerable.Range(0, 7)
            .Select(d => SignalsEngine.Evaluate(Input(asOf: monday.AddDays(d), market: [Market(gSpread: 0.0260m)]))
                .Single(s => s.Type == SignalType.SpreadWidening).EventKey)
            .ToList();

        keys.Distinct().Should().ContainSingle("ключ внутри ISO-недели не меняется — событие не мигает день-к-дню");

        var nextWeekKey = SignalsEngine.Evaluate(Input(asOf: monday.AddDays(7), market: [Market(gSpread: 0.0260m)]))
            .Single(s => s.Type == SignalType.SpreadWidening).EventKey;
        nextWeekKey.Should().NotBe(keys[0]);
    }

    [Fact]
    public void SpreadWidening_KeyFallsBackToInstrumentId_WhenIsinUnknown()
    {
        var signal = SignalsEngine.Evaluate(Input(market: [Market(isin: null, gSpread: 0.0900m)]))
            .Single(s => s.Type == SignalType.SpreadWidening);

        signal.EventKey.Should().Contain("#1");
    }

    // ─── PriceDrawdown ────────────────────────────────────────────────────────────────────

    [Fact]
    public void PriceDrawdown_AboveThreshold_IsWarning()
    {
        // максимум за 20 дней 100, цена 94 → просадка 6% > 5%
        var signals = SignalsEngine.Evaluate(Input(market: [Market(price: 94m)]));

        var signal = Of(signals, SignalType.PriceDrawdown).Should().ContainSingle().Subject;
        signal.Severity.Should().Be(SignalSeverity.Warning);
        signal.EventKey.Should().Contain("RU000A0TEST1").And.Contain(IsoWeek(AsOf));
        signal.SuggestedAction.Should().Contain("6.0%");
    }

    [Fact]
    public void PriceDrawdown_ExactlyAtThreshold_DoesNotFire()
    {
        // 100 → 95: ровно 5%
        var signals = SignalsEngine.Evaluate(Input(market: [Market(price: 95m)]));

        Of(signals, SignalType.PriceDrawdown).Should().BeEmpty();
    }

    [Fact]
    public void PriceDrawdown_UsesMaxOfHistoryAndToday()
    {
        // в истории пик 102, цена 96.9 → просадка 5.0%+ (>5%): (102-96.9)/102 = 0.05 ровно → не срабатывает; 96.8 → срабатывает
        var history = Flat(100m, 19).Append(102m).ToList();

        SignalsEngine.Evaluate(Input(market: [Market(price: 96.9m, priceHistory: history)]))
            .Should().NotContain(s => s.Type == SignalType.PriceDrawdown);
        SignalsEngine.Evaluate(Input(market: [Market(price: 96.8m, priceHistory: history)]))
            .Should().Contain(s => s.Type == SignalType.PriceDrawdown);
    }

    [Fact]
    public void PriceDrawdown_LessThanTenHistoryDays_DoesNotFire()
    {
        var signals = SignalsEngine.Evaluate(Input(market: [Market(price: 50m, priceHistory: Flat(100m, 9))]));

        Of(signals, SignalType.PriceDrawdown).Should().BeEmpty();
    }

    [Fact]
    public void PriceDrawdown_PriceGrowth_DoesNotFire_AndFloaterStillEvaluated()
    {
        SignalsEngine.Evaluate(Input(market: [Market(price: 105m)])).Should().BeEmpty();

        // просадка цены применима и к флоатеру (G-спред — нет)
        SignalsEngine.Evaluate(Input(market: [Market(price: 90m, gSpread: 0.0900m, isFloater: true)]))
            .Should().ContainSingle(s => s.Type == SignalType.PriceDrawdown)
            .And.NotContain(s => s.Type == SignalType.SpreadWidening);
    }

    [Fact]
    public void PriceDrawdown_NoPrice_Skipped_ClosedPosition_Skipped()
    {
        SignalsEngine.Evaluate(Input(market: [Market(price: null)])).Should().BeEmpty();
        SignalsEngine.Evaluate(Input(positions: [Position(quantity: 0m)], market: [Market(price: 50m)])).Should().BeEmpty();
    }

    [Fact]
    public void PriceDrawdown_CustomThresholdFromOptions()
    {
        var options = new SignalEngineOptions { PriceDrawdownThresholdFraction = 0.10m };

        SignalsEngine.Evaluate(Input(market: [Market(price: 94m)], options: options))
            .Should().NotContain(s => s.Type == SignalType.PriceDrawdown);
        SignalsEngine.Evaluate(Input(market: [Market(price: 89m)], options: options))
            .Should().Contain(s => s.Type == SignalType.PriceDrawdown);
    }

    [Fact]
    public void PriceDrawdown_Key_StableInsideWeek()
    {
        var monday = StartOfIsoWeek(AsOf);
        var keys = Enumerable.Range(0, 7)
            .Select(d => SignalsEngine.Evaluate(Input(asOf: monday.AddDays(d), market: [Market(price: 90m)]))
                .Single(s => s.Type == SignalType.PriceDrawdown).EventKey)
            .Distinct()
            .ToList();

        keys.Should().ContainSingle();
    }

    // ─── IssuerDefaultEvent ───────────────────────────────────────────────────────────────

    private static IssuerDefaultContext Default(int emitentId = 777, bool hasDefault = false, bool hasTech = false, ulong instrumentId = 1) => new()
    {
        EmitentId = emitentId,
        InstrumentId = instrumentId,
        Subject = "Эмитент А",
        HasDefault = hasDefault,
        HasTechnicalDefault = hasTech,
    };

    [Fact]
    public void IssuerDefault_Default_IsCritical_KeyedByEmitentAndKind()
    {
        var signals = SignalsEngine.Evaluate(Input(defaults: [Default(hasDefault: true)]));

        var signal = Of(signals, SignalType.IssuerDefaultEvent).Should().ContainSingle().Subject;
        signal.Severity.Should().Be(SignalSeverity.Critical);
        signal.EventKey.Should().Be(Signal.BuildKey(SignalType.IssuerDefaultEvent, "777:default", null));
        signal.Subject.Should().Be("Эмитент А");
        signal.InstrumentId.Should().Be(1ul);
        signal.EventDate.Should().BeNull();
        signal.SuggestedAction.Should().Contain("дефолт").And.NotContainAny("купите", "продайте");
    }

    [Fact]
    public void IssuerDefault_TechnicalDefault_SeparateKey_BothFlagsGiveTwoEvents()
    {
        var tech = SignalsEngine.Evaluate(Input(defaults: [Default(hasTech: true)]));
        tech.Single(s => s.Type == SignalType.IssuerDefaultEvent).EventKey
            .Should().Be(Signal.BuildKey(SignalType.IssuerDefaultEvent, "777:technical", null));

        var both = SignalsEngine.Evaluate(Input(defaults: [Default(hasDefault: true, hasTech: true)]));
        Of(both, SignalType.IssuerDefaultEvent).Should().HaveCount(2).And.OnlyContain(s => s.Severity == SignalSeverity.Critical);
    }

    [Fact]
    public void IssuerDefault_NoFlags_NoEvent()
    {
        SignalsEngine.Evaluate(Input(defaults: [Default()])).Should().BeEmpty();
    }

    [Fact]
    public void IssuerDefault_KeyDoesNotDependOnDate_StableAcrossDays()
    {
        var a = SignalsEngine.Evaluate(Input(asOf: AsOf, defaults: [Default(hasDefault: true)])).Single().EventKey;
        var b = SignalsEngine.Evaluate(Input(asOf: AsOf.AddDays(30), defaults: [Default(hasDefault: true)])).Single().EventKey;

        a.Should().Be(b, "событие живёт, пока флаг есть");
    }

    [Fact]
    public void IssuerDefault_TwoEmitents_TwoEvents_SameEmitentTwoBonds_OneEvent()
    {
        var signals = SignalsEngine.Evaluate(Input(
            positions: [Position(1), Position(2), Position(3, issuer: "Эмитент Б")],
            defaults: [Default(777, hasDefault: true, instrumentId: 1), Default(888, hasDefault: true, instrumentId: 3)]));

        Of(signals, SignalType.IssuerDefaultEvent).Should().HaveCount(2);
    }

    // ─── Синхронизация и устойчивость ─────────────────────────────────────────────────────

    [Fact]
    public void Events_Resolve_WhenConditionNormalizes()
    {
        var first = SignalsEngine.Evaluate(Input(market: [Market(gSpread: 0.0900m, price: 90m)], defaults: [Default(hasDefault: true)]));
        first.Select(s => s.Type).Should().BeEquivalentTo([SignalType.SpreadWidening, SignalType.PriceDrawdown, SignalType.IssuerDefaultEvent]);

        var existing = first.Select(s => { s.Id = 1; s.FirstSeenAt = DateTime.UtcNow; return s; }).ToList();
        var normalized = SignalsEngine.Evaluate(Input(market: [Market()]));
        var plan = SignalSetSynchronizer.Plan(normalized, existing);

        plan.ToInsert.Should().BeEmpty();
        plan.ToResolve.Should().HaveCount(3);
    }

    [Fact]
    public void Events_RepeatedEvaluation_SameKeys_NoDuplicatesOnSync()
    {
        var produced = SignalsEngine.Evaluate(Input(market: [Market(gSpread: 0.0900m)]));
        var plan = SignalSetSynchronizer.Plan(produced, produced.Select(s => { s.Id = 1; return s; }).ToList());

        plan.ToInsert.Should().BeEmpty();
        plan.ToRefresh.Should().HaveCount(produced.Count);
    }

    private static string IsoWeek(DateOnly date) =>
        string.Create(CultureInfo.InvariantCulture, $"{System.Globalization.ISOWeek.GetYear(date.ToDateTime(TimeOnly.MinValue))}-W{System.Globalization.ISOWeek.GetWeekOfYear(date.ToDateTime(TimeOnly.MinValue)):00}");

    private static DateOnly StartOfIsoWeek(DateOnly date)
    {
        var daysFromMonday = ((int)date.DayOfWeek + 6) % 7;
        return date.AddDays(-daysFromMonday);
    }
}

using Bonds.Core.Analytics;
using Bonds.Core.Models;
using FluentAssertions;
using Xunit;
using static Bonds.Core.Analytics.PositionVerdictService;

namespace Bonds.Tests.Analytics;

/// <summary>
/// Задача 49 часть A: вердикт позиции — правила в порядке приоритета (первое сработавшее). Единицы: выгода замены,
/// пороги — ДОЛИ годовых (0.005 = 50 б.п.), доли портфеля — ПРОЦЕНТЫ (как PortfolioCompositionService.SharePercent).
/// </summary>
public class PositionVerdictServiceTests
{
    private static ReliabilityScore Score(
        ReliabilityLight level = ReliabilityLight.Green, decimal coverage = 0.8m, decimal score = 80m,
        string[]? critical = null, string[]? warnings = null, string reason = "Зелёный: ...") =>
        new(score, coverage, level, reason, [], critical ?? [], warnings ?? []);

    private static ReplacementOption Replacement(
        decimal? benefit = 0.01m, ReliabilityLight light = ReliabilityLight.Green, decimal? netRub = 1500m, string name = "ОФЗ-Кандидат") => new()
    {
        TargetName = name, TargetSecid = "SU99", TargetLight = light, AnnualizedBenefitFraction = benefit, NetBenefitAfterTaxRub = netRub, EffectiveHorizonYears = 2m,
    };

    private static PositionVerdictInput Input(
        ReliabilityScore? score = null,
        RelativeValueVerdict value = RelativeValueVerdict.Fair,
        bool comparable = true,
        IReadOnlyList<SignalType>? events = null,
        ReplacementOption? best = null,
        decimal? afterBuy = 10m,
        decimal limit = 25m) => new()
    {
        Reliability = score ?? Score(),
        Value = value,
        YieldComparable = comparable,
        ActiveEventTypes = events ?? [],
        BestReplacement = best,
        IssuerSharePercentAfterBuy = afterBuy,
        ConcentrationLimitPercent = limit,
    };

    // ─── ConsiderExit ─────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(ReliabilityFlags.IssuerDefaulted)]
    [InlineData(ReliabilityFlags.InterestUncovered)]
    [InlineData(ReliabilityFlags.NegativeEquity)]
    [InlineData(ReliabilityFlags.SpreadShock)]
    public void CriticalFlag_GivesConsiderExit_WithReasonFromFlag(string flag)
    {
        var verdict = Evaluate(Input(Score(ReliabilityLight.Red, critical: [flag])));

        verdict.Verdict.Should().Be(PositionVerdictKind.ConsiderExit);
        verdict.Reasons.Should().NotBeEmpty().And.HaveCountLessThanOrEqualTo(3);
        verdict.Reasons[0].Should().NotContain(flag, "причина — человекочитаемая, а не имя флага");
    }

    [Fact]
    public void RedWithoutFlags_GivesConsiderExit_WithScorecardReason()
    {
        var verdict = Evaluate(Input(Score(ReliabilityLight.Red, reason: "Красный: балл 30 ниже порога 35.")));

        verdict.Verdict.Should().Be(PositionVerdictKind.ConsiderExit);
        verdict.Reasons.Should().Contain(r => r.Contains("балл 30"));
    }

    [Fact]
    public void Red_BeatsProfitableReplacement_Priority()
    {
        var verdict = Evaluate(Input(Score(ReliabilityLight.Red, critical: [ReliabilityFlags.SpreadShock]), best: Replacement(benefit: 0.05m)));

        verdict.Verdict.Should().Be(PositionVerdictKind.ConsiderExit);
    }

    [Fact]
    public void IssuerDefaultEvent_GivesConsiderExit_EvenIfScorecardNotRed()
    {
        var verdict = Evaluate(Input(events: [SignalType.IssuerDefaultEvent]));

        verdict.Verdict.Should().Be(PositionVerdictKind.ConsiderExit);
        verdict.Reasons.Should().Contain(r => r.Contains("дефолт"));
    }

    /// <summary>Флаг уровня эмитента (ревью В3): дефолт эмитента раскрыт на позицию, у которой само событие не привязано.</summary>
    [Fact]
    public void HasIssuerDefaultEvent_Flag_GivesConsiderExit_WithoutEventInActiveTypes()
    {
        var verdict = Evaluate(Input() with { HasIssuerDefaultEvent = true });

        verdict.Verdict.Should().Be(PositionVerdictKind.ConsiderExit);
        verdict.Reasons.Should().Contain(r => r.Contains("дефолт"));
    }

    [Fact]
    public void ConsiderExit_MultipleFlags_ReasonsCappedAtThree()
    {
        var verdict = Evaluate(Input(Score(ReliabilityLight.Red, critical:
            [ReliabilityFlags.IssuerDefaulted, ReliabilityFlags.InterestUncovered, ReliabilityFlags.NegativeEquity, ReliabilityFlags.SpreadShock])));

        verdict.Reasons.Should().HaveCount(3);
    }

    // ─── Нет данных ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void NoScorecard_CoverageZero_HoldWithLowConfidence_AndNoDataReason()
    {
        var verdict = Evaluate(Input(ReliabilityScoreService.Unknown(), value: RelativeValueVerdict.InsufficientData, best: Replacement(benefit: 0.05m)));

        verdict.Verdict.Should().Be(PositionVerdictKind.Hold);
        verdict.Confidence.Should().Be(VerdictConfidence.Low);
        verdict.Reasons.Should().ContainSingle().Which.Should().Contain("мало данных");
    }

    // ─── ConsiderReplace ──────────────────────────────────────────────────────────────────

    [Fact]
    public void ReplacementBenefitAtThreshold_GivesConsiderReplace_WithFigure()
    {
        var verdict = Evaluate(Input(best: Replacement(benefit: MinReplacementBenefitFraction)));

        verdict.Verdict.Should().Be(PositionVerdictKind.ConsiderReplace);
        verdict.Reasons[0].Should().Contain("ОФЗ-Кандидат").And.Contain("0.5%").And.Contain("годовых");
    }

    [Fact]
    public void ReplacementBenefitJustBelowThreshold_Hold()
    {
        var verdict = Evaluate(Input(best: Replacement(benefit: MinReplacementBenefitFraction - 0.0001m)));

        verdict.Verdict.Should().Be(PositionVerdictKind.Hold);
    }

    [Fact]
    public void ReplacementThreshold_IsFiftyBasisPointsAnnual() =>
        MinReplacementBenefitFraction.Should().Be(0.005m);

    [Fact]
    public void ReplacementTargetWorseThanHold_IsNotOffered()
    {
        // hold Green, цель Yellow — «хуже hold»
        var verdict = Evaluate(Input(Score(ReliabilityLight.Green), best: Replacement(benefit: 0.05m, light: ReliabilityLight.Yellow)));

        verdict.Verdict.Should().Be(PositionVerdictKind.Hold);
    }

    [Fact]
    public void ReplacementTargetSameOrBetterThanHold_Offered()
    {
        Evaluate(Input(Score(ReliabilityLight.Yellow), best: Replacement(light: ReliabilityLight.Yellow)))
            .Verdict.Should().Be(PositionVerdictKind.ConsiderReplace);
        Evaluate(Input(Score(ReliabilityLight.Yellow), best: Replacement(light: ReliabilityLight.Green)))
            .Verdict.Should().Be(PositionVerdictKind.ConsiderReplace);
    }

    [Fact]
    public void Floater_NeverConsiderReplaceByYield_ButOtherRulesWork()
    {
        Evaluate(Input(comparable: false, best: Replacement(benefit: 0.05m)))
            .Verdict.Should().Be(PositionVerdictKind.Hold);

        // остальные правила работают: критический флаг флоатера — выход
        Evaluate(Input(Score(ReliabilityLight.Red, critical: [ReliabilityFlags.NegativeEquity]), comparable: false))
            .Verdict.Should().Be(PositionVerdictKind.ConsiderExit);
    }

    [Fact]
    public void NullReplacementOrNullBenefit_NoReplace()
    {
        Evaluate(Input(best: null)).Verdict.Should().Be(PositionVerdictKind.Hold);
        Evaluate(Input(best: Replacement(benefit: null))).Verdict.Should().Be(PositionVerdictKind.Hold);
    }

    [Fact]
    public void ReplacementWithUnknownSaleTax_MentionsTaxNotIncluded()
    {
        var verdict = Evaluate(Input(best: Replacement(netRub: null)));

        verdict.Verdict.Should().Be(PositionVerdictKind.ConsiderReplace);
        verdict.Reasons.Should().Contain(r => r.Contains("налог продажи"));
    }

    [Fact]
    public void ReplacementBeatsBuyMore_Priority()
    {
        var verdict = Evaluate(Input(value: RelativeValueVerdict.Cheap, best: Replacement()));

        verdict.Verdict.Should().Be(PositionVerdictKind.ConsiderReplace);
    }

    // ─── BuyMoreCandidate ─────────────────────────────────────────────────────────────────

    [Fact]
    public void GreenCheapWithinLimit_GivesBuyMoreCandidate()
    {
        var verdict = Evaluate(Input(value: RelativeValueVerdict.Cheap, afterBuy: 24.9m, limit: 25m));

        verdict.Verdict.Should().Be(PositionVerdictKind.BuyMoreCandidate);
        verdict.Reasons.Should().Contain(r => r.Contains("дёшево к корзине")).And.Contain(r => r.Contains("лимит"));
    }

    [Fact]
    public void GreenCheap_ConcentrationExactlyAtLimit_StillBuyMore()
    {
        Evaluate(Input(value: RelativeValueVerdict.Cheap, afterBuy: 25m, limit: 25m)).Verdict.Should().Be(PositionVerdictKind.BuyMoreCandidate);
    }

    [Fact]
    public void GreenCheap_ConcentrationAboveLimit_HoldWithConcentrationReason()
    {
        var verdict = Evaluate(Input(value: RelativeValueVerdict.Cheap, afterBuy: 25.1m, limit: 25m));

        verdict.Verdict.Should().Be(PositionVerdictKind.Hold);
        verdict.Reasons.Should().Contain(r => r.Contains("лимит"));
    }

    [Fact]
    public void GreenCheap_ConcentrationUnknown_NoBuyMore()
    {
        Evaluate(Input(value: RelativeValueVerdict.Cheap, afterBuy: null)).Verdict.Should().Be(PositionVerdictKind.Hold);
    }

    [Theory]
    [InlineData(ReliabilityLight.Yellow)]
    public void YellowCheap_NoBuyMore(ReliabilityLight level)
    {
        Evaluate(Input(Score(level), value: RelativeValueVerdict.Cheap)).Verdict.Should().Be(PositionVerdictKind.Hold);
    }

    [Theory]
    [InlineData(RelativeValueVerdict.Fair)]
    [InlineData(RelativeValueVerdict.Rich)]
    [InlineData(RelativeValueVerdict.InsufficientData)]
    public void GreenButNotCheap_NoBuyMore(RelativeValueVerdict value)
    {
        Evaluate(Input(value: value)).Verdict.Should().Be(PositionVerdictKind.Hold);
    }

    [Theory]
    [InlineData(SignalType.SpreadWidening)]
    [InlineData(SignalType.PriceDrawdown)]
    public void GreenCheap_WithActiveMarketWarning_NoBuyMore(SignalType warning)
    {
        // дешевизна на фоне свежего расширения спреда/просадки — возможное ухудшение, а не «скидка»
        var verdict = Evaluate(Input(value: RelativeValueVerdict.Cheap, events: [warning]));

        verdict.Verdict.Should().Be(PositionVerdictKind.Hold);
    }

    [Fact]
    public void GreenCheap_WithTechnicalDefaultWarningFlag_NoBuyMore()
    {
        var verdict = Evaluate(Input(Score(warnings: [ReliabilityFlags.IssuerTechDefault]), value: RelativeValueVerdict.Cheap));

        verdict.Verdict.Should().Be(PositionVerdictKind.Hold);
    }

    // ─── Hold ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void DefaultCase_Hold_WithBasketReason()
    {
        var verdict = Evaluate(Input());

        verdict.Verdict.Should().Be(PositionVerdictKind.Hold);
        verdict.Reasons.Should().Contain(r => r.Contains("выгодных замен после издержек нет"));
    }

    [Fact]
    public void Hold_ActiveMarketWarning_AddedToReasons_MaxThree()
    {
        var verdict = Evaluate(Input(value: RelativeValueVerdict.Rich, events: [SignalType.SpreadWidening, SignalType.PriceDrawdown]));

        verdict.Verdict.Should().Be(PositionVerdictKind.Hold);
        verdict.Reasons.Should().HaveCountLessThanOrEqualTo(3);
        verdict.Reasons.Should().Contain(r => r.Contains("спред"));
    }

    // ─── Confidence ───────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0.0, VerdictConfidence.Low)]
    [InlineData(0.29, VerdictConfidence.Low)]
    [InlineData(0.3, VerdictConfidence.Medium)]
    [InlineData(0.59, VerdictConfidence.Medium)]
    [InlineData(0.6, VerdictConfidence.High)]
    [InlineData(1.0, VerdictConfidence.High)]
    public void Confidence_FollowsScorecardCoverage(double coverage, VerdictConfidence expected)
    {
        ConfidenceFor((decimal)coverage).Should().Be(expected);
    }

    // ─── Концентрация ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void ConcentrationAfterBuy_ProbeIsOnePercentOfPortfolio_ResultInPercent()
    {
        // эмитент 20 000 из 100 000 → докупка 1% портфеля = 1 000: (20 000 + 1 000) / (100 000 + 1 000) = 20.79%
        var share = ConcentrationAfterBuyPercent(issuerValueRub: 20_000m, portfolioValueRub: 100_000m);

        share.Should().BeApproximately(20.79m, 0.01m);
    }

    [Fact]
    public void ConcentrationAfterBuy_EmptyPortfolio_Null()
    {
        ConcentrationAfterBuyPercent(0m, 0m).Should().BeNull();
    }

    // ─── Выбор лучшей замены ──────────────────────────────────────────────────────────────

    [Fact]
    public void SelectBestReplacement_PicksMaxBenefit_AmongTargetsNotWorseThanHold()
    {
        var best = SelectBestReplacement(ReliabilityLight.Green,
        [
            Replacement(benefit: 0.02m, light: ReliabilityLight.Yellow, name: "Жёлтая (хуже hold)"),
            Replacement(benefit: 0.008m, light: ReliabilityLight.Green, name: "Зелёная-1"),
            Replacement(benefit: 0.012m, light: ReliabilityLight.Green, name: "Зелёная-2"),
            Replacement(benefit: null, light: ReliabilityLight.Green, name: "Без доходности"),
        ]);

        best!.TargetName.Should().Be("Зелёная-2");
    }

    [Fact]
    public void SelectBestReplacement_NoEligible_Null()
    {
        SelectBestReplacement(ReliabilityLight.Green, []).Should().BeNull();
        SelectBestReplacement(ReliabilityLight.Green, [Replacement(light: ReliabilityLight.Red)]).Should().BeNull();
    }

    [Fact]
    public void Disclaimer_NotAdvice_AndNoImperativeWording()
    {
        Disclaimer.Should().Contain("не рекомендация");
        var all = new[]
        {
            Evaluate(Input(Score(ReliabilityLight.Red, critical: [ReliabilityFlags.SpreadShock]))),
            Evaluate(Input(best: Replacement())),
            Evaluate(Input(value: RelativeValueVerdict.Cheap)),
            Evaluate(Input()),
        };
        foreach (var text in all.SelectMany(v => v.Reasons))
        {
            text.ToLowerInvariant().Should().NotContainAny("купите", "продайте", "покупайте", "продавайте");
        }
    }
}

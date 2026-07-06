using Bonds.Core.Analytics;
using Bonds.Core.Interfaces;
using FluentAssertions;
using Xunit;
using static Bonds.Core.Analytics.ReplacementMatrixService;

namespace Bonds.Tests.Analytics;

/// <summary>
/// Задача 23 — серверный перебор всех пар «держать vs переложиться» (заменяет фронтовый цикл
/// buildReplacementRequests, максимум 6 запросов). Покрывает: полный перебор всех сравнимых пар,
/// ранжирование bestPairs по netBenefit, попадание невыгодных/несопоставимых по дюрации пар в
/// rejectedPairs с причиной, исключение targetYield &lt;= holdYield пар вообще, annualizedBenefitFraction,
/// watchlist-таргеты, предохранитель на большом числе пар.
/// </summary>
public class ReplacementMatrixServiceTests
{
    private static MatrixCandidate Candidate(
        ulong positionId, ulong instrumentId, decimal marketValueRub, decimal? yield,
        decimal? duration, int daysToHorizon, bool isWatchlist = false, string? name = null) => new()
    {
        PositionId = positionId,
        InstrumentId = instrumentId,
        Name = name,
        MarketValueRub = marketValueRub,
        EffectiveYield = yield,
        ModifiedDuration = duration,
        DaysToHorizon = daysToHorizon,
        IsWatchlist = isWatchlist,
    };

    [Fact]
    public void BuildMatrix_HigherYieldTarget_ProducesBestPair_WithFullBreakdown()
    {
        var hold = Candidate(1, 10, 100_000m, 0.10m, 2m, 730, name: "Держим");
        var target = Candidate(2, 20, 100_000m, 0.16m, 2.2m, 900, name: "Переложиться");

        var result = BuildMatrix([hold], [target], 0.003m, 0.003m, CommissionRateSource.Default);

        result.BestPairs.Should().HaveCount(1);
        var pair = result.BestPairs[0];
        pair.HoldPositionId.Should().Be(1);
        pair.TargetPositionId.Should().Be(2);
        pair.SpreadFraction.Should().Be(0.06m);
        pair.NetBenefitRub.Should().BeGreaterThan(0m);
        pair.CapitalRub.Should().BeApproximately(100_000m - 300m, 0.01m, "капитал после комиссии продажи");
        pair.AnnualizedBenefitFraction.Should().NotBeNull();
        pair.CommissionRateUsed.Should().Be(0.003m);
        pair.CommissionRateSource.Should().Be(CommissionRateSource.Default);
        pair.IsWatchlistTarget.Should().BeFalse();
        result.RejectedPairs.Should().BeEmpty();
        result.TotalConsideredPairs.Should().Be(1);
    }

    [Fact]
    public void BuildMatrix_LowerOrEqualYieldTarget_NotIncludedAtAll_NotEvenRejected()
    {
        var hold = Candidate(1, 10, 100_000m, 0.15m, 2m, 730);
        var lowerYield = Candidate(2, 20, 100_000m, 0.10m, 2m, 730);
        var equalYield = Candidate(3, 30, 100_000m, 0.15m, 2m, 730);

        var result = BuildMatrix([hold], [lowerYield, equalYield], 0.003m, 0.003m, CommissionRateSource.Default);

        result.BestPairs.Should().BeEmpty();
        result.RejectedPairs.Should().BeEmpty();
        result.TotalConsideredPairs.Should().Be(0, "targetYield <= holdYield пары тривиальны и не считаются вообще (plan/23 §A.4)");
    }

    [Fact]
    public void BuildMatrix_NotProfitableAfterCommissions_GoesToRejectedWithReasonAndAmount()
    {
        // Маленький спред на коротком горизонте — комиссии двух сделок съедают выгоду.
        var hold = Candidate(1, 10, 1_000m, 0.10m, 2m, 10);
        var target = Candidate(2, 20, 1_000m, 0.101m, 2m, 10);

        var result = BuildMatrix([hold], [target], 0.01m, 0.01m, CommissionRateSource.Default);

        result.BestPairs.Should().BeEmpty();
        result.RejectedPairs.Should().HaveCount(1);
        var rejected = result.RejectedPairs[0];
        rejected.Reason.Should().Be(RejectedPairReason.NotProfitable);
        rejected.NetBenefitRub.Should().NotBeNull();
        rejected.NetBenefitRub!.Value.Should().BeLessOrEqualTo(0m);
        result.TotalConsideredPairs.Should().Be(1, "пара прошла targetYield>holdYield фильтр, просто оказалась невыгодной");
    }

    [Fact]
    public void BuildMatrix_DurationOutsideWindow_GoesToRejectedWithDurationMismatch_NotDropped()
    {
        var hold = Candidate(1, 10, 100_000m, 0.10m, 2m, 730);
        // Дюрация отличается на 3 года > 1.5 окна.
        var target = Candidate(2, 20, 100_000m, 0.20m, 5m, 3000);

        var result = BuildMatrix([hold], [target], 0.003m, 0.003m, CommissionRateSource.Default);

        result.BestPairs.Should().BeEmpty();
        result.RejectedPairs.Should().HaveCount(1);
        result.RejectedPairs[0].Reason.Should().Be(RejectedPairReason.DurationMismatch);
        result.RejectedPairs[0].NetBenefitRub.Should().BeNull("для durationMismatch чистая выгода не считалась вовсе");
    }

    [Fact]
    public void BuildMatrix_DurationWindowBoundary_ExactlyOnePointFive_IsComparable()
    {
        var hold = Candidate(1, 10, 100_000m, 0.10m, 2m, 730);
        var target = Candidate(2, 20, 100_000m, 0.20m, 3.5m, 900); // разница ровно 1.5

        var result = BuildMatrix([hold], [target], 0.003m, 0.003m, CommissionRateSource.Default);

        result.RejectedPairs.Should().NotContain(p => p.Reason == RejectedPairReason.DurationMismatch);
    }

    [Fact]
    public void BuildMatrix_NullDuration_DoesNotBlockComparison()
    {
        var hold = Candidate(1, 10, 100_000m, 0.10m, null, 730);
        var target = Candidate(2, 20, 100_000m, 0.20m, 5m, 900);

        var result = BuildMatrix([hold], [target], 0.003m, 0.003m, CommissionRateSource.Default);

        result.RejectedPairs.Should().NotContain(p => p.Reason == RejectedPairReason.DurationMismatch);
        result.BestPairs.Should().HaveCount(1);
    }

    [Fact]
    public void BuildMatrix_RanksBestPairsByNetBenefitDescending()
    {
        var hold = Candidate(1, 10, 100_000m, 0.05m, 2m, 900);
        var smallSpread = Candidate(2, 20, 100_000m, 0.07m, 2m, 900, name: "small");
        var bigSpread = Candidate(3, 30, 100_000m, 0.15m, 2m, 900, name: "big");

        var result = BuildMatrix([hold], [smallSpread, bigSpread], 0.003m, 0.003m, CommissionRateSource.Default);

        result.BestPairs.Should().HaveCount(2);
        result.BestPairs[0].TargetName.Should().Be("big", "самая крупная выгода должна идти первой");
        result.BestPairs[0].NetBenefitRub.Should().BeGreaterThan(result.BestPairs[1].NetBenefitRub);
    }

    [Fact]
    public void BuildMatrix_SkipsPairWithSameInstrument_EvenIfDifferentPositionId()
    {
        var hold = Candidate(1, 10, 100_000m, 0.10m, 2m, 730);
        var sameInstrumentDifferentPosition = Candidate(2, 10, 100_000m, 0.20m, 2m, 730);

        var result = BuildMatrix([hold], [sameInstrumentDifferentPosition], 0.003m, 0.003m, CommissionRateSource.Default);

        result.BestPairs.Should().BeEmpty();
        result.RejectedPairs.Should().BeEmpty();
        result.TotalConsideredPairs.Should().Be(0);
    }

    [Fact]
    public void BuildMatrix_WatchlistTarget_IsFlagged_AndParticipatesInMatrix()
    {
        var hold = Candidate(1, 10, 100_000m, 0.10m, 2m, 730);
        var watchlistTarget = Candidate(0, 99, 1_000m, 0.20m, 2m, 900, isWatchlist: true, name: "Watchlist bond");

        var result = BuildMatrix([hold], [watchlistTarget], 0.003m, 0.003m, CommissionRateSource.Default);

        result.BestPairs.Should().HaveCount(1);
        result.BestPairs[0].IsWatchlistTarget.Should().BeTrue();
        result.BestPairs[0].TargetInstrumentId.Should().Be(99);
    }

    [Fact]
    public void BuildMatrix_MissingYieldOnEitherSide_PairNotConsidered()
    {
        var hold = Candidate(1, 10, 100_000m, null, 2m, 730);
        var target = Candidate(2, 20, 100_000m, 0.20m, 2m, 730);

        var result = BuildMatrix([hold], [target], 0.003m, 0.003m, CommissionRateSource.Default);

        result.BestPairs.Should().BeEmpty();
        result.RejectedPairs.Should().BeEmpty();
        result.TotalConsideredPairs.Should().Be(0);
    }

    [Fact]
    public void BuildMatrix_AnnualizedBenefitFraction_MatchesDocumentedFormula()
    {
        var hold = Candidate(1, 10, 100_000m, 0.10m, 2m, 365);
        var target = Candidate(2, 20, 100_000m, 0.16m, 2m, 365);

        var result = BuildMatrix([hold], [target], 0.003m, 0.003m, CommissionRateSource.Default);

        var pair = result.BestPairs[0];
        var expected = pair.NetBenefitRub / pair.CapitalRub / pair.HorizonYears;
        pair.AnnualizedBenefitFraction!.Value.Should().BeApproximately(expected, 0.0000001m);
    }

    [Fact]
    public void HorizonYearsFor_UsesMinimumOfBothHorizons_MatchingFrontendFormula()
    {
        ReplacementMatrixService.HorizonYearsFor(730, 3650).Should().BeApproximately(730 / 365m, 0.0001m);
    }

    [Fact]
    public void HorizonYearsFor_ZeroOrNegativeDays_ClampsToMinimumOneDay()
    {
        ReplacementMatrixService.HorizonYearsFor(0, 100).Should().BeApproximately(1m / 365m, 0.0001m);
        ReplacementMatrixService.HorizonYearsFor(-5, 100).Should().BeApproximately(1m / 365m, 0.0001m);
    }

    [Fact]
    public void BuildMatrix_SafetyValve_CapsEachCategoryButKeepsTotalConsideredAccurate()
    {
        // 40 hold × 40 target (все выгодные, все несопоставимые по дюрации специально не делаем —
        // хотим переполнить именно bestPairs) => 40*39 = 1560 упорядоченных пар > порог теста ниже.
        var holds = Enumerable.Range(1, 60)
            .Select(i => Candidate((ulong)i, (ulong)i, 100_000m, 0.05m, 2m, 900))
            .ToList();
        var targets = Enumerable.Range(1, 60)
            .Select(i => Candidate((ulong)i, (ulong)i, 100_000m, 0.05m + (decimal)i / 1000m, 2m, 900))
            .ToList();

        var result = BuildMatrix(holds, targets, 0.001m, 0.001m, CommissionRateSource.Default);

        // 60*59 = 3540 упорядоченных пар, все с положительным спредом (targets всегда выше holds
        // по построению кроме i, где спред слишком мал/равен) — суммарно точно больше порога 2000.
        (result.BestPairs.Count + result.RejectedPairs.Count).Should().BeLessOrEqualTo(MaxPairsPerCategory * 2);
        result.TotalConsideredPairs.Should().BeGreaterThan(MaxPairsSafetyThreshold, "предохранитель не должен занижать TotalConsideredPairs — это честное число рассмотренных пар");
    }

    [Fact]
    public void IsComparable_FloaterOrIndexedOrDataIncomplete_ReturnsFalse()
    {
        ReplacementMatrixService.IsComparable(isFloater: true, isIndexed: false, dataIncomplete: false).Should().BeFalse();
        ReplacementMatrixService.IsComparable(isFloater: false, isIndexed: true, dataIncomplete: false).Should().BeFalse();
        ReplacementMatrixService.IsComparable(isFloater: false, isIndexed: false, dataIncomplete: true).Should().BeFalse();
        ReplacementMatrixService.IsComparable(isFloater: false, isIndexed: false, dataIncomplete: false).Should().BeTrue();
    }
}

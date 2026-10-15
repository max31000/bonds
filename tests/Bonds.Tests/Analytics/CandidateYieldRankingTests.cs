using Bonds.Core.Analytics;
using Bonds.Core.Models;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Analytics;

/// <summary>Задача 50: ранг кандидата = после-налоговая доходность к worst (min(YTM, yield_to_offer) × (1 − НДФЛ)).</summary>
public class CandidateYieldRankingTests
{
    private static BondUniverseEntry Entry(
        string secid, decimal? yield, decimal? toOffer = null, string? dateType = "MATDATE") => new()
    {
        Secid = secid,
        YieldFraction = yield,
        YieldToOfferFraction = toOffer,
        YieldDateType = dateType,
    };

    [Fact]
    public void Evaluate_ToOfferLowerThanYield_RanksByOfferAfterTax()
    {
        // Эталон вручную: yield 0.22, к оферте 0.18 → worst 0.18 → 0.18 × 0.87 = 0.1566.
        var ranked = CandidateYieldRanking.Evaluate(Entry("A", 0.22m, 0.18m));

        ranked.Should().NotBeNull();
        ranked!.AfterTaxYieldFraction.Should().Be(0.1566m);
        ranked.Basis.Should().Be(CandidateYieldRanking.BasisOffer);
    }

    [Fact]
    public void Evaluate_NoOfferYield_UsesMaturityYieldAfterTax()
    {
        // 0.20 × 0.87 = 0.174.
        var ranked = CandidateYieldRanking.Evaluate(Entry("A", 0.20m));

        ranked!.AfterTaxYieldFraction.Should().Be(0.174m);
        ranked.Basis.Should().Be(CandidateYieldRanking.BasisMaturity);
    }

    [Fact]
    public void Evaluate_OfferYieldHigherThanYield_WorstIsMaturity()
    {
        // К оферте выгоднее (0.25 > 0.20): консервативно берём меньшее — доходность к погашению.
        var ranked = CandidateYieldRanking.Evaluate(Entry("A", 0.20m, 0.25m));

        ranked!.AfterTaxYieldFraction.Should().Be(0.174m);
        ranked.Basis.Should().Be(CandidateYieldRanking.BasisMaturity);
    }

    [Fact]
    public void Evaluate_ExchangeAlreadyYieldsToOffer_BasisIsOfferEvenWithoutOfferYield()
    {
        // YIELDDATETYPE=OFFER: биржевая YIELD уже посчитана к оферте — основа «оферта».
        var ranked = CandidateYieldRanking.Evaluate(Entry("A", 0.20m, null, "OFFER"));

        ranked!.Basis.Should().Be(CandidateYieldRanking.BasisOffer);
        ranked.AfterTaxYieldFraction.Should().Be(0.174m);
    }

    [Fact]
    public void Evaluate_NoYield_ReturnsNull() =>
        CandidateYieldRanking.Evaluate(Entry("A", null, 0.18m)).Should().BeNull();

    [Fact]
    public void Evaluate_NegativeWorstYield_NotTaxed()
    {
        // Отрицательная доходность налога не создаёт — как в SwitchAnalysisService.AfterTax.
        CandidateYieldRanking.Evaluate(Entry("A", -0.02m))!.AfterTaxYieldFraction.Should().Be(-0.02m);
    }

    [Fact]
    public void Rank_SortsByAfterTaxDescending_NotByRawYield()
    {
        // B: сырая 0.25, но к оферте 0.15 → 0.1305; A: сырая 0.20 → 0.174 — A выше.
        var ranked = CandidateYieldRanking.Rank(
        [
            Entry("B", 0.25m, 0.15m),
            Entry("A", 0.20m),
            Entry("C", null),
        ]);

        ranked.Select(r => r.Entry.Secid).Should().Equal("A", "B");
    }

    [Fact]
    public void Rank_TiesBrokenBySecidAndStableWithNullOfferYield()
    {
        var entries = new[] { Entry("Z", 0.2m), Entry("M", 0.2m, null), Entry("A", 0.2m, 0.2m) };

        var first = CandidateYieldRanking.Rank(entries).Select(r => r.Entry.Secid).ToList();
        var second = CandidateYieldRanking.Rank(entries.Reverse()).Select(r => r.Entry.Secid).ToList();

        first.Should().Equal("A", "M", "Z");
        second.Should().Equal(first);
    }
}

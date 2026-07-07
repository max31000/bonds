using Bonds.Core.Models;
using Bonds.Core.Universe;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Universe;

/// <summary>Задача 26 часть C.4 — гигиенический фильтр банка облигаций, по одному тесту на причину скрытия.</summary>
public class UniverseHygieneFilterTests
{
    private static readonly DateOnly Today = new(2026, 7, 7);

    private static BondUniverseEntry HealthyEntry() => new()
    {
        Secid = "TESTBOND",
        TurnoverRub = 1_000_000m,
        ListLevel = 1,
        YieldFraction = 0.15m,
        DurationYears = 2.0m,
        PricePercent = 99.5m,
        MaturityDate = Today.AddYears(3),
    };

    private static UniverseHygieneOptions DefaultOptions() => new();

    [Fact]
    public void Evaluate_HealthyEntry_ReturnsNone()
    {
        var result = UniverseHygieneFilter.Evaluate(HealthyEntry(), DefaultOptions(), Today);
        result.Should().Be(HygieneHiddenReason.None);
    }

    [Fact]
    public void Evaluate_LowTurnover_ReturnsLowTurnover()
    {
        var entry = HealthyEntry();
        entry.TurnoverRub = 50_000m; // < дефолт 100 тыс

        var result = UniverseHygieneFilter.Evaluate(entry, DefaultOptions(), Today);

        result.Should().Be(HygieneHiddenReason.LowTurnover);
    }

    [Fact]
    public void Evaluate_NullTurnover_TreatedAsZero_ReturnsLowTurnover()
    {
        var entry = HealthyEntry();
        entry.TurnoverRub = null;

        var result = UniverseHygieneFilter.Evaluate(entry, DefaultOptions(), Today);

        result.Should().Be(HygieneHiddenReason.LowTurnover);
    }

    [Fact]
    public void Evaluate_ListLevelThree_HiddenByDefault_ReturnsListLevelThree()
    {
        var entry = HealthyEntry();
        entry.ListLevel = 3;

        var result = UniverseHygieneFilter.Evaluate(entry, DefaultOptions(), Today);

        result.Should().Be(HygieneHiddenReason.ListLevelThree);
    }

    [Fact]
    public void Evaluate_ListLevelThree_WithOptionDisabled_IsNotHidden()
    {
        var entry = HealthyEntry();
        entry.ListLevel = 3;
        var options = new UniverseHygieneOptions { HideListLevelThree = false };

        var result = UniverseHygieneFilter.Evaluate(entry, options, Today);

        result.Should().Be(HygieneHiddenReason.None);
    }

    [Fact]
    public void Evaluate_ImplausibleYield_ReturnsImplausibleYield()
    {
        var entry = HealthyEntry();
        entry.YieldFraction = 0.60m; // > дефолт 0.45

        var result = UniverseHygieneFilter.Evaluate(entry, DefaultOptions(), Today);

        result.Should().Be(HygieneHiddenReason.ImplausibleYield);
    }

    [Fact]
    public void Evaluate_MissingDuration_ReturnsMissingDurationOrPrice()
    {
        var entry = HealthyEntry();
        entry.DurationYears = null;

        var result = UniverseHygieneFilter.Evaluate(entry, DefaultOptions(), Today);

        result.Should().Be(HygieneHiddenReason.MissingDurationOrPrice);
    }

    [Fact]
    public void Evaluate_MissingPrice_ReturnsMissingDurationOrPrice()
    {
        var entry = HealthyEntry();
        entry.PricePercent = null;

        var result = UniverseHygieneFilter.Evaluate(entry, DefaultOptions(), Today);

        result.Should().Be(HygieneHiddenReason.MissingDurationOrPrice);
    }

    [Fact]
    public void Evaluate_NearMaturity_ReturnsNearMaturity()
    {
        var entry = HealthyEntry();
        entry.MaturityDate = Today.AddDays(7); // < дефолт 14 дней

        var result = UniverseHygieneFilter.Evaluate(entry, DefaultOptions(), Today);

        result.Should().Be(HygieneHiddenReason.NearMaturity);
    }

    [Fact]
    public void Evaluate_NearOffer_EarlierThanMaturity_ReturnsNearMaturity()
    {
        var entry = HealthyEntry();
        entry.MaturityDate = Today.AddYears(5);
        entry.OfferDate = Today.AddDays(5); // оферта ближе погашения и внутри порога

        var result = UniverseHygieneFilter.Evaluate(entry, DefaultOptions(), Today);

        result.Should().Be(HygieneHiddenReason.NearMaturity);
    }

    [Fact]
    public void Evaluate_OfferFarButMaturityFar_IsNotHidden()
    {
        var entry = HealthyEntry();
        entry.MaturityDate = Today.AddYears(5);
        entry.OfferDate = Today.AddYears(2); // оферта тоже далеко

        var result = UniverseHygieneFilter.Evaluate(entry, DefaultOptions(), Today);

        result.Should().Be(HygieneHiddenReason.None);
    }

    [Fact]
    public void Evaluate_PriorityOrder_LowTurnoverWinsOverOtherIssues()
    {
        // Несколько причин применимы одновременно — низкий оборот проверяется первым (план: порядок
        // "оборот → листинг → доходность → отсутствие данных → близость погашения").
        var entry = HealthyEntry();
        entry.TurnoverRub = 0m;
        entry.ListLevel = 3;
        entry.YieldFraction = 0.99m;

        var result = UniverseHygieneFilter.Evaluate(entry, DefaultOptions(), Today);

        result.Should().Be(HygieneHiddenReason.LowTurnover);
    }
}

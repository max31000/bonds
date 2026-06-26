using Bonds.Core.CashFlow;
using Bonds.Core.Models;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.CashFlow;

/// <summary>Тесты агрегации потока по месяцам/позициям и дат освобождения тела (plan/06 A4, spec §7.4).</summary>
public class CashFlowAggregatorTests
{
    private static ProjectedCashFlow Flow(
        ulong positionId, ulong instrumentId, DateOnly date, CashFlowType type,
        decimal gross, decimal tax, bool isEstimated = false) => new()
    {
        PositionId = positionId,
        InstrumentId = instrumentId,
        Date = date,
        FlowType = type,
        GrossRub = gross,
        TaxRub = tax,
        NetRub = gross - tax,
        IsEstimated = isEstimated,
    };

    [Fact]
    public void ByMonth_SumsGrossTaxNet_AndNetEqualsGrossMinusTax()
    {
        var jan = new DateOnly(2025, 1, 15);
        var janLater = new DateOnly(2025, 1, 28);
        var feb = new DateOnly(2025, 2, 5);

        var flows = new[]
        {
            Flow(1, 10, jan, CashFlowType.Coupon, 1000m, 130m),
            Flow(1, 10, janLater, CashFlowType.Amortization, 500m, 0m),
            Flow(2, 20, feb, CashFlowType.Coupon, 200m, 26m),
        };

        var byMonth = CashFlowAggregator.ByMonth(flows);

        byMonth.Should().HaveCount(2);

        var january = byMonth.Single(m => m.Month == new DateOnly(2025, 1, 1));
        january.GrossRub.Should().Be(1500m);
        january.TaxRub.Should().Be(130m);
        january.NetRub.Should().Be(1370m);
        january.NetRub.Should().Be(january.GrossRub - january.TaxRub, "нетто = брутто − налог всегда");
        january.CouponGrossRub.Should().Be(1000m);
        january.PrincipalGrossRub.Should().Be(500m);

        var february = byMonth.Single(m => m.Month == new DateOnly(2025, 2, 1));
        february.GrossRub.Should().Be(200m);
        february.NetRub.Should().Be(174m);
    }

    [Fact]
    public void ByMonth_FlagsMonthAsEstimated_WhenAnyFlowIsEstimated()
    {
        var month = new DateOnly(2025, 3, 1);
        var flows = new[]
        {
            Flow(1, 10, month, CashFlowType.Coupon, 100m, 13m, isEstimated: true),
            Flow(2, 20, month, CashFlowType.Coupon, 50m, 6.5m, isEstimated: false),
        };

        var byMonth = CashFlowAggregator.ByMonth(flows);

        byMonth.Single().HasEstimatedFlows.Should().BeTrue();
    }

    [Fact]
    public void ByPosition_SumsAcrossWholeHorizonPerPosition()
    {
        var flows = new[]
        {
            Flow(1, 10, new DateOnly(2025, 1, 1), CashFlowType.Coupon, 100m, 13m),
            Flow(1, 10, new DateOnly(2025, 7, 1), CashFlowType.Coupon, 100m, 13m),
            Flow(2, 20, new DateOnly(2025, 1, 1), CashFlowType.Coupon, 1000m, 130m),
        };

        var byPosition = CashFlowAggregator.ByPosition(flows);

        byPosition.Should().HaveCount(2);
        var position1 = byPosition.Single(p => p.PositionId == 1);
        position1.GrossRub.Should().Be(200m);
        position1.TaxRub.Should().Be(26m);
        position1.NetRub.Should().Be(174m);
    }

    [Fact]
    public void PrincipalReleases_ReturnsOnlyAmortizationAndRedemption_OrderedByDate()
    {
        var flows = new[]
        {
            Flow(1, 10, new DateOnly(2025, 6, 1), CashFlowType.Coupon, 100m, 13m),
            Flow(1, 10, new DateOnly(2025, 3, 1), CashFlowType.Amortization, 500m, 0m),
            Flow(1, 10, new DateOnly(2026, 1, 1), CashFlowType.Redemption, 500m, 0m),
        };

        var releases = CashFlowAggregator.PrincipalReleases(flows);

        releases.Should().HaveCount(2);
        releases[0].Date.Should().Be(new DateOnly(2025, 3, 1));
        releases[0].FlowType.Should().Be(CashFlowType.Amortization);
        releases[1].FlowType.Should().Be(CashFlowType.Redemption);
    }

    [Fact]
    public void ByMonthPosition_GroupsSameMonthPositionType_IntoOneRow()
    {
        var flows = new[]
        {
            Flow(1, 10, new DateOnly(2025, 1, 5), CashFlowType.Coupon, 100m, 13m),
            Flow(1, 10, new DateOnly(2025, 1, 20), CashFlowType.Coupon, 200m, 26m),
            Flow(2, 20, new DateOnly(2025, 1, 15), CashFlowType.Coupon, 50m, 6.5m),
        };

        var result = CashFlowAggregator.ByMonthPosition(flows);

        result.Should().HaveCount(2);
        var pos1 = result.Single(r => r.PositionId == 1);
        pos1.GrossRub.Should().Be(300m);
        pos1.TaxRub.Should().Be(39m);
        pos1.NetRub.Should().Be(261m);
        pos1.Month.Should().Be(new DateOnly(2025, 1, 1));
        var pos2 = result.Single(r => r.PositionId == 2);
        pos2.GrossRub.Should().Be(50m);
    }

    [Fact]
    public void ByMonthPosition_SeparatesFlowTypes_WhenSamePositionHasCouponAndAmortization()
    {
        var flows = new[]
        {
            Flow(1, 10, new DateOnly(2025, 3, 1), CashFlowType.Coupon, 100m, 13m),
            Flow(1, 10, new DateOnly(2025, 3, 15), CashFlowType.Amortization, 500m, 0m),
        };

        var result = CashFlowAggregator.ByMonthPosition(flows);

        result.Should().HaveCount(2);
        result.Should().Contain(r => r.FlowType == CashFlowType.Coupon && r.GrossRub == 100m);
        result.Should().Contain(r => r.FlowType == CashFlowType.Amortization && r.GrossRub == 500m);
    }

    [Fact]
    public void ByMonthPosition_IsEstimated_WhenAnyFlowInGroupIsEstimated()
    {
        var flows = new[]
        {
            Flow(1, 10, new DateOnly(2025, 5, 1), CashFlowType.Coupon, 100m, 13m, isEstimated: false),
            Flow(1, 10, new DateOnly(2025, 5, 15), CashFlowType.Coupon, 100m, 13m, isEstimated: true),
        };

        var result = CashFlowAggregator.ByMonthPosition(flows);

        result.Should().ContainSingle().Which.IsEstimated.Should().BeTrue();
    }

    [Fact]
    public void PrincipalReleases_FiltersByMinAmount()
    {
        var flows = new[]
        {
            Flow(1, 10, new DateOnly(2025, 3, 1), CashFlowType.Amortization, 100m, 0m),
            Flow(1, 10, new DateOnly(2025, 6, 1), CashFlowType.Amortization, 5000m, 0m),
        };

        var releases = CashFlowAggregator.PrincipalReleases(flows, minAmountRub: 1000m);

        releases.Should().ContainSingle().Which.AmountRub.Should().Be(5000m);
    }
}

using Bonds.Core.Models;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.CashFlow;

/// <summary>Тесты логики фильтрации ближайших 5 поступлений (task C1).</summary>
public class NextPaymentsTests
{
    private static ProjectedCashFlow Flow(
        ulong instrumentId, DateOnly date, CashFlowType type,
        decimal netRub, bool isEstimated = false) => new()
    {
        PositionId = 1,
        InstrumentId = instrumentId,
        Date = date,
        FlowType = type,
        GrossRub = netRub,
        TaxRub = 0m,
        NetRub = netRub,
        IsEstimated = isEstimated,
    };

    private static List<ProjectedCashFlow> SelectNextPayments(IEnumerable<ProjectedCashFlow> flows, DateOnly today)
        => flows.Where(f => f.Date >= today).OrderBy(f => f.Date).Take(5).ToList();

    [Fact]
    public void NextPaymentsAreFilteredToFutureDates()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var past = today.AddDays(-10);
        var future1 = today.AddDays(5);
        var future2 = today.AddDays(15);
        var future3 = today.AddDays(25);
        var future4 = today.AddDays(35);
        var future5 = today.AddDays(45);
        var future6 = today.AddDays(55);

        var flows = new[]
        {
            Flow(10, past, CashFlowType.Coupon, 100m),
            Flow(10, future6, CashFlowType.Coupon, 600m),
            Flow(10, future3, CashFlowType.Coupon, 300m),
            Flow(10, future1, CashFlowType.Coupon, 100m),
            Flow(10, future4, CashFlowType.Coupon, 400m),
            Flow(10, future2, CashFlowType.Coupon, 200m),
            Flow(10, future5, CashFlowType.Coupon, 500m),
        };

        var result = SelectNextPayments(flows, today);

        result.Should().HaveCount(5);
        result.Should().NotContain(f => f.Date == past);
        result.Should().NotContain(f => f.Date == future6);
        result[0].Date.Should().Be(future1);
        result[1].Date.Should().Be(future2);
        result[4].Date.Should().Be(future5);
    }

    [Fact]
    public void NextPaymentsAreEmpty_WhenNoFutureFlows()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var past1 = today.AddDays(-5);
        var past2 = today.AddDays(-1);

        var flows = new[]
        {
            Flow(10, past1, CashFlowType.Coupon, 100m),
            Flow(10, past2, CashFlowType.Coupon, 200m),
        };

        var result = SelectNextPayments(flows, today);

        result.Should().BeEmpty();
    }
}

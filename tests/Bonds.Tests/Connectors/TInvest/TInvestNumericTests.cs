using Bonds.Infrastructure.Connectors.TInvest;
using FluentAssertions;
using Tinkoff.InvestApi.V1;
using Xunit;

namespace Bonds.Tests.Connectors.TInvest;

public class TInvestNumericTests
{
    [Fact]
    public void MoneyValue_ToDecimal_CombinesUnitsAndNano()
    {
        var value = new MoneyValue { Units = 985, Nano = 500_000_000 }; // 985.5

        value.ToDecimal().Should().Be(985.5m);
    }

    [Fact]
    public void Quotation_ToDecimal_CombinesUnitsAndNano()
    {
        var value = new Quotation { Units = 12, Nano = 340_000_000 }; // 12.34

        value.ToDecimal().Should().Be(12.34m);
    }

    [Fact]
    public void Null_ToDecimal_ReturnsZero()
    {
        MoneyValue? value = null;

        value.ToDecimal().Should().Be(0m);
    }

    [Fact]
    public void Null_ToNullableDecimal_ReturnsNull()
    {
        MoneyValue? value = null;

        value.ToNullableDecimal().Should().BeNull();
    }

    [Fact]
    public void NegativeValue_RoundTrips()
    {
        var value = new MoneyValue { Units = -10, Nano = -250_000_000 }; // -10.25

        value.ToDecimal().Should().Be(-10.25m);
    }
}

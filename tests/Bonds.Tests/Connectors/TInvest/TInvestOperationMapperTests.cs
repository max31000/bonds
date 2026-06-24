using Bonds.Core.Models;
using Bonds.Infrastructure.Connectors.TInvest;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Connectors.TInvest;

/// <summary>
/// Тесты маппинга строкового protobuf-enum T-Invest OperationType → доменный OperationType
/// (см. Connectors/TInvest/README.md — верификация контракта §12.2). Чистая функция, без gRPC.
/// </summary>
public class TInvestOperationMapperTests
{
    [Theory]
    [InlineData("Buy", OperationType.Buy)]
    [InlineData("BuyCard", OperationType.Buy)]
    [InlineData("BuyMargin", OperationType.Buy)]
    [InlineData("Sell", OperationType.Sell)]
    [InlineData("SellCard", OperationType.Sell)]
    [InlineData("Coupon", OperationType.Coupon)]
    [InlineData("BondRepayment", OperationType.Amortization)]
    [InlineData("BondRepaymentFull", OperationType.Redemption)]
    [InlineData("BondTax", OperationType.Tax)]
    [InlineData("BondTaxProgressive", OperationType.Tax)]
    [InlineData("Tax", OperationType.Tax)]
    [InlineData("BrokerFee", OperationType.Fee)]
    [InlineData("ServiceFee", OperationType.Fee)]
    public void Map_KnownTypes_ReturnsExpectedDomainType(string tInvestType, OperationType expected)
    {
        var result = TInvestOperationMapper.Map(tInvestType);

        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("Dividend")] // акции — вне скоупа продукта (только облигации, spec §3)
    [InlineData("AccruingVarmargin")] // фьючерсы — вне скоупа
    [InlineData("Unspecified")]
    [InlineData("SomeFutureUnknownType")]
    public void Map_IrrelevantOrUnknownTypes_ReturnsNull(string tInvestType)
    {
        var result = TInvestOperationMapper.Map(tInvestType);

        result.Should().BeNull("операция не релевантна облигационному журналу одного счёта — не должна маппиться произвольно");
    }
}

using Bonds.Core.Models;
using Bonds.Infrastructure.Connectors.TInvest;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Connectors.TInvest;

/// <summary>Задача 51: маппер типов операций по фондам и конверсия TER в долю (чистые функции, без gRPC).</summary>
public class TInvestFundTests
{
    [Theory]
    [InlineData("Buy", FundOperationType.Buy)]
    [InlineData("BuyCard", FundOperationType.Buy)]
    [InlineData("BuyMargin", FundOperationType.Buy)]
    [InlineData("Sell", FundOperationType.Sell)]
    [InlineData("SellCard", FundOperationType.Sell)]
    [InlineData("SellMargin", FundOperationType.Sell)]
    [InlineData("Dividend", FundOperationType.Distribution)]
    [InlineData("DividendTransfer", FundOperationType.Distribution)]
    [InlineData("DivExt", FundOperationType.Distribution)]
    [InlineData("DividendTax", FundOperationType.Tax)]
    [InlineData("DividendTaxProgressive", FundOperationType.Tax)]
    [InlineData("Tax", FundOperationType.Tax)]
    [InlineData("TaxCorrection", FundOperationType.Tax)]
    [InlineData("BrokerFee", FundOperationType.Fee)]
    [InlineData("ServiceFee", FundOperationType.Fee)]
    [InlineData("InputSecurities", FundOperationType.SecurityIn)]
    [InlineData("OutputSecurities", FundOperationType.SecurityOut)]
    public void FundMapper_KnownTypes(string tInvestType, FundOperationType expected)
        => TInvestFundOperationMapper.Map(tInvestType).Should().Be(expected);

    [Theory]
    [InlineData("Coupon")] // у фонда купонов нет
    [InlineData("BondRepayment")]
    [InlineData("Unspecified")]
    [InlineData("AccruingVarmargin")]
    [InlineData("SomethingNew")]
    public void FundMapper_UnknownOrIrrelevant_ReturnsNull(string tInvestType)
        => TInvestFundOperationMapper.Map(tInvestType).Should().BeNull();

    [Theory]
    [InlineData("0.9", "0.009")]    // проценты -> доля
    [InlineData("0.009", "0.009")]  // уже доля
    [InlineData("0.05", "0.05")]    // граница: <= 0.05 считаем долей
    [InlineData("0.06", "0.0006")]  // сразу выше границы — проценты
    [InlineData("1.5", "0.015")]
    [InlineData("10", "0.1")]       // верхняя правдоподобная граница (10%)
    public void ExpenseRatioToFraction_Plausible(string raw, string expected)
        => TInvestNumeric.ExpenseRatioToFraction(decimal.Parse(raw, System.Globalization.CultureInfo.InvariantCulture))
            .Should().Be(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("10.01")]
    [InlineData("50")]
    public void ExpenseRatioToFraction_Implausible_ReturnsNull(string raw)
        => TInvestNumeric.ExpenseRatioToFraction(decimal.Parse(raw, System.Globalization.CultureInfo.InvariantCulture))
            .Should().BeNull();

    [Theory]
    [InlineData("Etf", true)]
    [InlineData("etf", true)]
    [InlineData("Bond", false)]
    [InlineData(null, false)]
    public void IsFund_CaseInsensitive(string? kind, bool expected)
        => new TInvestOperation { Id = "1", OperationType = "Buy", InstrumentKind = kind }.IsFund.Should().Be(expected);
}

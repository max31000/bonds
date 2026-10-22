using Bonds.Core.Models;
using Bonds.Infrastructure.Connectors.TInvest;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Connectors.TInvest;

/// <summary>Задача 55: маппер типов операций по валюте и пересчёт платежа не в рублях (чистые функции, без gRPC). Имена типов сверены по SDK 0.6.22.1.</summary>
public class TInvestCurrencyTests
{
    [Theory]
    [InlineData("Buy", CurrencyOperationType.Buy)]
    [InlineData("BuyCard", CurrencyOperationType.Buy)]
    [InlineData("BuyMargin", CurrencyOperationType.Buy)]
    [InlineData("Sell", CurrencyOperationType.Sell)]
    [InlineData("SellCard", CurrencyOperationType.Sell)]
    [InlineData("SellMargin", CurrencyOperationType.Sell)]
    [InlineData("Tax", CurrencyOperationType.Tax)]
    [InlineData("TaxCorrection", CurrencyOperationType.Tax)]
    [InlineData("BrokerFee", CurrencyOperationType.Fee)]
    [InlineData("ServiceFee", CurrencyOperationType.Fee)]
    [InlineData("OtherFee", CurrencyOperationType.Fee)]
    public void CurrencyMapper_KnownTypes(string tInvestType, CurrencyOperationType expected)
        => TInvestCurrencyOperationMapper.Map(tInvestType).Should().Be(expected);

    [Theory]
    [InlineData("Coupon")]
    [InlineData("Dividend")]
    [InlineData("Input")]              // пополнение счёта - не операция с валютным инструментом
    [InlineData("Output")]
    [InlineData("OverIncome")]         // доход от овернайта - к валютному инструменту однозначно не относится (Income не мапится)
    [InlineData("Overnight")]
    [InlineData("AccruingVarmargin")]  // вариационная маржа - не берём
    [InlineData("WritingOffVarmargin")]
    [InlineData("DeliveryBuy")]        // поставочные фьючерсы
    [InlineData("InputSecurities")]
    [InlineData("SomethingNew")]
    public void CurrencyMapper_UnknownOrIrrelevant_ReturnsNull(string tInvestType)
        => TInvestCurrencyOperationMapper.Map(tInvestType).Should().BeNull();

    [Fact]
    public void KindFlags_CurrencyOperation_IsNeitherBondNorFund()
    {
        var op = new TInvestOperation { Id = "x", OperationType = "Buy", InstrumentKind = "Currency" };
        op.IsCurrency.Should().BeTrue();
        op.IsBond.Should().BeFalse();
        op.IsFund.Should().BeFalse();
    }

    [Theory]
    [InlineData("RUB000UTSTOM", true)]
    [InlineData("rub000utstom", true)]
    [InlineData("BBG0013HRTL0", false)] // юань
    [InlineData(null, false)]
    public void RubleFigi_IsRecognised(string? figi, bool expected)
        => TInvestCurrencyInfo.IsRubleFigi(figi).Should().Be(expected);

    private static TInvestOperation Op(string? currency, decimal payment) => new()
    {
        Id = "op-1", OperationType = "BrokerFee", Figi = "F", Date = new DateTime(2026, 9, 1),
        PaymentRub = payment, PaymentCurrency = currency, UnitPrice = 1.5m, Quantity = 3, InstrumentKind = "Bond", AccruedInterest = 2m,
    };

    [Fact]
    public void ToRubles_RubOperation_ReturnsSameInstance()
    {
        var op = Op("rub", -100m);
        TInvestOperationCurrency.ToRubles(op, rubPerUnit: null).Should().BeSameAs(op);
        var noCurrency = Op(null, -100m);
        TInvestOperationCurrency.ToRubles(noCurrency, null).Should().BeSameAs(noCurrency);
    }

    [Fact]
    public void ToRubles_CnyOperation_ConvertsByRate_AndMarksResultRub()
    {
        var converted = TInvestOperationCurrency.ToRubles(Op("cny", -10m), 12.5m);

        converted.Should().NotBeNull();
        converted!.PaymentRub.Should().Be(-125m);
        converted.PaymentCurrency.Should().Be("rub");
        converted.UnitPrice.Should().BeNull("цена за единицу была в юанях - в рубли не переносится");
        converted.Id.Should().Be("op-1");
        converted.Quantity.Should().Be(3);
        converted.InstrumentKind.Should().Be("Bond");
        converted.AccruedInterest.Should().Be(2m);
    }

    [Fact]
    public void ToRubles_CnyWithoutRate_ReturnsNull_NotRubles()
        => TInvestOperationCurrency.ToRubles(Op("cny", -10m), null).Should().BeNull();
}

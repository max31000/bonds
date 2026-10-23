using Bonds.Core.Models;
using Bonds.Core.Trading;
using Bonds.Infrastructure.Connectors.TInvest;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Trading;

public class TradingMetaRulesTests
{
    // --- выбор classCode ---

    [Fact]
    public void Pick_Lqdt_TqbrApiFalse_SpbruApiTrue_PicksSpbru()
    {
        var picked = TradingListingSelector.Pick([new("TQBR", false), new("SPBRU", true)]);

        picked!.ClassCode.Should().Be("SPBRU");
    }

    [Fact]
    public void Pick_OnlyTqbrApiFalse_KeepsTqbr()
    {
        // SBMM/AKMM: единственный листинг TQBR без доступа через API
        var picked = TradingListingSelector.Pick([new("TQBR", false)]);

        picked!.ClassCode.Should().Be("TQBR");
        picked.ApiTradeAvailable.Should().BeFalse();
    }

    [Fact]
    public void Pick_NoApiListings_PrefersTqbrOverOthers()
    {
        TradingListingSelector.Pick([new("SPBRU", false), new("TQBR", false)])!.ClassCode.Should().Be("TQBR");
    }

    [Fact]
    public void Pick_NoApiListingsAndNoTqbr_AlphabeticallyFirst_Deterministic()
    {
        TradingListingSelector.Pick([new("TQTF", false), new("SPBRU", false)])!.ClassCode.Should().Be("SPBRU");
    }

    [Fact]
    public void Pick_SeveralApiListings_PrefersTqbr()
    {
        TradingListingSelector.Pick([new("SPBRU", true), new("TQBR", true)])!.ClassCode.Should().Be("TQBR");
    }

    [Fact]
    public void Pick_Empty_ReturnsNull() => TradingListingSelector.Pick([]).Should().BeNull();

    // --- единицы шага цены облигаций ---

    [Fact]
    public void BondPriceStep_PointsToRub_Reference_001PointAtNominal1000_Is01Rub()
    {
        BondPriceStep.PointsToRub(0.01m, 1000m).Should().Be(0.1m);
    }

    [Fact]
    public void BondPriceStep_AmortizedNominal_ScalesWithCurrentNominal()
    {
        BondPriceStep.PointsToRub(0.01m, 600m).Should().Be(0.06m);
    }

    // --- тип операции пополнения/вывода ---

    [Theory]
    [InlineData("Input", CashMovementType.Deposit)]
    [InlineData("InputAcquiring", CashMovementType.Deposit)]
    [InlineData("InputSwift", CashMovementType.Deposit)]
    [InlineData("Output", CashMovementType.Withdrawal)]
    [InlineData("OutputAcquiring", CashMovementType.Withdrawal)]
    [InlineData("OutputSwift", CashMovementType.Withdrawal)]
    public void CashMovementMapper_MapsDepositsAndWithdrawals(string type, CashMovementType expected)
    {
        TInvestCashMovementMapper.Map(type).Should().Be(expected);
    }

    [Theory]
    [InlineData("InputSecurities")]
    [InlineData("OutputSecurities")]
    [InlineData("InpMulti")]
    [InlineData("OutMulti")]
    [InlineData("OutputPenalty")]
    [InlineData("Buy")]
    [InlineData("Coupon")]
    [InlineData("BrokerFee")]
    public void CashMovementMapper_OtherTypes_NotMapped(string type)
    {
        TInvestCashMovementMapper.Map(type).Should().BeNull();
    }
}

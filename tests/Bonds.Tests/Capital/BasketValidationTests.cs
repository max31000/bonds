using Bonds.Core.Capital;
using Bonds.Core.Models;
using FluentAssertions;
using Xunit;
using BrokerIds = Bonds.Core.Models.Brokers;

namespace Bonds.Tests.Capital;

public class BasketValidationTests
{
    private static CapitalBasket B(decimal? target, decimal band = 0.05m, decimal hard = 0.10m) => new()
    {
        Title = "Корзина", TargetWeight = target, Band = band, HardBand = hard, LiquidityTier = 1, FillRule = BasketFillRules.Fixed,
    };

    [Fact]
    public void DefaultRubBaskets_TargetsSumToOne_AndFieldsAreValid()
    {
        var rub = DefaultBaskets.For(1, CapitalPortfolios.Rub);

        BasketValidation.ValidateTargetsSum("rub", rub).Should().BeNull();
        rub.Select(BasketValidation.ValidateFields).Should().AllSatisfy(e => e.Should().BeNull());
        rub.Select(b => b.Code).Should().Equal("liquidity", "bonds", "ru_equity", "temporary");
        rub.Single(b => b.Code == "liquidity").Members.Select(m => (m.InstrumentKey, m.SubWeight)).Should().Equal(("TMON@", 0.5m), ("TSAV", 0.5m));
        rub.Single(b => b.Code == "bonds").Members.Should().ContainSingle(m => m.MatchRule == BasketMatchRules.AssetClass && m.InstrumentKey == "bond");
        rub.Single(b => b.Code == "temporary").TargetWeight.Should().BeNull();
    }

    [Fact]
    public void DefaultUsdBaskets_UseFreedomTickers()
    {
        var usd = DefaultBaskets.For(1, CapitalPortfolios.Usd);

        usd.Select(b => b.Code).Should().Equal("global_core", "satellites", "frozen");
        usd.SelectMany(b => b.Members).Should().OnlyContain(m => m.Broker == BrokerIds.Freedom);
        usd.Single(b => b.Code == "satellites").Members.Select(m => m.InstrumentKey).Should().BeEquivalentTo("LUV", "UFO", "ROBO", "SJPA", "LGQM", "OTGLY");
        usd.Select(BasketValidation.ValidateFields).Should().AllSatisfy(e => e.Should().BeNull());
    }

    [Fact]
    public void DefaultUsdBaskets_TargetsSumToOne_OnlyCoreHasTarget()
    {
        var usd = DefaultBaskets.For(1, CapitalPortfolios.Usd);

        usd.Single(b => b.Code == "global_core").TargetWeight.Should().Be(1.0m);
        usd.Where(b => b.Code != "global_core").Should().OnlyContain(b => b.TargetWeight == null);
        BasketValidation.ValidateTargetsSum("usd", usd).Should().BeNull();
    }

    [Theory]
    [InlineData("freedom", "LUV", "freedom", "LUV.US", true)]
    [InlineData("freedom", "LUV.US", "freedom", "LUV", true)]
    [InlineData("freedom", "LUV.US", "freedom", "LUV.US", true)]
    [InlineData("freedom", "LUV.US", "freedom", "LUV.EU", false)]
    [InlineData("freedom", "LUV", "freedom", "LUVX.US", false)]
    [InlineData("tinvest", "TMON", "tinvest", "TMON@", true)]
    [InlineData("tinvest", "TMON@", "tinvest", "tmon", true)]
    [InlineData("tinvest", "TMON", "freedom", "TMON", false)]
    public void MembersConflict_BareKeyAndSuffixedFormsConflict(string brokerA, string keyA, string brokerB, string keyB, bool conflict)
    {
        var a = new CapitalBasketMember { Broker = brokerA, InstrumentKey = keyA };
        var b = new CapitalBasketMember { Broker = brokerB, InstrumentKey = keyB };

        BasketValidation.MembersConflict(a, b).Should().Be(conflict);
        BasketValidation.MembersConflict(b, a).Should().Be(conflict);
    }

    [Fact]
    public void MembersConflict_AssetClass_SameClassOnly()
    {
        CapitalBasketMember C(string k) => new() { Broker = "tinvest", InstrumentKey = k, MatchRule = BasketMatchRules.AssetClass };

        BasketValidation.MembersConflict(C("bond"), C("BOND")).Should().BeTrue();
        BasketValidation.MembersConflict(C("bond"), C("fund")).Should().BeFalse();
    }

    [Theory]
    [InlineData(0.15, 0.85, true)]
    [InlineData(0.15, 0.8501, true)]    // в допуске 0.0001
    [InlineData(0.15, 0.80, false)]
    [InlineData(0.50, 0.60, false)]
    public void TargetsSum_Tolerance(double a, double b, bool ok)
    {
        var result = BasketValidation.ValidateTargetsSum("rub", [B((decimal)a), B((decimal)b), B(null)]);

        (result is null).Should().Be(ok);
    }

    [Fact]
    public void TargetsSum_NoTargetsAtAll_IsSkipped() =>
        BasketValidation.ValidateTargetsSum("usd", [B(null), B(null)]).Should().BeNull();

    [Theory]
    [InlineData(0.05, 0.05)]   // band == hard
    [InlineData(0.10, 0.05)]   // band > hard
    public void Fields_BandMustBeLessThanHardBand(double band, double hard) =>
        BasketValidation.ValidateFields(B(0.5m, (decimal)band, (decimal)hard)).Should().Contain("band");

    [Fact]
    public void Fields_TargetOutOfRange_IsRejected() =>
        BasketValidation.ValidateFields(B(1.5m)).Should().Contain("targetWeight");

    [Fact]
    public void Member_BrokerMustMatchPortfolio() =>
        BasketValidation.ValidateMember(CapitalPortfolios.Rub, new CapitalBasketMember { Broker = BrokerIds.Freedom, InstrumentKey = "VUAA.EU" })
            .Should().Contain("broker");

    [Fact]
    public void Member_IdentityIgnoresCaseAndAtSign()
    {
        var a = new CapitalBasketMember { Broker = "tinvest", InstrumentKey = "TMON@" };
        var b = new CapitalBasketMember { Broker = "tinvest", InstrumentKey = "tmon" };

        BasketValidation.MemberIdentity(a).Should().Be(BasketValidation.MemberIdentity(b));
    }
}

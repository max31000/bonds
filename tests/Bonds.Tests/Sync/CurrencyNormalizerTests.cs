using Bonds.Infrastructure.Sync;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Sync;

/// <summary>
/// T-2 (находка N-2): валюта номинала определяется из MOEX FACEUNIT. Чистая функция-маппинг:
/// SUR/RUR → RUB (в скоупе), любая иная валюта (USD/EUR/…) → как есть и вне рублёвого скоупа MVP.
/// </summary>
public class CurrencyNormalizerTests
{
    [Fact]
    public void OutOfScopeCurrency_SetFromFaceUnit_Usd()
    {
        var (currency, outOfScope) = CurrencyNormalizer.Normalize("USD");

        currency.Should().Be("USD");
        outOfScope.Should().BeTrue("USD-номинал (замещающая облигация) вне рублёвого контура MVP");
    }

    [Theory]
    [InlineData("SUR")]
    [InlineData("RUB")]
    [InlineData("RUR")]
    [InlineData("rub")]
    public void RoubleCodes_NormalizeToRub_InScope(string faceUnit)
    {
        var (currency, outOfScope) = CurrencyNormalizer.Normalize(faceUnit);

        currency.Should().Be("RUB");
        outOfScope.Should().BeFalse();
    }

    [Fact]
    public void NullOrEmpty_DefaultsToRub_InScope()
    {
        CurrencyNormalizer.Normalize(null).Should().Be(("RUB", false));
        CurrencyNormalizer.Normalize("").Should().Be(("RUB", false));
    }
}

using Bonds.Core.Telegram;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Telegram;

public class BotCallbackTests
{
    [Fact]
    public void Nonce_Is8RandomBytes_Base64Url()
    {
        var a = BotCallback.NewNonce();
        var b = BotCallback.NewNonce();
        a.Should().MatchRegex("^[A-Za-z0-9_-]{11}$");
        a.Should().NotBe(b);
    }

    [Theory]
    [InlineData(BotPlanAction.Approve, "a")]
    [InlineData(BotPlanAction.Reject, "r")]
    [InlineData(BotPlanAction.Send, "s")]
    public void RoundTrip(BotPlanAction action, string code)
    {
        var nonce = BotCallback.NewNonce();
        var data = BotCallback.Build(18446744073709551615, action, nonce);

        data.Should().Be($"p:18446744073709551615:{code}:{nonce}");
        System.Text.Encoding.UTF8.GetByteCount(data).Should().BeLessThanOrEqualTo(64);
        BotCallback.TryParse(data, out var parsed).Should().BeTrue();
        parsed.Should().Be(new BotCallbackData(18446744073709551615, action, nonce));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("x:1:a:AbCdEfGh123")]
    [InlineData("p:1:a")]
    [InlineData("p:abc:a:AbCdEfGh123")]
    [InlineData("p:-1:a:AbCdEfGh123")]
    [InlineData("p:1:z:AbCdEfGh123")]
    [InlineData("p:1:a:short")]
    [InlineData("p:1:a:bad nonce!!!")]
    [InlineData("p:1:a:AbCdEfGh123:extra")]
    public void Garbage_IsRejected(string? data) => BotCallback.TryParse(data, out _).Should().BeFalse();

    [Fact]
    public void NonceEquals_ComparesExactly()
    {
        BotCallback.NonceEquals("AbCdEfGh123", "AbCdEfGh123").Should().BeTrue();
        BotCallback.NonceEquals("AbCdEfGh123", "AbCdEfGh124").Should().BeFalse();
        BotCallback.NonceEquals("AbCdEfGh123", "AbCdEfGh12").Should().BeFalse();
        BotCallback.NonceEquals(null, "AbCdEfGh123").Should().BeFalse();
        BotCallback.NonceEquals("AbCdEfGh123", null).Should().BeFalse();
    }
}

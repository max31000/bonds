using Bonds.Core.Models;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Signals;

public class SignalKeyTests
{
    [Fact]
    public void BuildKey_LongIssuerName_FitsColumnAndStaysDistinct()
    {
        var a = Signal.BuildKey(SignalType.ConcentrationLimitBreached, new string('А', 400) + "1", null);
        var b = Signal.BuildKey(SignalType.ConcentrationLimitBreached, new string('А', 400) + "2", null);

        a.Length.Should().BeLessThanOrEqualTo(255); // event_key VARCHAR(255)
        a.Should().NotBe(b);
    }

    [Fact]
    public void BuildKey_ShortSubject_Unchanged()
    {
        Signal.BuildKey(SignalType.UpcomingOffer, "42", new DateOnly(2026, 11, 3))
            .Should().Be("UpcomingOffer|42|2026-11-03");
    }
}

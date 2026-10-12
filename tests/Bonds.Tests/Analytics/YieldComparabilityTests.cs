using Bonds.Core.Analytics;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Analytics;

public class YieldComparabilityTests
{
    [Fact]
    public void IsComparable_FloaterOrIndexedOrDataIncomplete_ReturnsFalse()
    {
        YieldComparability.IsComparable(isFloater: true, isIndexed: false, dataIncomplete: false).Should().BeFalse();
        YieldComparability.IsComparable(isFloater: false, isIndexed: true, dataIncomplete: false).Should().BeFalse();
        YieldComparability.IsComparable(isFloater: false, isIndexed: false, dataIncomplete: true).Should().BeFalse();
        YieldComparability.IsComparable(isFloater: false, isIndexed: false, dataIncomplete: false).Should().BeTrue();
    }
}

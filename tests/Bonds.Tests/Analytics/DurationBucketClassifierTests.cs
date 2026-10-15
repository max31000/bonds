using Bonds.Core.Analytics;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Analytics;

public class DurationBucketClassifierTests
{
    [Theory]
    [InlineData(0.5, 0)]
    [InlineData(1.0, 1)]   // граница верхняя — не включается в нижний бакет (как Label)
    [InlineData(2.9, 1)]
    [InlineData(3.0, 2)]
    [InlineData(6.99, 3)]
    [InlineData(7.0, 4)]
    [InlineData(12.0, 4)]
    public void Index_MatchesLabelBoundaries(double years, int expected) =>
        DurationBucketClassifier.Index((decimal)years).Should().Be(expected);

    [Fact]
    public void Index_Null_ReturnsNull() => DurationBucketClassifier.Index(null).Should().BeNull();

    [Theory]
    [InlineData(2.0, 2.5, true)]   // тот же бакет
    [InlineData(2.0, 4.0, true)]   // соседний (1–3 и 3–5)
    [InlineData(2.0, 0.5, true)]   // соседний вниз
    [InlineData(2.0, 6.0, false)]  // через бакет
    [InlineData(0.5, 8.0, false)]
    public void AreSameOrAdjacent_ReturnsExpected(double a, double b, bool expected) =>
        DurationBucketClassifier.AreSameOrAdjacent((decimal)a, (decimal)b).Should().Be(expected);

    [Fact]
    public void AreSameOrAdjacent_AnyUnknown_IsFalse()
    {
        DurationBucketClassifier.AreSameOrAdjacent(null, 2m).Should().BeFalse();
        DurationBucketClassifier.AreSameOrAdjacent(2m, null).Should().BeFalse();
    }
}

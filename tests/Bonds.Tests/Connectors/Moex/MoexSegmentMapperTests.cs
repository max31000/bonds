using Bonds.Infrastructure.Connectors.Moex;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Connectors.Moex;

public class MoexSegmentMapperTests
{
    [Theory]
    [InlineData("ofz_bond", "Гособлигации")]
    [InlineData("subfederal_bond", "Муниципальные")]
    [InlineData("municipal_bond", "Муниципальные")]
    [InlineData("corporate_bond", "Корпоративные")]
    [InlineData("exchange_bond", "Корпоративные")]
    public void KnownTypes_MappedCorrectly(string typeCode, string expected)
    {
        MoexSegmentMapper.MapTypeToSegment(typeCode).Should().Be(expected);
    }

    [Fact]
    public void NullType_ReturnsNull()
    {
        MoexSegmentMapper.MapTypeToSegment(null).Should().BeNull();
    }

    [Fact]
    public void UnknownType_ReturnsNull()
    {
        MoexSegmentMapper.MapTypeToSegment("unknown_xyz").Should().BeNull();
    }
}

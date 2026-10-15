using Bonds.Core.Models;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Universe;

/// <summary>Задача 42: приоритет спреда банка — биржевой (marketdata_yields), иначе наше приближение по кривой.</summary>
public class BondUniverseEntryGspreadTests
{
    [Fact]
    public void EffectiveGspread_OfficialPresent_WinsOverApprox()
    {
        var e = new BondUniverseEntry { GspreadOfficialFraction = 0.0035m, GspreadApproxFraction = 0.0100m };

        e.EffectiveGspreadFraction.Should().Be(0.0035m);
        e.GspreadSource.Should().Be("moex");
    }

    [Fact]
    public void EffectiveGspread_OfficialMissing_FallsBackToApprox()
    {
        var e = new BondUniverseEntry { GspreadOfficialFraction = null, GspreadApproxFraction = 0.0100m };

        e.EffectiveGspreadFraction.Should().Be(0.0100m);
        e.GspreadSource.Should().Be("approx");
    }

    [Fact]
    public void EffectiveGspread_OfficialZero_IsStillOfficial_NotFallback()
    {
        // Нулевой биржевой спред — валидное значение (0 б.п.), а не "нет данных".
        var e = new BondUniverseEntry { GspreadOfficialFraction = 0m, GspreadApproxFraction = 0.0100m };

        e.EffectiveGspreadFraction.Should().Be(0m);
        e.GspreadSource.Should().Be("moex");
    }

    [Fact]
    public void EffectiveGspread_BothMissing_NullAndNoSource()
    {
        var e = new BondUniverseEntry();

        e.EffectiveGspreadFraction.Should().BeNull();
        e.GspreadSource.Should().BeNull();
    }

    [Fact]
    public void HistoryPoint_ResolvedGspread_PrefersEffective_FallsBackToLegacyApproxColumn()
    {
        new BondUniverseHistoryPoint { GspreadEffectiveFraction = 0.002m, GspreadApproxFraction = 0.009m }
            .ResolvedGspreadFraction.Should().Be(0.002m);
        new BondUniverseHistoryPoint { GspreadEffectiveFraction = null, GspreadApproxFraction = 0.009m }
            .ResolvedGspreadFraction.Should().Be(0.009m, "строки до миграции 017 хранят только приближённый спред");
    }
}

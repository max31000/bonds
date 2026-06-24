using Bonds.Core.Analytics;
using Bonds.Core.Models;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Analytics;

/// <summary>
/// Тесты сравнения/сортировки позиций (plan/06 B3, spec §9). Проверяет сортировку по доходности,
/// замену YTM на CurrentYield для флоатера/индексируемой бумаги, и наличие обязательного
/// дисклеймера о том, что более низкая доходность ≠ «хуже» (spec §9, §6).
/// </summary>
public class PositionComparisonServiceTests
{
    private static readonly DateOnly AsOf = new(2025, 1, 1);

    private static PortfolioHolding Holding(
        ulong positionId, decimal? ytm = null, decimal? currentYield = null,
        bool isFloater = false, bool isIndexed = false, decimal? duration = 3m,
        DateOnly? horizon = null, bool dataIncomplete = false) => new()
    {
        PositionId = positionId,
        InstrumentId = positionId,
        Quantity = 1,
        MarketValueRub = 1000m,
        CouponType = isFloater ? CouponType.Floating : isIndexed ? CouponType.Indexed : CouponType.Fixed,
        MaturityDate = new DateOnly(2030, 1, 1),
        HorizonDate = horizon ?? new DateOnly(2030, 1, 1),
        IsCalculatedToOffer = false,
        ModifiedDuration = duration,
        YtmEffective = ytm,
        CurrentYield = currentYield,
        IsFloater = isFloater,
        IsIndexed = isIndexed,
        IsEstimated = isFloater || isIndexed,
        DataIncomplete = dataIncomplete,
    };

    [Fact]
    public void Compare_SortsDescendingByEffectiveYield()
    {
        var holdings = new[]
        {
            Holding(1, ytm: 0.12m),
            Holding(2, ytm: 0.20m),
            Holding(3, ytm: 0.08m),
        };

        var result = PositionComparisonService.Compare(holdings, AsOf);

        result.Rows.Select(r => r.EffectiveYield).Should().BeInDescendingOrder();
        result.Rows[0].PositionId.Should().Be(2);
        result.Rows[^1].PositionId.Should().Be(3);
    }

    [Fact]
    public void Compare_Floater_UsesCurrentYieldNotYtm()
    {
        var holdings = new[] { Holding(1, ytm: null, currentYield: 0.09m, isFloater: true) };

        var result = PositionComparisonService.Compare(holdings, AsOf);

        var row = result.Rows.Single();
        row.YieldKind.Should().Be(YieldKind.CurrentYield);
        row.EffectiveYield.Should().Be(0.09m);
    }

    [Fact]
    public void Compare_IndexedBond_UsesCurrentYieldNotYtm()
    {
        var holdings = new[] { Holding(1, ytm: null, currentYield: 0.07m, isIndexed: true) };

        var result = PositionComparisonService.Compare(holdings, AsOf);

        result.Rows.Single().YieldKind.Should().Be(YieldKind.CurrentYield);
    }

    [Fact]
    public void Compare_FixedBond_UsesYtm()
    {
        var holdings = new[] { Holding(1, ytm: 0.15m, currentYield: 0.10m) };

        var result = PositionComparisonService.Compare(holdings, AsOf);

        var row = result.Rows.Single();
        row.YieldKind.Should().Be(YieldKind.Ytm);
        row.EffectiveYield.Should().Be(0.15m);
    }

    [Fact]
    public void Compare_PositionsWithoutYield_SortToTheEnd_NotDropped()
    {
        var holdings = new[]
        {
            Holding(1, ytm: 0.10m),
            Holding(2, ytm: null, currentYield: null, dataIncomplete: true),
        };

        var result = PositionComparisonService.Compare(holdings, AsOf);

        result.Rows.Should().HaveCount(2, "позиция без доходности не отбрасывается (spec §4.4)");
        result.Rows[^1].PositionId.Should().Be(2);
        result.Rows[^1].EffectiveYield.Should().BeNull();
    }

    [Fact]
    public void Compare_AlwaysIncludesDisclaimer()
    {
        var result = PositionComparisonService.Compare(Array.Empty<PortfolioHolding>(), AsOf);

        result.Disclaimer.Should().NotBeNullOrWhiteSpace();
        result.Disclaimer.Should().Contain("риск");
    }

    [Fact]
    public void Compare_ComputesDaysToHorizon()
    {
        var horizon = AsOf.AddDays(100);
        var holdings = new[] { Holding(1, ytm: 0.1m, horizon: horizon) };

        var result = PositionComparisonService.Compare(holdings, AsOf);

        result.Rows.Single().DaysToHorizon.Should().Be(100);
    }
}

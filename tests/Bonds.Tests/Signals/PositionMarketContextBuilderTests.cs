using Bonds.Core.Analytics;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Bonds.Core.Time;
using Bonds.Infrastructure.Universe;
using FluentAssertions;
using Moq;
using Xunit;

namespace Bonds.Tests.Signals;

/// <summary>Задача 49: батчевая сборка входов рыночных/кредитных событий (без N+1, без сегодняшней строки истории).</summary>
public class PositionMarketContextBuilderTests
{
    private static readonly DateOnly Today = BusinessClock.MoscowToday();

    private readonly Mock<IBondUniverseRepository> _universe = new();
    private readonly Mock<ISecurityDetailsRepository> _details = new();

    private PositionMarketContextBuilder Builder() => new(_universe.Object, _details.Object);

    private static PortfolioHolding Holding(ulong id, string? isin, string issuer = "Эмитент", bool floater = false, bool indexed = false) => new()
    {
        PositionId = id,
        InstrumentId = id,
        Quantity = 1m,
        MarketValueRub = 1000m,
        Isin = isin,
        Issuer = issuer,
        Name = $"Бумага {id}",
        CouponType = CouponType.Fixed,
        MaturityDate = Today.AddYears(3),
        HorizonDate = Today.AddYears(3),
        IsCalculatedToOffer = false,
        IsFloater = floater,
        IsIndexed = indexed,
        IsEstimated = false,
        DataIncomplete = false,
    };

    private static BondUniverseEntry Entry(string secid, string isin, decimal? gspread = 0.02m, decimal? price = 99m, int? emitentId = null, bool? floater = false) => new()
    {
        Secid = secid, Isin = isin, GspreadOfficialFraction = gspread, PricePercent = price, EmitentId = emitentId, EmitentTitle = "Эмитент (MOEX)", IsFloater = floater,
    };

    private static BondUniverseHistoryPoint Point(string secid, DateOnly date, decimal? gspread, decimal? price) => new()
    {
        Secid = secid, SnapshotDate = date, GspreadEffectiveFraction = gspread, PricePercent = price,
    };

    [Fact]
    public async Task Build_UsesBankEntryAndHistory_ExcludesTodayRow_AndKeepsLatest20Days()
    {
        _universe.Setup(u => u.GetByIsinsAsync(It.IsAny<IReadOnlyCollection<string>>(), default))
            .ReturnsAsync([Entry("SU1", "RU000A0TEST1", gspread: 0.03m, price: 95m)]);
        var history = Enumerable.Range(0, 22).Select(d => Point("SU1", Today.AddDays(-d), 0.02m + d * 0.0001m, 100m - d)).ToList();
        _universe.Setup(u => u.GetRecentHistoryForSecidsAsync(It.IsAny<IReadOnlyCollection<string>>(), PositionMarketContextBuilder.HistoryFetchDays, default))
            .ReturnsAsync(history);
        _details.Setup(d => d.GetBySecidsAsync(It.IsAny<IReadOnlyCollection<string>>(), default)).ReturnsAsync([]);

        var result = await Builder().BuildAsync([Holding(1, "RU000A0TEST1")], Today);

        var market = result.Market.Should().ContainSingle().Subject;
        market.InstrumentId.Should().Be(1ul);
        market.Isin.Should().Be("RU000A0TEST1");
        market.GSpreadFraction.Should().Be(0.03m);
        market.PricePercent.Should().Be(95m);
        market.GSpreadHistory.Should().HaveCount(PositionMarketContextBuilder.HistoryDays, "сегодняшняя строка исключена, берутся 20 последних торговых дней");
        market.GSpreadHistory.Should().NotContain(0.02m, "значение сегодняшней строки (d=0) в историю не попадает");
        market.PriceHistory.Should().NotContain(100m);
    }

    [Fact]
    public async Task Build_PositionOutsideBank_OrWithoutIsin_HasNoMarketContext()
    {
        _universe.Setup(u => u.GetByIsinsAsync(It.IsAny<IReadOnlyCollection<string>>(), default)).ReturnsAsync([]);
        _universe.Setup(u => u.GetRecentHistoryForSecidsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<int>(), default)).ReturnsAsync([]);
        _details.Setup(d => d.GetBySecidsAsync(It.IsAny<IReadOnlyCollection<string>>(), default)).ReturnsAsync([]);

        var result = await Builder().BuildAsync([Holding(1, "RU000A0NOBANK"), Holding(2, null)], Today);

        result.Market.Should().BeEmpty();
        result.IssuerDefaults.Should().BeEmpty();
    }

    [Fact]
    public async Task Build_FloaterFlag_FromHoldingOrBank()
    {
        _universe.Setup(u => u.GetByIsinsAsync(It.IsAny<IReadOnlyCollection<string>>(), default))
            .ReturnsAsync([Entry("SU1", "I1", floater: false), Entry("SU2", "I2", floater: true), Entry("SU3", "I3", floater: false)]);
        _universe.Setup(u => u.GetRecentHistoryForSecidsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<int>(), default)).ReturnsAsync([]);
        _details.Setup(d => d.GetBySecidsAsync(It.IsAny<IReadOnlyCollection<string>>(), default)).ReturnsAsync([]);

        var result = await Builder().BuildAsync([Holding(1, "I1"), Holding(2, "I2"), Holding(3, "I3", floater: true)], Today);

        result.Market.Single(m => m.InstrumentId == 1).IsFloater.Should().BeFalse();
        result.Market.Single(m => m.InstrumentId == 2).IsFloater.Should().BeTrue("банк пометил флоатером");
        result.Market.Single(m => m.InstrumentId == 3).IsFloater.Should().BeTrue("движок пометил флоатером");
    }

    [Fact]
    public async Task Build_IssuerDefaults_OnlyEmitentsWithFlag_OneContextPerEmitent_BatchedLookups()
    {
        _universe.Setup(u => u.GetByIsinsAsync(It.IsAny<IReadOnlyCollection<string>>(), default))
            .ReturnsAsync([Entry("SU1", "I1", emitentId: 10), Entry("SU2", "I2", emitentId: 10), Entry("SU3", "I3", emitentId: 20), Entry("SU4", "I4")]);
        _universe.Setup(u => u.GetRecentHistoryForSecidsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<int>(), default)).ReturnsAsync([]);
        // SU4 без emitent_id в банке — эмитент берётся из карточки (30)
        _details.Setup(d => d.GetBySecidsAsync(It.IsAny<IReadOnlyCollection<string>>(), default))
            .ReturnsAsync([new SecurityDetails { Secid = "SU4", EmitentId = 30 }]);
        _details.Setup(d => d.GetEmitentDefaultHistoryAsync(It.IsAny<IReadOnlyCollection<int>>(), default))
            .ReturnsAsync(new Dictionary<int, EmitentDefaultHistory>
            {
                [10] = new(10, true, false),
                [20] = new(20, false, false),
                [30] = new(30, null, true),
            });

        var result = await Builder().BuildAsync(
            [Holding(1, "I1"), Holding(2, "I2"), Holding(3, "I3"), Holding(4, "I4")], Today);

        result.IssuerDefaults.Should().HaveCount(2);
        var first = result.IssuerDefaults.Single(d => d.EmitentId == 10);
        first.HasDefault.Should().BeTrue();
        first.HasTechnicalDefault.Should().BeFalse();
        first.InstrumentId.Should().Be(1ul, "ссылка — на первую позицию эмитента");
        first.Subject.Should().Be("Эмитент", "название — по данным портфеля (issuer позиции)");
        var third = result.IssuerDefaults.Single(d => d.EmitentId == 30);
        third.HasDefault.Should().BeFalse("null = неизвестно, не дефолт");
        third.HasTechnicalDefault.Should().BeTrue();

        _universe.Verify(u => u.GetByIsinsAsync(It.IsAny<IReadOnlyCollection<string>>(), default), Times.Once);
        _universe.Verify(u => u.GetRecentHistoryForSecidsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<int>(), default), Times.Once);
        _details.Verify(d => d.GetEmitentDefaultHistoryAsync(It.IsAny<IReadOnlyCollection<int>>(), default), Times.Once);
    }

    [Fact]
    public async Task Build_NoHoldings_NoRepositoryCalls()
    {
        var result = await Builder().BuildAsync([], Today);

        result.Market.Should().BeEmpty();
        _universe.VerifyNoOtherCalls();
        _details.VerifyNoOtherCalls();
    }
}

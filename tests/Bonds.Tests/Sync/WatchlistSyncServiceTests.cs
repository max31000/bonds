using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Bonds.Infrastructure.Connectors.Moex;
using Bonds.Infrastructure.Connectors.TInvest;
using Bonds.Infrastructure.Sync;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Bonds.Tests.Sync;

/// <summary>
/// Задача 20 (часть A): синк watchlist-бумаг (ISIN без позиции) — переиспользует
/// <see cref="BondSyncService.ResolveOrCreateInstrumentByIsinAsync"/> (тот же путь заведения
/// инструмента, что у позиций) и дополнительно пишет MOEX-котировку (движок сам считает НКД
/// fallback'ом — см. doc-comment <see cref="WatchlistSyncService"/>).
/// </summary>
public class WatchlistSyncServiceTests
{
    private readonly Mock<IWatchlistItemRepository> _watchlistItems = new();
    private readonly Mock<IInstrumentRepository> _instruments = new();
    private readonly Mock<IMoexIssClient> _moex = new();
    private readonly Mock<IMarketQuoteRepository> _quotes = new();

    // BondSyncService dependencies (нужен реальный экземпляр — ResolveOrCreateInstrumentByIsinAsync не виртуальный).
    private readonly Mock<ITInvestPortfolioClient> _tInvest = new();
    private readonly Mock<ICouponScheduleRepository> _coupons = new();
    private readonly Mock<IAmortizationScheduleRepository> _amortizations = new();
    private readonly Mock<IOfferScheduleRepository> _offers = new();
    private readonly Mock<IYieldCurveRepository> _yieldCurve = new();
    private readonly Mock<IPositionRepository> _positions = new();
    private readonly Mock<IOperationRepository> _operations = new();

    private const string Isin = "RU000A1038V6";
    private const string Secid = "SU26238RMFS4";
    private const ulong InstrumentId = 7;

    private BondSyncService CreateBondSyncService() => new(
        _tInvest.Object,
        _moex.Object,
        _instruments.Object,
        _coupons.Object,
        _amortizations.Object,
        _offers.Object,
        _quotes.Object,
        _yieldCurve.Object,
        _positions.Object,
        _operations.Object,
        NullLogger<BondSyncService>.Instance);

    private WatchlistSyncService CreateService() => new(
        _watchlistItems.Object,
        _instruments.Object,
        _moex.Object,
        _quotes.Object,
        CreateBondSyncService(),
        NullLogger<WatchlistSyncService>.Instance);

    private void SetupInstrumentAndBondization(ulong instrumentId = InstrumentId, string isin = Isin, string? secid = Secid)
    {
        _instruments.Setup(r => r.GetByIsinAsync(isin)).ReturnsAsync(new Instrument
        {
            Id = instrumentId,
            Isin = isin,
            Secid = secid,
            FaceValue = 1000m,
            MaturityDate = new DateOnly(2041, 5, 15),
        });
        _instruments.Setup(r => r.GetByIdAsync(instrumentId)).ReturnsAsync(new Instrument
        {
            Id = instrumentId,
            Isin = isin,
            Secid = secid,
            FaceValue = 1000m,
            MaturityDate = new DateOnly(2041, 5, 15),
        });
        _instruments.Setup(r => r.UpsertAsync(It.IsAny<Instrument>())).ReturnsAsync(instrumentId);

        if (secid is not null)
        {
            _moex.Setup(m => m.GetBondizationAsync(secid, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MoexBondizationResult { Secid = secid });
        }
    }

    [Fact]
    public async Task SyncAllAsync_ResolvesInstrumentAndWritesMoexQuote()
    {
        _watchlistItems.Setup(w => w.GetAllAsync()).ReturnsAsync(new List<WatchlistItem>
        {
            new() { Id = 1, UserId = 1, Isin = Isin },
        });

        SetupInstrumentAndBondization();
        _moex.Setup(m => m.GetSecurityInfoAsync(Secid, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MoexSecurityInfo
            {
                Secid = Secid,
                BoardId = "TQOB",
                FaceValue = 1000m,
                MatDate = new DateOnly(2041, 5, 15),
                PrevPrice = 98.5m,
            });

        var service = CreateService();
        var result = await service.SyncAllAsync();

        result.HasErrors.Should().BeFalse();
        result.InstrumentsSynced.Should().Be(1);

        _quotes.Verify(q => q.UpsertAsync(It.Is<MarketQuote>(mq =>
            mq.InstrumentId == InstrumentId
            && mq.Source == MarketQuoteSource.Moex
            && mq.CleanPrice == 985m // 98.5% of FaceValue 1000
            && mq.Accrued == null)), Times.Once);
    }

    [Fact]
    public async Task SyncAllAsync_IsinNotFoundOnMoex_RecordsErrorButDoesNotThrow()
    {
        const string unknownIsin = "RU000UNKNOWN0";
        _watchlistItems.Setup(w => w.GetAllAsync()).ReturnsAsync(new List<WatchlistItem>
        {
            new() { Id = 2, UserId = 1, Isin = unknownIsin },
        });

        _instruments.Setup(r => r.GetByIsinAsync(unknownIsin)).ReturnsAsync((Instrument?)null);
        _instruments.Setup(r => r.UpsertAsync(It.IsAny<Instrument>())).ReturnsAsync(InstrumentId);
        _instruments.Setup(r => r.GetByIdAsync(InstrumentId)).ReturnsAsync(new Instrument
        {
            Id = InstrumentId,
            Isin = unknownIsin,
            Secid = null,
        });
        _moex.Setup(m => m.ResolveSecidByIsinAsync(unknownIsin, It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        var service = CreateService();
        var result = await service.SyncAllAsync();

        result.InstrumentsSynced.Should().Be(1, "инструмент всё равно заводится в справочник с пометкой неполноты, как у позиций");
        _quotes.Verify(q => q.UpsertAsync(It.IsAny<MarketQuote>()), Times.Never);
    }

    [Fact]
    public async Task SyncAllAsync_DuplicateIsinAcrossUsers_SyncedOnce()
    {
        _watchlistItems.Setup(w => w.GetAllAsync()).ReturnsAsync(new List<WatchlistItem>
        {
            new() { Id = 1, UserId = 1, Isin = Isin },
            new() { Id = 2, UserId = 2, Isin = Isin },
        });

        SetupInstrumentAndBondization();
        _moex.Setup(m => m.GetSecurityInfoAsync(Secid, It.IsAny<CancellationToken>()))
            .ReturnsAsync((MoexSecurityInfo?)null);

        var service = CreateService();
        var result = await service.SyncAllAsync();

        result.InstrumentsSynced.Should().Be(1);
        _moex.Verify(m => m.GetBondizationAsync(Secid, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SyncAllAsync_OneIsinThrows_OthersStillSynced()
    {
        const string secondIsin = "RU000SECOND01";

        _watchlistItems.Setup(w => w.GetAllAsync()).ReturnsAsync(new List<WatchlistItem>
        {
            new() { Id = 1, UserId = 1, Isin = Isin },
            new() { Id = 2, UserId = 1, Isin = secondIsin },
        });

        SetupInstrumentAndBondization();
        _moex.Setup(m => m.GetSecurityInfoAsync(Secid, It.IsAny<CancellationToken>()))
            .ReturnsAsync((MoexSecurityInfo?)null);

        _instruments.Setup(r => r.GetByIsinAsync(secondIsin)).ThrowsAsync(new InvalidOperationException("boom"));

        var service = CreateService();
        var result = await service.SyncAllAsync();

        result.InstrumentsSynced.Should().Be(1);
        result.Errors.Should().ContainSingle(e => e.Contains(secondIsin));
    }
}

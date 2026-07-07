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
/// Задача 27 часть A: путь «обогатить одну бумагу по ISIN», вынесенный из
/// <see cref="WatchlistSyncService"/> — общий для watchlist (задача 20) и материализации из банка
/// облигаций (<c>POST /api/universe/{secid}/materialize</c>). Тесты дублируют сценарии
/// <see cref="WatchlistSyncServiceTests"/>, проверяя сам вынесенный сервис напрямую.
/// </summary>
public class InstrumentEnrichmentServiceTests
{
    private readonly Mock<IInstrumentRepository> _instruments = new();
    private readonly Mock<IMoexIssClient> _moex = new();
    private readonly Mock<IMarketQuoteRepository> _quotes = new();

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

    private InstrumentEnrichmentService CreateService() => new(
        _instruments.Object,
        _moex.Object,
        _quotes.Object,
        CreateBondSyncService());

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
    public async Task EnrichByIsinAsync_KnownIsin_ResolvesInstrumentAndWritesMoexQuote()
    {
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
        var instrumentId = await service.EnrichByIsinAsync(Isin);

        instrumentId.Should().Be(InstrumentId);
        _quotes.Verify(q => q.UpsertAsync(It.Is<MarketQuote>(mq =>
            mq.InstrumentId == InstrumentId
            && mq.Source == MarketQuoteSource.Moex
            && mq.CleanPrice == 985m // 98.5% of FaceValue 1000
            && mq.Accrued == null)), Times.Once);
    }

    [Fact]
    public async Task EnrichByIsinAsync_IsinNotFoundOnMoex_ReturnsInstrumentIdButNoQuote()
    {
        const string unknownIsin = "RU000UNKNOWN0";
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
        var instrumentId = await service.EnrichByIsinAsync(unknownIsin);

        // Инструмент всё равно заводится в справочник с пометкой неполноты, как у позиций.
        instrumentId.Should().Be(InstrumentId);
        _quotes.Verify(q => q.UpsertAsync(It.IsAny<MarketQuote>()), Times.Never);
    }

    [Fact]
    public async Task EnrichByIsinAsync_CalledTwice_IsIdempotent()
    {
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
        var first = await service.EnrichByIsinAsync(Isin);
        var second = await service.EnrichByIsinAsync(Isin);

        first.Should().Be(InstrumentId);
        second.Should().Be(InstrumentId);
        // ResolveOrCreateInstrumentByIsinAsync находит существующий по ISIN — не заводит новый (UpsertAsync не вызывается повторно с placeholder).
        _instruments.Verify(r => r.GetByIsinAsync(Isin), Times.Exactly(2));
        _quotes.Verify(q => q.UpsertAsync(It.IsAny<MarketQuote>()), Times.Exactly(2));
    }
}

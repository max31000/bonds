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
/// Тесты оркестратора синка (plan/04 Часть C) с замоканными ITInvestPortfolioClient/IMoexIssClient
/// и репозиториями — без реальных gRPC/HTTP вызовов (plan/04 "Тесты": интеграционные с замоканным
/// клиентом T-Invest). Проверяет: маппинг позиций/операций, идемпотентность, устойчивость к
/// частичному сбою одного инструмента, пометки неполноты при отсутствии НКД/SECID.
/// </summary>
public class BondSyncServiceTests
{
    private readonly Mock<ITInvestPortfolioClient> _tInvest = new();
    private readonly Mock<IMoexIssClient> _moex = new();
    private readonly Mock<IInstrumentRepository> _instruments = new();
    private readonly Mock<ICouponScheduleRepository> _coupons = new();
    private readonly Mock<IAmortizationScheduleRepository> _amortizations = new();
    private readonly Mock<IOfferScheduleRepository> _offers = new();
    private readonly Mock<IMarketQuoteRepository> _quotes = new();
    private readonly Mock<IYieldCurveRepository> _yieldCurve = new();
    private readonly Mock<IPositionRepository> _positions = new();
    private readonly Mock<IOperationRepository> _operations = new();

    private const ulong AccountId = 42;
    private const string BrokerAccountId = "BA-1";
    private const string Figi = "BBG00FXBVTV0";
    private const string Isin = "RU000A1038V6";
    private const ulong InstrumentId = 7;

    private BondSyncService CreateService() => new(
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

    private void SetupHappyPathAccountAndPositions(decimal? currentNkd = 12.3m, decimal? currentPrice = 985.5m)
    {
        _tInvest.Setup(c => c.GetPrimaryAccountIdAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(BrokerAccountId);

        _tInvest.Setup(c => c.GetBondPositionsAsync(BrokerAccountId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TInvestPortfolioPosition>
            {
                new()
                {
                    Figi = Figi,
                    InstrumentUid = "uid-1",
                    Quantity = 10,
                    AveragePositionPrice = 980m,
                    CurrentPrice = currentPrice,
                    CurrentNkd = currentNkd,
                },
            });

        _tInvest.Setup(c => c.GetOperationsAsync(BrokerAccountId, It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TInvestOperation>());

        _tInvest.Setup(c => c.GetQuotesAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, TInvestQuote>());

        _instruments.Setup(r => r.GetByFigiAsync(Figi)).ReturnsAsync(new Instrument
        {
            Id = InstrumentId,
            Isin = Isin,
            Figi = Figi,
            Secid = "SU26238RMFS4",
            FaceValue = 1000m,
            MaturityDate = new DateOnly(2041, 5, 15),
        });
        _instruments.Setup(r => r.GetByIdAsync(InstrumentId)).ReturnsAsync(new Instrument
        {
            Id = InstrumentId,
            Isin = Isin,
            Figi = Figi,
            Secid = "SU26238RMFS4",
            FaceValue = 1000m,
            MaturityDate = new DateOnly(2041, 5, 15),
        });
        _instruments.Setup(r => r.UpsertAsync(It.IsAny<Instrument>())).ReturnsAsync(InstrumentId);

        _moex.Setup(m => m.GetSecurityInfoAsync("SU26238RMFS4", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MoexSecurityInfo
            {
                Secid = "SU26238RMFS4",
                BoardId = "TQOB",
                FaceValue = 1000m,
                MatDate = new DateOnly(2041, 5, 15),
                CouponPercent = 7.1m,
            });
        _moex.Setup(m => m.GetBondizationAsync("SU26238RMFS4", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MoexBondizationResult
            {
                Secid = "SU26238RMFS4",
                Coupons = [new CouponSchedule { CouponDate = new DateOnly(2026, 12, 8), ValueRub = 35.4m, IsKnown = true }],
            });
        _moex.Setup(m => m.GetYieldCurveAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((YieldCurveSnapshot?)null);
    }

    [Fact]
    public async Task SyncAsync_HappyPath_UpsertsPositionAndQuote_NoErrors()
    {
        SetupHappyPathAccountAndPositions();
        var service = CreateService();

        var result = await service.SyncAsync(AccountId);

        result.Errors.Should().BeEmpty();
        result.InstrumentsSynced.Should().Be(1);

        _positions.Verify(p => p.UpsertAsync(It.Is<Position>(pos =>
            pos.AccountId == AccountId
            && pos.InstrumentId == InstrumentId
            && pos.Quantity == 10
            && pos.AvgPurchasePrice == 980m
            && pos.Accrued == 12.3m
            && pos.DataIncomplete == false)), Times.Once);

        _quotes.Verify(q => q.UpsertAsync(It.Is<MarketQuote>(mq =>
            mq.InstrumentId == InstrumentId
            && mq.Source == MarketQuoteSource.TInvest
            && mq.CleanPrice == 985.5m)), Times.AtLeastOnce);
    }

    [Fact]
    public async Task SyncAsync_PortfolioAlreadyHasCurrentPrice_DoesNotOverwriteQuoteWithoutAccrued()
    {
        // Регрессия: GetQuotesAsync (часть 3, fallback) не несёт НКД (только LastPrice). Если бы он
        // писал поверх котировки из портфеля для того же (InstrumentId, AsOf, Source), upsert
        // ON DUPLICATE KEY UPDATE обнулил бы уже сохранённый Accrued/DirtyPrice (см. историю
        // реализации этапа 04 — найдено при самопроверке). Эта позиция получает CurrentPrice из
        // портфеля, поэтому GetQuotesAsync вообще не должен запрашиваться для неё.
        SetupHappyPathAccountAndPositions();
        var service = CreateService();

        await service.SyncAsync(AccountId);

        _tInvest.Verify(c => c.GetQuotesAsync(
            It.Is<IReadOnlyCollection<string>>(figis => figis.Contains(Figi)),
            It.IsAny<CancellationToken>()), Times.Never);

        _quotes.Verify(q => q.UpsertAsync(It.Is<MarketQuote>(mq =>
            mq.InstrumentId == InstrumentId && mq.Accrued == 12.3m)), Times.Once);
    }

    [Fact]
    public async Task SyncAsync_NoAccruedInterestFromBroker_MarksPositionDataIncomplete()
    {
        // spec §4.4: НКД отсутствующий у брокера по открытой позиции — не подставляем 0 как факт,
        // помечаем позицию неполной.
        SetupHappyPathAccountAndPositions(currentNkd: null);
        var service = CreateService();

        await service.SyncAsync(AccountId);

        _positions.Verify(p => p.UpsertAsync(It.Is<Position>(pos => pos.DataIncomplete == true)), Times.Once);
    }

    [Fact]
    public async Task SyncAsync_NoBrokerAccount_ReturnsErrorWithoutThrowing()
    {
        _tInvest.Setup(c => c.GetPrimaryAccountIdAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        var service = CreateService();

        var result = await service.SyncAsync(AccountId);

        result.Errors.Should().ContainSingle();
        result.InstrumentsSynced.Should().Be(0);
    }

    [Fact]
    public async Task SyncAsync_OneInstrumentThrows_OthersStillSynced_PartialFailureIsolated()
    {
        const string secondFigi = "BBG00SECOND0";
        const ulong secondInstrumentId = 8;

        _tInvest.Setup(c => c.GetPrimaryAccountIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(BrokerAccountId);
        _tInvest.Setup(c => c.GetOperationsAsync(BrokerAccountId, It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TInvestOperation>());
        _tInvest.Setup(c => c.GetQuotesAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, TInvestQuote>());

        _tInvest.Setup(c => c.GetBondPositionsAsync(BrokerAccountId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TInvestPortfolioPosition>
            {
                new() { Figi = Figi, InstrumentUid = "uid-1", Quantity = 10, AveragePositionPrice = 980m, CurrentNkd = 1m },
                new() { Figi = secondFigi, InstrumentUid = "uid-2", Quantity = 5, AveragePositionPrice = 500m, CurrentNkd = 1m },
            });

        // Первый инструмент резолвится нормально...
        _instruments.Setup(r => r.GetByFigiAsync(Figi)).ReturnsAsync(new Instrument { Id = InstrumentId, Isin = Isin, Secid = "SU26238RMFS4" });
        _instruments.Setup(r => r.GetByIdAsync(InstrumentId)).ReturnsAsync(new Instrument { Id = InstrumentId, Isin = Isin, Secid = "SU26238RMFS4" });
        _moex.Setup(m => m.GetSecurityInfoAsync("SU26238RMFS4", It.IsAny<CancellationToken>())).ReturnsAsync((MoexSecurityInfo?)null);
        _moex.Setup(m => m.GetBondizationAsync("SU26238RMFS4", It.IsAny<CancellationToken>())).ReturnsAsync(new MoexBondizationResult { Secid = "SU26238RMFS4" });

        // ...а второй ломается при резолве (например, временный сбой MOEX).
        _instruments.Setup(r => r.GetByFigiAsync(secondFigi)).ReturnsAsync(new Instrument { Id = secondInstrumentId, Isin = "RU000BROKEN0", Secid = null });
        _instruments.Setup(r => r.GetByIdAsync(secondInstrumentId)).ReturnsAsync(new Instrument { Id = secondInstrumentId, Isin = "RU000BROKEN0", Secid = null });
        _moex.Setup(m => m.ResolveSecidByIsinAsync("RU000BROKEN0", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("simulated MOEX outage"));

        _moex.Setup(m => m.GetYieldCurveAsync(It.IsAny<CancellationToken>())).ReturnsAsync((YieldCurveSnapshot?)null);

        var service = CreateService();

        var result = await service.SyncAsync(AccountId);

        result.InstrumentsSynced.Should().Be(1, "первый инструмент должен синкнуться, несмотря на сбой второго");
        result.Errors.Should().ContainSingle(e => e.Contains(secondFigi));

        _positions.Verify(p => p.UpsertAsync(It.Is<Position>(pos => pos.InstrumentId == InstrumentId)), Times.Once);
        _positions.Verify(p => p.UpsertAsync(It.Is<Position>(pos => pos.InstrumentId == secondInstrumentId)), Times.Never);
    }

    [Fact]
    public async Task SyncAsync_OperationsFromTInvest_MappedAndUpsertedIdempotently()
    {
        SetupHappyPathAccountAndPositions();
        _tInvest.Setup(c => c.GetOperationsAsync(BrokerAccountId, It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TInvestOperation>
            {
                new() { Id = "op-1", OperationType = "Buy", Figi = Figi, Date = new DateTime(2026, 1, 10), PaymentRub = -9800m, Quantity = 10 },
                new() { Id = "op-2", OperationType = "Coupon", Figi = Figi, Date = new DateTime(2026, 6, 8), PaymentRub = 354m },
                new() { Id = "op-3", OperationType = "Dividend", Date = new DateTime(2026, 6, 9), PaymentRub = 100m }, // вне скоупа — должен быть отфильтрован
            });
        _operations.Setup(o => o.UpsertManyByExternalIdAsync(It.IsAny<IEnumerable<Operation>>()))
            .ReturnsAsync((IEnumerable<Operation> ops) => ops.Count());

        var service = CreateService();

        var result = await service.SyncAsync(AccountId);

        result.OperationsUpserted.Should().Be(2, "операция типа Dividend вне скоупа продукта и должна быть отфильтрована");
        _operations.Verify(o => o.UpsertManyByExternalIdAsync(It.Is<IEnumerable<Operation>>(ops =>
            ops.Any(op => op.ExternalId == "op-1" && op.Type == OperationType.Buy)
            && ops.Any(op => op.ExternalId == "op-2" && op.Type == OperationType.Coupon)
            && !ops.Any(op => op.ExternalId == "op-3"))), Times.Once);
    }

    [Fact]
    public async Task SyncAsync_MoexSecidNotFound_MarksInstrumentDataIncomplete_DoesNotThrow()
    {
        _tInvest.Setup(c => c.GetPrimaryAccountIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(BrokerAccountId);
        _tInvest.Setup(c => c.GetOperationsAsync(BrokerAccountId, It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TInvestOperation>());
        _tInvest.Setup(c => c.GetQuotesAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, TInvestQuote>());
        _tInvest.Setup(c => c.GetBondPositionsAsync(BrokerAccountId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TInvestPortfolioPosition>
            {
                new() { Figi = Figi, InstrumentUid = "uid-1", Quantity = 1, AveragePositionPrice = 100m, CurrentNkd = 0m },
            });

        _instruments.Setup(r => r.GetByFigiAsync(Figi)).ReturnsAsync(new Instrument { Id = InstrumentId, Isin = "RU000NOTFOUND", Secid = null });
        _instruments.Setup(r => r.GetByIdAsync(InstrumentId)).ReturnsAsync(new Instrument { Id = InstrumentId, Isin = "RU000NOTFOUND", Secid = null });
        _instruments.Setup(r => r.UpsertAsync(It.IsAny<Instrument>())).ReturnsAsync(InstrumentId);
        _moex.Setup(m => m.ResolveSecidByIsinAsync("RU000NOTFOUND", It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
        _moex.Setup(m => m.GetYieldCurveAsync(It.IsAny<CancellationToken>())).ReturnsAsync((YieldCurveSnapshot?)null);

        var service = CreateService();

        var result = await service.SyncAsync(AccountId);

        result.InstrumentsSynced.Should().Be(1, "позиция всё равно синкается, даже если MOEX не нашёл SECID");
        _instruments.Verify(r => r.UpsertAsync(It.Is<Instrument>(i => i.DataIncomplete == true)), Times.AtLeastOnce);
    }

    [Fact]
    public async Task SyncAsync_YieldCurveAvailable_UpsertsSnapshot()
    {
        SetupHappyPathAccountAndPositions();
        var snapshot = new YieldCurveSnapshot { AsOf = new DateOnly(2026, 6, 24), B1 = 1, B2 = 2, B3 = 3, T1 = 1 };
        _moex.Setup(m => m.GetYieldCurveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(snapshot);

        var service = CreateService();
        var result = await service.SyncAsync(AccountId);

        result.YieldCurveUpdated.Should().BeTrue();
        _yieldCurve.Verify(y => y.UpsertAsync(snapshot), Times.Once);
    }
}

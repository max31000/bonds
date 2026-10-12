using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Bonds.Core.Time;
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
    private readonly Mock<IAccountRepository> _accounts = new();

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
        _accounts.Object,
        NullLogger<BondSyncService>.Instance);

    private void SetupHappyPathAccountAndPositions(decimal? currentNkd = 12.3m, decimal? currentPrice = 985.5m)
    {
        _tInvest.Setup(c => c.GetPrimaryAccountIdAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(BrokerAccountId);

        _tInvest.Setup(c => c.GetPortfolioAsync(BrokerAccountId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TInvestPortfolio
            {
                Bonds = new List<TInvestPortfolioPosition>
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
    public async Task SyncAsync_ReconcilesPositions_DeletesEverythingBrokerNoLongerHolds()
    {
        // Баг ОФЗ 26226: погашенная бумага пропадает из GetPortfolio, а в positions оставалась навсегда.
        SetupHappyPathAccountAndPositions();
        _positions.Setup(p => p.DeleteAllExceptAsync(AccountId, It.IsAny<IReadOnlyCollection<ulong>>())).ReturnsAsync(1);

        var result = await CreateService().SyncAsync(AccountId);

        result.PositionsClosed.Should().Be(1);
        _positions.Verify(p => p.DeleteAllExceptAsync(AccountId,
            It.Is<IReadOnlyCollection<ulong>>(keep => keep.Count == 1 && keep.Contains(InstrumentId))), Times.Once);
    }

    [Fact]
    public async Task SyncAsync_BrokerHoldsNoBonds_DeletesAllPositions()
    {
        SetupHappyPathAccountAndPositions();
        _tInvest.Setup(c => c.GetPortfolioAsync(BrokerAccountId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TInvestPortfolio { Bonds = [], CashRub = 16_000m });

        await CreateService().SyncAsync(AccountId);

        _positions.Verify(p => p.DeleteAllExceptAsync(AccountId,
            It.Is<IReadOnlyCollection<ulong>>(keep => keep.Count == 0)), Times.Once);
    }

    [Fact]
    public async Task SyncAsync_BrokerFigiNotResolvable_SkipsReconciliation()
    {
        // Не смогли сопоставить бумагу брокера с инструментом — не знаем, какие позиции живые;
        // удалять ничего нельзя.
        SetupHappyPathAccountAndPositions();
        _tInvest.Setup(c => c.GetPortfolioAsync(BrokerAccountId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TInvestPortfolio
            {
                Bonds = [new() { Figi = "UNKNOWN", InstrumentUid = "uid-x", Quantity = 1, CurrentNkd = 0m }],
            });
        _tInvest.Setup(c => c.GetIsinByFigiAsync("UNKNOWN", It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);

        await CreateService().SyncAsync(AccountId);

        _positions.Verify(p => p.DeleteAllExceptAsync(It.IsAny<ulong>(), It.IsAny<IReadOnlyCollection<ulong>>()), Times.Never);
    }

    [Fact]
    public async Task SyncAsync_StoresCashBalanceFromBroker()
    {
        SetupHappyPathAccountAndPositions();
        _tInvest.Setup(c => c.GetPortfolioAsync(BrokerAccountId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TInvestPortfolio { Bonds = [], CashRub = 8_123.45m, TotalPortfolioRub = 173_000m });

        await CreateService().SyncAsync(AccountId);

        _accounts.Verify(a => a.UpdateBalanceAsync(AccountId, 8_123.45m, 173_000m, It.IsAny<DateTime>()), Times.Once);
    }

    [Fact]
    public async Task SyncAsync_UsesPinnedBrokerAccount_DoesNotFallBackToFirstOpenAccount()
    {
        // Закреплённый счёт важнее «первого открытого»: иначе открытие второго счёта переключило бы
        // синк, а сверка стёрла бы весь портфель.
        SetupHappyPathAccountAndPositions();
        _accounts.Setup(a => a.GetByIdAsync(AccountId)).ReturnsAsync(new Account { Id = AccountId, BrokerAccountId = BrokerAccountId });
        _tInvest.Setup(c => c.GetPrimaryAccountIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync("OTHER-ACCOUNT");

        var result = await CreateService().SyncAsync(AccountId);

        result.Errors.Should().BeEmpty();
        _tInvest.Verify(c => c.GetPortfolioAsync(BrokerAccountId, It.IsAny<CancellationToken>()), Times.Once);
        _tInvest.Verify(c => c.GetPrimaryAccountIdAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SyncAsync_NoPinnedBrokerAccount_PinsTheResolvedOne()
    {
        SetupHappyPathAccountAndPositions();
        _accounts.Setup(a => a.GetByIdAsync(AccountId)).ReturnsAsync(new Account { Id = AccountId, UserId = 1 });

        await CreateService().SyncAsync(AccountId);

        _accounts.Verify(a => a.UpdateAsync(It.Is<Account>(acc => acc.BrokerAccountId == BrokerAccountId)), Times.Once);
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

        _tInvest.Setup(c => c.GetPortfolioAsync(BrokerAccountId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TInvestPortfolio
            {
                Bonds = new List<TInvestPortfolioPosition>
                {
                    new() { Figi = Figi, InstrumentUid = "uid-1", Quantity = 10, AveragePositionPrice = 980m, CurrentNkd = 1m },
                    new() { Figi = secondFigi, InstrumentUid = "uid-2", Quantity = 5, AveragePositionPrice = 500m, CurrentNkd = 1m },
                },
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
    public async Task SyncAsync_NonBondOperations_AreNotStoredInBondJournal()
    {
        // plan/39 B3: покупка валюты/акции и дивидендный налог по акции не должны попасть в журнал
        // облигационного XIRR; комиссия без инструмента (InstrumentKind = null) — остаётся.
        SetupHappyPathAccountAndPositions();
        _tInvest.Setup(c => c.GetOperationsAsync(BrokerAccountId, It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TInvestOperation>
            {
                new() { Id = "bond-buy", OperationType = "Buy", Figi = Figi, InstrumentKind = "Bond", Date = new DateTime(2026, 1, 10), PaymentRub = -9800m, Quantity = 10 },
                new() { Id = "cny-buy", OperationType = "Buy", Figi = "CNY-FIGI", InstrumentKind = "Currency", Date = new DateTime(2026, 1, 11), PaymentRub = -50000m, Quantity = 5000 },
                new() { Id = "share-sell", OperationType = "Sell", Figi = "SBER-FIGI", InstrumentKind = "Share", Date = new DateTime(2026, 1, 12), PaymentRub = 7000m, Quantity = 20 },
                new() { Id = "share-tax", OperationType = "DividendTax", Figi = "SBER-FIGI", InstrumentKind = "Share", Date = new DateTime(2026, 1, 13), PaymentRub = -130m },
                new() { Id = "acct-fee", OperationType = "ServiceFee", Date = new DateTime(2026, 1, 14), PaymentRub = -290m },
            });
        _operations.Setup(o => o.UpsertManyByExternalIdAsync(It.IsAny<IEnumerable<Operation>>()))
            .ReturnsAsync((IEnumerable<Operation> ops) => ops.Count());

        var result = await CreateService().SyncAsync(AccountId);

        result.OperationsUpserted.Should().Be(2);
        _operations.Verify(o => o.UpsertManyByExternalIdAsync(It.Is<IEnumerable<Operation>>(ops =>
            ops.Select(x => x.ExternalId).OrderBy(x => x).SequenceEqual(new[] { "acct-fee", "bond-buy" }))), Times.Once);
    }

    [Fact]
    public async Task SyncAsync_HistoricalBondOperation_CreatesInstrument_ResolvedOncePerFigi()
    {
        // plan/39 M6: бумага куплена и погашена до первого синка — в справочнике её нет. Инструмент
        // создаётся, InstrumentId проставлен; два события по одному FIGI → один поход в T-Invest за ISIN.
        SetupHappyPathAccountAndPositions();
        const string histFigi = "HIST-FIGI";
        _tInvest.Setup(c => c.GetIsinByFigiAsync(histFigi, It.IsAny<CancellationToken>())).ReturnsAsync("RU000HIST001");
        _tInvest.Setup(c => c.GetOperationsAsync(BrokerAccountId, It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TInvestOperation>
            {
                new() { Id = "h-buy", OperationType = "Buy", Figi = histFigi, InstrumentKind = "Bond", Date = new DateTime(2025, 1, 10), PaymentRub = -980m, Quantity = 1 },
                new() { Id = "h-red", OperationType = "BondRepaymentFull", Figi = histFigi, InstrumentKind = "Bond", Date = new DateTime(2025, 9, 10), PaymentRub = 1000m, Quantity = 1 },
            });
        _operations.Setup(o => o.UpsertManyByExternalIdAsync(It.IsAny<IEnumerable<Operation>>()))
            .ReturnsAsync((IEnumerable<Operation> ops) => ops.Count());

        await CreateService().SyncAsync(AccountId);

        _tInvest.Verify(c => c.GetIsinByFigiAsync(histFigi, It.IsAny<CancellationToken>()), Times.Once);
        _instruments.Verify(r => r.UpsertAsync(It.Is<Instrument>(i => i.Figi == histFigi && i.Isin == "RU000HIST001")), Times.Once);
        _operations.Verify(o => o.UpsertManyByExternalIdAsync(It.Is<IEnumerable<Operation>>(ops =>
            ops.Where(x => x.ExternalId == "h-buy" || x.ExternalId == "h-red").All(x => x.InstrumentId == InstrumentId)
            && ops.Count(x => x.ExternalId == "h-buy" || x.ExternalId == "h-red") == 2)), Times.Once);
    }

    [Fact]
    public async Task SyncAsync_FallbackQuote_NotWrittenForMaturedInstrument()
    {
        // plan/39 M8: у погашенной бумаги может остаться «последняя сделка» — котировку на сегодня не пишем.
        SetupHappyPathAccountAndPositions(currentPrice: null);
        var matured = new Instrument
        {
            Id = InstrumentId, Isin = Isin, Figi = Figi, Secid = "SU26238RMFS4",
            FaceValue = 1000m, MaturityDate = BusinessClock.MoscowToday().AddDays(-3),
        };
        _instruments.Setup(r => r.GetByIdAsync(InstrumentId)).ReturnsAsync(matured);
        _instruments.Setup(r => r.GetByFigiAsync(Figi)).ReturnsAsync(matured);
        // Обогащение из MOEX перезаписывает MaturityDate — отдаём ту же (прошедшую) дату.
        _moex.Setup(m => m.GetSecurityInfoAsync("SU26238RMFS4", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MoexSecurityInfo
            {
                Secid = "SU26238RMFS4", BoardId = "TQOB", FaceValue = 1000m, MatDate = matured.MaturityDate,
            });
        _tInvest.Setup(c => c.GetQuotesAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, TInvestQuote>
            {
                [Figi] = new TInvestQuote { Figi = Figi, LastPrice = 100m },
            });

        await CreateService().SyncAsync(AccountId);

        _quotes.Verify(q => q.UpsertAsync(It.IsAny<MarketQuote>()), Times.Never);
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
        _tInvest.Setup(c => c.GetPortfolioAsync(BrokerAccountId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TInvestPortfolio
            {
                Bonds = new List<TInvestPortfolioPosition>
                {
                    new() { Figi = Figi, InstrumentUid = "uid-1", Quantity = 1, AveragePositionPrice = 100m, CurrentNkd = 0m },
                },
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
    public async Task SyncAsync_PortfolioHasNoCurrentPrice_FallbackConvertsLastPricePointsToRubles()
    {
        // Регрессия на баг единиц (задание): часть 3 (fallback) писала q.LastPrice напрямую как
        // CleanPrice, но T-Invest marketdata (GetQuotesAsync/LastPrice) отдаёт цену в ПУНКТАХ
        // (% от номинала), не в рублях. LastPrice=96.4, FaceValue=1000 → CleanPrice должно быть 964.
        SetupHappyPathAccountAndPositions(currentPrice: null);
        _tInvest.Setup(c => c.GetQuotesAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, TInvestQuote>
            {
                [Figi] = new TInvestQuote { Figi = Figi, LastPrice = 96.4m },
            });

        var service = CreateService();
        await service.SyncAsync(AccountId);

        _quotes.Verify(q => q.UpsertAsync(It.Is<MarketQuote>(mq =>
            mq.InstrumentId == InstrumentId && mq.CleanPrice == 964m)), Times.Once);
    }

    [Fact]
    public async Task SyncAsync_PortfolioHasNoCurrentPrice_OutOfScopeCurrencyInstrument_SkipsFallbackQuote()
    {
        // Валютный номинал (IsOutOfScopeCurrency) — конвертация пунктов в рубли без курса
        // невозможна, fallback не должен писать неверную котировку (задание п.2).
        SetupHappyPathAccountAndPositions(currentPrice: null);
        _instruments.Setup(r => r.GetByIdAsync(InstrumentId)).ReturnsAsync(new Instrument
        {
            Id = InstrumentId,
            Isin = Isin,
            Figi = Figi,
            Secid = "SU26238RMFS4",
            FaceValue = 1000m,
            MaturityDate = new DateOnly(2041, 5, 15),
            IsOutOfScopeCurrency = true,
        });
        _tInvest.Setup(c => c.GetQuotesAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, TInvestQuote>
            {
                [Figi] = new TInvestQuote { Figi = Figi, LastPrice = 96.4m },
            });

        var service = CreateService();
        await service.SyncAsync(AccountId);

        _quotes.Verify(q => q.UpsertAsync(It.Is<MarketQuote>(mq => mq.InstrumentId == InstrumentId)), Times.Never);
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

    // ─── Задача 20: ResolveOrCreateInstrumentByIsinAsync (watchlist — бумага без позиции/FIGI) ───

    [Fact]
    public async Task ResolveOrCreateInstrumentByIsinAsync_NewIsin_CreatesPlaceholderAndEnriches()
    {
        const string secid = "SU26238RMFS4";
        _instruments.Setup(r => r.GetByIsinAsync(Isin)).ReturnsAsync((Instrument?)null);
        _instruments.Setup(r => r.UpsertAsync(It.IsAny<Instrument>())).ReturnsAsync(InstrumentId);
        _instruments.Setup(r => r.GetByIdAsync(InstrumentId)).ReturnsAsync(new Instrument
        {
            Id = InstrumentId,
            Isin = Isin,
            Secid = null,
            FaceValue = 0m,
            DataIncomplete = true,
        });
        _moex.Setup(m => m.ResolveSecidByIsinAsync(Isin, It.IsAny<CancellationToken>())).ReturnsAsync(secid);
        _moex.Setup(m => m.GetSecurityInfoAsync(secid, It.IsAny<CancellationToken>())).ReturnsAsync(new MoexSecurityInfo
        {
            Secid = secid,
            BoardId = "TQOB",
            FaceValue = 1000m,
            MatDate = new DateOnly(2041, 5, 15),
        });
        _moex.Setup(m => m.GetBondizationAsync(secid, It.IsAny<CancellationToken>())).ReturnsAsync(new MoexBondizationResult
        {
            Secid = secid,
            Coupons = [new CouponSchedule { CouponDate = new DateOnly(2026, 12, 8), ValueRub = 35.4m, IsKnown = true }],
        });

        var service = CreateService();
        var instrumentId = await service.ResolveOrCreateInstrumentByIsinAsync(Isin);

        instrumentId.Should().Be(InstrumentId);
        _instruments.Verify(r => r.UpsertAsync(It.Is<Instrument>(i => i.Isin == Isin && i.Figi == null)), Times.AtLeastOnce);
        _coupons.Verify(c => c.ReplaceForInstrumentAsync(InstrumentId, It.IsAny<IReadOnlyList<CouponSchedule>>()), Times.Once);
    }

    [Fact]
    public async Task ResolveOrCreateInstrumentByIsinAsync_ExistingInstrument_ReEnrichesWithoutDuplicateCreate()
    {
        _instruments.Setup(r => r.GetByIsinAsync(Isin)).ReturnsAsync(new Instrument
        {
            Id = InstrumentId,
            Isin = Isin,
            Secid = "SU26238RMFS4",
            FaceValue = 1000m,
        });
        _instruments.Setup(r => r.GetByIdAsync(InstrumentId)).ReturnsAsync(new Instrument
        {
            Id = InstrumentId,
            Isin = Isin,
            Secid = "SU26238RMFS4",
            FaceValue = 1000m,
        });
        _instruments.Setup(r => r.UpsertAsync(It.IsAny<Instrument>())).ReturnsAsync(InstrumentId);
        _moex.Setup(m => m.GetSecurityInfoAsync("SU26238RMFS4", It.IsAny<CancellationToken>())).ReturnsAsync((MoexSecurityInfo?)null);
        _moex.Setup(m => m.GetBondizationAsync("SU26238RMFS4", It.IsAny<CancellationToken>())).ReturnsAsync(new MoexBondizationResult { Secid = "SU26238RMFS4" });

        var service = CreateService();
        var instrumentId = await service.ResolveOrCreateInstrumentByIsinAsync(Isin);

        instrumentId.Should().Be(InstrumentId);
        _moex.Verify(m => m.ResolveSecidByIsinAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never,
            "SECID уже закэширован в справочнике — повторный поиск по ISIN не нужен");
    }

    [Fact]
    public async Task ResolveOrCreateInstrumentByIsinAsync_MoexDoesNotFindIsin_MarksDataIncomplete_DoesNotThrow()
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
        _moex.Setup(m => m.ResolveSecidByIsinAsync(unknownIsin, It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);

        var service = CreateService();
        var instrumentId = await service.ResolveOrCreateInstrumentByIsinAsync(unknownIsin);

        instrumentId.Should().Be(InstrumentId, "инструмент заводится как заготовка, даже если MOEX не нашёл SECID");
        _instruments.Verify(r => r.UpsertAsync(It.Is<Instrument>(i => i.DataIncomplete == true)), Times.AtLeastOnce);
    }
}

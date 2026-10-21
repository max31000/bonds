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

/// <summary>Задача 51: FundSyncService на моках — создание фонда, позиции, операции, сверка (в т.ч. защита от пустого ответа).</summary>
public class FundSyncServiceTests
{
    private readonly Mock<ITInvestPortfolioClient> _tInvest = new();
    private readonly Mock<IMoexIssClient> _moex = new();
    private readonly Mock<IFundRepository> _funds = new();
    private readonly Mock<IFundPositionRepository> _positions = new();
    private readonly Mock<IFundOperationRepository> _operations = new();

    private const string Figi = "TCS00A108WX3";
    private const string Isin = "RU000A108WX3";

    private static ulong NextAccount = 9_000_000; // уникальный счёт на тест: static-словарь «пустого ответа» общий
    private readonly ulong _accountId = Interlocked.Increment(ref NextAccount);

    private FundSyncService CreateService() => new(
        _tInvest.Object, _moex.Object, _funds.Object, _positions.Object, _operations.Object,
        NullLogger<FundSyncService>.Instance);

    private static TInvestPortfolioPosition Position(string figi = Figi, decimal qty = 100m) => new()
    {
        Figi = figi, InstrumentUid = "u", Quantity = qty, AveragePositionPrice = 101.5m, CurrentPrice = 102.25m,
    };

    private void SetupNewFund(ulong id = 5)
    {
        _funds.Setup(r => r.GetByFigiAsync(Figi)).ReturnsAsync((Fund?)null);
        _funds.Setup(r => r.UpsertAsync(It.IsAny<Fund>())).ReturnsAsync(id);
        _tInvest.Setup(c => c.GetFundInfoAsync(Figi, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TInvestFundInfo { Isin = Isin, Ticker = "TPAY", Name = "Пассивный доход", FocusType = "fixed_income", TotalExpenseFraction = 0.009m });
        _moex.Setup(m => m.ResolveSecidByIsinAsync(Isin, It.IsAny<CancellationToken>())).ReturnsAsync("TPAY");
        _operations.Setup(o => o.UpsertManyByExternalIdAsync(It.IsAny<IEnumerable<FundOperation>>()))
            .ReturnsAsync((IEnumerable<FundOperation> ops) => ops.Count());
        _positions.Setup(p => p.GetByAccountAsync(_accountId)).ReturnsAsync(new List<FundPosition>());
    }

    [Fact]
    public async Task Apply_NewFund_CreatesFundPositionAndOperations_PreservesBrokerSigns()
    {
        SetupNewFund();
        var ops = new List<TInvestOperation>
        {
            new() { Id = "b", OperationType = "Buy", Figi = Figi, InstrumentKind = "Etf", Date = new DateTime(2026, 9, 1), PaymentRub = -10150m, Quantity = 100 },
            new() { Id = "d", OperationType = "Dividend", Figi = Figi, InstrumentKind = "Etf", Date = new DateTime(2026, 10, 1), PaymentRub = 90m },
            new() { Id = "t", OperationType = "DividendTax", Figi = Figi, InstrumentKind = "Etf", Date = new DateTime(2026, 10, 1), PaymentRub = -12m },
            new() { Id = "x", OperationType = "Coupon", Figi = Figi, InstrumentKind = "Etf", Date = new DateTime(2026, 10, 1), PaymentRub = 1m },
        };

        var result = await CreateService().ApplyAsync(_accountId, [Position()], ops);

        result.Errors.Should().BeEmpty();
        result.FundsSynced.Should().Be(1);
        result.OperationsUpserted.Should().Be(3, "неизвестный тип Coupon пропускается");
        _funds.Verify(r => r.UpsertAsync(It.Is<Fund>(f =>
            f.Figi == Figi && f.Isin == Isin && f.Secid == "TPAY" && f.Ticker == "TPAY"
            && f.TotalExpenseFraction == 0.009m && f.FocusType == "fixed_income")), Times.Once);
        _positions.Verify(p => p.UpsertAsync(It.Is<FundPosition>(x =>
            x.AccountId == _accountId && x.FundId == 5 && x.Quantity == 100m
            && x.AvgPurchasePriceRub == 101.5m && x.CurrentPriceRub == 102.25m)), Times.Once);
        _operations.Verify(o => o.UpsertManyByExternalIdAsync(It.Is<IEnumerable<FundOperation>>(l =>
            l.Count() == 3
            && l.Single(x => x.ExternalId == "b").AmountRub == -10150m && l.Single(x => x.ExternalId == "b").Type == FundOperationType.Buy
            && l.Single(x => x.ExternalId == "d").AmountRub == 90m && l.Single(x => x.ExternalId == "d").Type == FundOperationType.Distribution
            && l.Single(x => x.ExternalId == "t").Type == FundOperationType.Tax
            && l.All(x => x.FundId == 5))), Times.Once);
        _tInvest.Verify(c => c.GetFundInfoAsync(Figi, It.IsAny<CancellationToken>()), Times.Once, "FIGI резолвится один раз за синк");
    }

    [Fact]
    public async Task Apply_SecidNotFoundOnMoex_FallsBackToTicker()
    {
        SetupNewFund();
        _moex.Setup(m => m.ResolveSecidByIsinAsync(Isin, It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);

        await CreateService().ApplyAsync(_accountId, [Position()], []);

        _funds.Verify(r => r.UpsertAsync(It.Is<Fund>(f => f.Secid == "TPAY")), Times.Once);
    }

    [Fact]
    public async Task Apply_FreshExistingFund_NotRefreshedFromTInvest()
    {
        _funds.Setup(r => r.GetByFigiAsync(Figi)).ReturnsAsync(new Fund { Id = 5, Figi = Figi, Isin = Isin, UpdatedAt = DateTime.UtcNow.AddHours(-2) });
        _positions.Setup(p => p.GetByAccountAsync(_accountId)).ReturnsAsync(new List<FundPosition>());

        await CreateService().ApplyAsync(_accountId, [Position()], []);

        _tInvest.Verify(c => c.GetFundInfoAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never, "справочник обновляется раз в сутки");
        _positions.Verify(p => p.UpsertAsync(It.Is<FundPosition>(x => x.FundId == 5)), Times.Once);
    }

    [Fact]
    public async Task Apply_StaleExistingFund_RefreshedKeepingSettingsAndOldTerWhenMissing()
    {
        var existing = new Fund { Id = 5, Figi = Figi, Isin = Isin, TotalExpenseFraction = 0.01m, ForecastMethod = FundForecastMethod.IndexYield, UpdatedAt = DateTime.UtcNow.AddDays(-2) };
        _funds.Setup(r => r.GetByFigiAsync(Figi)).ReturnsAsync(existing);
        _funds.Setup(r => r.UpsertAsync(It.IsAny<Fund>())).ReturnsAsync(5UL);
        _tInvest.Setup(c => c.GetFundInfoAsync(Figi, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TInvestFundInfo { Isin = Isin, Ticker = "TPAY", TotalExpenseFraction = null });
        _positions.Setup(p => p.GetByAccountAsync(_accountId)).ReturnsAsync(new List<FundPosition>());

        await CreateService().ApplyAsync(_accountId, [Position()], []);

        _funds.Verify(r => r.UpsertAsync(It.Is<Fund>(f => f.TotalExpenseFraction == 0.01m && f.ForecastMethod == FundForecastMethod.IndexYield)), Times.Once);
    }

    [Fact]
    public async Task Apply_UnresolvedFundOperation_SavedWithNullFundId()
    {
        _funds.Setup(r => r.GetByFigiAsync("UNKNOWN")).ReturnsAsync((Fund?)null);
        _tInvest.Setup(c => c.GetFundInfoAsync("UNKNOWN", It.IsAny<CancellationToken>())).ReturnsAsync((TInvestFundInfo?)null);
        _operations.Setup(o => o.UpsertManyByExternalIdAsync(It.IsAny<IEnumerable<FundOperation>>()))
            .ReturnsAsync((IEnumerable<FundOperation> ops) => ops.Count());
        _positions.Setup(p => p.GetByAccountAsync(_accountId)).ReturnsAsync(new List<FundPosition>());

        var result = await CreateService().ApplyAsync(_accountId, [],
            [new TInvestOperation { Id = "o1", OperationType = "Sell", Figi = "UNKNOWN", InstrumentKind = "Etf", Date = new DateTime(2026, 9, 1), PaymentRub = 500m, Quantity = 5 }]);

        result.OperationsUpserted.Should().Be(1);
        _operations.Verify(o => o.UpsertManyByExternalIdAsync(It.Is<IEnumerable<FundOperation>>(l => l.Single().FundId == null && l.Single().AmountRub == 500m)), Times.Once);
    }

    [Fact]
    public async Task Apply_Reconciliation_DeletesFundsBrokerNoLongerHolds()
    {
        SetupNewFund();
        _positions.Setup(p => p.DeleteAllExceptAsync(_accountId, It.IsAny<IReadOnlyCollection<ulong>>())).ReturnsAsync(1);

        var result = await CreateService().ApplyAsync(_accountId, [Position()], []);

        result.PositionsClosed.Should().Be(1);
        _positions.Verify(p => p.DeleteAllExceptAsync(_accountId, It.Is<IReadOnlyCollection<ulong>>(k => k.Count == 1 && k.Contains(5UL))), Times.Once);
    }

    [Fact]
    public async Task Apply_UnresolvedPosition_SkipsReconciliation()
    {
        _funds.Setup(r => r.GetByFigiAsync("UNKNOWN")).ReturnsAsync((Fund?)null);
        _tInvest.Setup(c => c.GetFundInfoAsync("UNKNOWN", It.IsAny<CancellationToken>())).ReturnsAsync((TInvestFundInfo?)null);

        var result = await CreateService().ApplyAsync(_accountId, [Position("UNKNOWN")], []);

        result.Errors.Should().ContainSingle();
        _positions.Verify(p => p.DeleteAllExceptAsync(It.IsAny<ulong>(), It.IsAny<IReadOnlyCollection<ulong>>()), Times.Never);
    }

    [Fact]
    public async Task Apply_BrokerReturnsNoFunds_DeletesOnlyOnSecondConsecutiveEmptySync()
    {
        _positions.Setup(p => p.GetByAccountAsync(_accountId)).ReturnsAsync(new List<FundPosition> { new() { Id = 1, AccountId = _accountId, FundId = 5 } });
        _positions.Setup(p => p.DeleteAllExceptAsync(_accountId, It.IsAny<IReadOnlyCollection<ulong>>())).ReturnsAsync(1);
        var service = CreateService();

        await service.ApplyAsync(_accountId, [], []);
        _positions.Verify(p => p.DeleteAllExceptAsync(It.IsAny<ulong>(), It.IsAny<IReadOnlyCollection<ulong>>()), Times.Never, "первый пустой ответ — подозрительный");

        await service.ApplyAsync(_accountId, [], []);
        _positions.Verify(p => p.DeleteAllExceptAsync(_accountId, It.Is<IReadOnlyCollection<ulong>>(k => k.Count == 0)), Times.Once);
    }

    [Fact]
    public async Task Apply_BrokerReturnsNoFunds_NoLocalPositions_NothingDeferred()
    {
        _positions.Setup(p => p.GetByAccountAsync(_accountId)).ReturnsAsync(new List<FundPosition>());
        _positions.Setup(p => p.DeleteAllExceptAsync(_accountId, It.IsAny<IReadOnlyCollection<ulong>>())).ReturnsAsync(0);

        await CreateService().ApplyAsync(_accountId, [], []);

        _positions.Verify(p => p.DeleteAllExceptAsync(_accountId, It.IsAny<IReadOnlyCollection<ulong>>()), Times.Once);
    }
}

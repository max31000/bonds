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

/// <summary>Задача 55: CurrencySyncService на моках - создание валюты, позиции (единицы/рубли за единицу), операции, сверка (в т.ч. защита от пустого ответа).</summary>
public class CurrencySyncServiceTests
{
    private readonly Mock<ITInvestPortfolioClient> _tInvest = new();
    private readonly Mock<IMoexIssClient> _moex = new();
    private readonly Mock<ICurrencyRepository> _currencies = new();
    private readonly Mock<ICurrencyPositionRepository> _positions = new();
    private readonly Mock<ICurrencyOperationRepository> _operations = new();

    private const string Figi = "BBG0013HRTL0";

    private static ulong NextAccount = 8_000_000; // уникальный счёт на тест: static-словарь «пустого ответа» общий
    private readonly ulong _accountId = Interlocked.Increment(ref NextAccount);

    private CurrencySyncService CreateService() => new(
        _tInvest.Object, _moex.Object, _currencies.Object, _positions.Object, _operations.Object,
        NullLogger<CurrencySyncService>.Instance);

    private static TInvestPortfolioPosition Position(string figi = Figi, decimal qty = 1000m, decimal avg = 13m, decimal? price = 12.2m) => new()
    {
        Figi = figi, InstrumentUid = "u", Quantity = qty, AveragePositionPrice = avg, CurrentPrice = price,
    };

    private void SetupNewCurrency(ulong id = 3, decimal moexFace = 1m)
    {
        _currencies.Setup(r => r.GetByFigiAsync(Figi)).ReturnsAsync((Currency?)null);
        _currencies.Setup(r => r.UpsertAsync(It.IsAny<Currency>())).ReturnsAsync(id);
        _tInvest.Setup(c => c.GetCurrencyInfoAsync(Figi, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TInvestCurrencyInfo { IsoCode = "CNY", Ticker = "CNYRUB_TOM", Name = "Китайский юань", Lot = 1000m });
        _moex.Setup(m => m.GetCurrencyFaceValueAsync("CNYRUB_TOM", It.IsAny<CancellationToken>())).ReturnsAsync(moexFace);
        _operations.Setup(o => o.UpsertManyByExternalIdAsync(It.IsAny<IEnumerable<CurrencyOperation>>()))
            .ReturnsAsync((IEnumerable<CurrencyOperation> ops) => ops.Count());
        _positions.Setup(p => p.GetByAccountAsync(_accountId)).ReturnsAsync(new List<CurrencyPosition>());
    }

    [Fact]
    public async Task Apply_NewCurrency_CreatesCurrencyPositionAndOperations_PreservesBrokerSigns()
    {
        SetupNewCurrency();
        var ops = new List<TInvestOperation>
        {
            new() { Id = "b", OperationType = "Buy", Figi = Figi, InstrumentKind = "Currency", Date = new DateTime(2026, 6, 1), PaymentRub = -13000m, PaymentCurrency = "rub", Quantity = 1000 },
            new() { Id = "s", OperationType = "Sell", Figi = Figi, InstrumentKind = "Currency", Date = new DateTime(2026, 8, 1), PaymentRub = 5000m, Quantity = 400 },
            new() { Id = "f", OperationType = "BrokerFee", Figi = Figi, InstrumentKind = "Currency", Date = new DateTime(2026, 8, 1), PaymentRub = -5m, Quantity = 400 },
            new() { Id = "x", OperationType = "Coupon", Figi = Figi, InstrumentKind = "Currency", Date = new DateTime(2026, 8, 1), PaymentRub = 1m },
        };

        var result = await CreateService().ApplyAsync(_accountId, [Position()], ops);

        result.Errors.Should().BeEmpty();
        result.CurrenciesSynced.Should().Be(1);
        result.OperationsUpserted.Should().Be(3, "неизвестный тип Coupon пропускается");
        _currencies.Verify(r => r.UpsertAsync(It.Is<Currency>(c =>
            c.Figi == Figi && c.IsoCode == "CNY" && c.Ticker == "CNYRUB_TOM" && c.MoexSecid == "CNYRUB_TOM" && c.FaceValue == 1m && c.Lot == 1000m)), Times.Once);
        _positions.Verify(p => p.UpsertAsync(It.Is<CurrencyPosition>(x =>
            x.AccountId == _accountId && x.CurrencyId == 3 && x.Quantity == 1000m
            && x.AvgPurchasePriceRub == 13m && x.CurrentPriceRub == 12.2m && x.PriceAsOf != null)), Times.Once);
        _operations.Verify(o => o.UpsertManyByExternalIdAsync(It.Is<IEnumerable<CurrencyOperation>>(l =>
            l.Count() == 3
            && l.Single(x => x.ExternalId == "b").AmountRub == -13000m && l.Single(x => x.ExternalId == "b").Type == CurrencyOperationType.Buy
            && l.Single(x => x.ExternalId == "b").Quantity == 1000m
            && l.Single(x => x.ExternalId == "s").AmountRub == 5000m && l.Single(x => x.ExternalId == "s").Type == CurrencyOperationType.Sell
            && l.Single(x => x.ExternalId == "f").Type == CurrencyOperationType.Fee && l.Single(x => x.ExternalId == "f").Quantity == null
            && l.All(x => x.CurrencyId == 3))), Times.Once);
        _tInvest.Verify(c => c.GetCurrencyInfoAsync(Figi, It.IsAny<CancellationToken>()), Times.Once, "FIGI резолвится один раз за синк");
    }

    [Fact]
    public async Task Apply_JpyLikeCurrency_StoresMoexFaceValue()
    {
        _currencies.Setup(r => r.GetByFigiAsync("JPY-F")).ReturnsAsync((Currency?)null);
        _currencies.Setup(r => r.UpsertAsync(It.IsAny<Currency>())).ReturnsAsync(4UL);
        _tInvest.Setup(c => c.GetCurrencyInfoAsync("JPY-F", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TInvestCurrencyInfo { IsoCode = "JPY", Ticker = "JPYRUB_TOM" });
        _moex.Setup(m => m.GetCurrencyFaceValueAsync("JPYRUB_TOM", It.IsAny<CancellationToken>())).ReturnsAsync(100m);
        _positions.Setup(p => p.GetByAccountAsync(_accountId)).ReturnsAsync(new List<CurrencyPosition>());

        await CreateService().ApplyAsync(_accountId, [Position("JPY-F", 50_000m, 0.08m, 0.082m)], []);

        _currencies.Verify(r => r.UpsertAsync(It.Is<Currency>(c => c.IsoCode == "JPY" && c.FaceValue == 100m && c.MoexSecid == "JPYRUB_TOM")), Times.Once);
    }

    [Fact]
    public async Task Apply_MoexFaceValueUnavailable_KeepsPreviousAndDefaultsToOne()
    {
        SetupNewCurrency();
        _moex.Setup(m => m.GetCurrencyFaceValueAsync("CNYRUB_TOM", It.IsAny<CancellationToken>())).ReturnsAsync((decimal?)null);

        var result = await CreateService().ApplyAsync(_accountId, [Position()], []);

        result.Errors.Should().BeEmpty("номинал - необязательный атрибут справочника");
        _currencies.Verify(r => r.UpsertAsync(It.Is<Currency>(c => c.FaceValue == 1m)), Times.Once);
    }

    [Fact]
    public async Task Apply_BrokerDoesNotKnowAveragePrice_StoresNull_NotZero()
    {
        SetupNewCurrency();

        await CreateService().ApplyAsync(_accountId, [Position(avg: 0m, price: 0m)], []);

        _positions.Verify(p => p.UpsertAsync(It.Is<CurrencyPosition>(x => x.AvgPurchasePriceRub == null && x.CurrentPriceRub == null && x.PriceAsOf == null)), Times.Once);
    }

    [Fact]
    public async Task Apply_QuantityMissingOnOperation_DerivedFromPaymentAndUnitPrice()
    {
        SetupNewCurrency();

        await CreateService().ApplyAsync(_accountId, [],
            [new TInvestOperation { Id = "b", OperationType = "Buy", Figi = Figi, InstrumentKind = "Currency", Date = new DateTime(2026, 6, 1), PaymentRub = -13000m, UnitPrice = 13m }]);

        _operations.Verify(o => o.UpsertManyByExternalIdAsync(It.Is<IEnumerable<CurrencyOperation>>(l => l.Single().Quantity == 1000m)), Times.Once);
    }

    [Fact]
    public async Task Apply_FreshExistingCurrency_NotRefreshedFromTInvest()
    {
        _currencies.Setup(r => r.GetByFigiAsync(Figi)).ReturnsAsync(new Currency { Id = 3, Figi = Figi, IsoCode = "CNY", UpdatedAt = DateTime.UtcNow.AddHours(-2) });
        _positions.Setup(p => p.GetByAccountAsync(_accountId)).ReturnsAsync(new List<CurrencyPosition>());

        await CreateService().ApplyAsync(_accountId, [Position()], []);

        _tInvest.Verify(c => c.GetCurrencyInfoAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never, "справочник обновляется раз в сутки");
        _positions.Verify(p => p.UpsertAsync(It.Is<CurrencyPosition>(x => x.CurrencyId == 3)), Times.Once);
    }

    [Fact]
    public async Task Apply_UnresolvedCurrencyOperation_SavedWithNullCurrencyId()
    {
        _currencies.Setup(r => r.GetByFigiAsync("UNKNOWN")).ReturnsAsync((Currency?)null);
        _tInvest.Setup(c => c.GetCurrencyInfoAsync("UNKNOWN", It.IsAny<CancellationToken>())).ReturnsAsync((TInvestCurrencyInfo?)null);
        _operations.Setup(o => o.UpsertManyByExternalIdAsync(It.IsAny<IEnumerable<CurrencyOperation>>()))
            .ReturnsAsync((IEnumerable<CurrencyOperation> ops) => ops.Count());
        _positions.Setup(p => p.GetByAccountAsync(_accountId)).ReturnsAsync(new List<CurrencyPosition>());

        var result = await CreateService().ApplyAsync(_accountId, [],
            [new TInvestOperation { Id = "o1", OperationType = "Sell", Figi = "UNKNOWN", InstrumentKind = "Currency", Date = new DateTime(2026, 9, 1), PaymentRub = 500m, Quantity = 40 }]);

        result.OperationsUpserted.Should().Be(1);
        _operations.Verify(o => o.UpsertManyByExternalIdAsync(It.Is<IEnumerable<CurrencyOperation>>(l => l.Single().CurrencyId == null && l.Single().AmountRub == 500m)), Times.Once);
    }

    [Fact]
    public async Task Apply_Reconciliation_DeletesCurrenciesBrokerNoLongerHolds()
    {
        SetupNewCurrency();
        _positions.Setup(p => p.DeleteAllExceptAsync(_accountId, It.IsAny<IReadOnlyCollection<ulong>>())).ReturnsAsync(1);

        var result = await CreateService().ApplyAsync(_accountId, [Position()], []);

        result.PositionsClosed.Should().Be(1);
        _positions.Verify(p => p.DeleteAllExceptAsync(_accountId, It.Is<IReadOnlyCollection<ulong>>(k => k.Count == 1 && k.Contains(3UL))), Times.Once);
    }

    [Fact]
    public async Task Apply_UnresolvedPosition_SkipsReconciliation()
    {
        _currencies.Setup(r => r.GetByFigiAsync("UNKNOWN")).ReturnsAsync((Currency?)null);
        _tInvest.Setup(c => c.GetCurrencyInfoAsync("UNKNOWN", It.IsAny<CancellationToken>())).ReturnsAsync((TInvestCurrencyInfo?)null);

        var result = await CreateService().ApplyAsync(_accountId, [Position("UNKNOWN")], []);

        result.Errors.Should().ContainSingle();
        _positions.Verify(p => p.DeleteAllExceptAsync(It.IsAny<ulong>(), It.IsAny<IReadOnlyCollection<ulong>>()), Times.Never);
    }

    [Fact]
    public async Task Apply_BrokerReturnsNoCurrencies_DeletesOnlyOnSecondConsecutiveEmptySync()
    {
        _positions.Setup(p => p.GetByAccountAsync(_accountId)).ReturnsAsync(new List<CurrencyPosition> { new() { Id = 1, AccountId = _accountId, CurrencyId = 3 } });
        _positions.Setup(p => p.DeleteAllExceptAsync(_accountId, It.IsAny<IReadOnlyCollection<ulong>>())).ReturnsAsync(1);
        var service = CreateService();

        await service.ApplyAsync(_accountId, [], []);
        _positions.Verify(p => p.DeleteAllExceptAsync(It.IsAny<ulong>(), It.IsAny<IReadOnlyCollection<ulong>>()), Times.Never, "первый пустой ответ - подозрительный");

        await service.ApplyAsync(_accountId, [], []);
        _positions.Verify(p => p.DeleteAllExceptAsync(_accountId, It.Is<IReadOnlyCollection<ulong>>(k => k.Count == 0)), Times.Once);
    }

    [Fact]
    public async Task Apply_NonEmptyResponseBetweenEmptyOnes_ResetsTheSuspicion()
    {
        SetupNewCurrency();
        _positions.Setup(p => p.GetByAccountAsync(_accountId)).ReturnsAsync(new List<CurrencyPosition> { new() { Id = 1, AccountId = _accountId, CurrencyId = 3 } });
        _positions.Setup(p => p.DeleteAllExceptAsync(_accountId, It.IsAny<IReadOnlyCollection<ulong>>())).ReturnsAsync(0);
        var service = CreateService();

        await service.ApplyAsync(_accountId, [], []);                 // подозрение
        await service.ApplyAsync(_accountId, [Position()], []);       // нормальный ответ - сброс
        _positions.Invocations.Clear();
        await service.ApplyAsync(_accountId, [], []);                 // снова «первый» пустой

        _positions.Verify(p => p.DeleteAllExceptAsync(It.IsAny<ulong>(), It.Is<IReadOnlyCollection<ulong>>(k => k.Count == 0)), Times.Never);
    }

    [Fact]
    public async Task Apply_BrokerReturnsNoCurrencies_NoLocalPositions_NothingDeferred()
    {
        _positions.Setup(p => p.GetByAccountAsync(_accountId)).ReturnsAsync(new List<CurrencyPosition>());
        _positions.Setup(p => p.DeleteAllExceptAsync(_accountId, It.IsAny<IReadOnlyCollection<ulong>>())).ReturnsAsync(0);

        await CreateService().ApplyAsync(_accountId, [], []);

        _positions.Verify(p => p.DeleteAllExceptAsync(_accountId, It.IsAny<IReadOnlyCollection<ulong>>()), Times.Once);
    }
}

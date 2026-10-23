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
/// Задача 61: Input/Output журнала T-Invest → <c>account_cash_movements</c> (раньше выбрасывались). Проверяются маппинг типов, знак/валюта/рубли,
/// отсутствие попадания в облигационный журнал и повторный синк (те же external_id → тот же набор ключей, дубли гасит UNIQUE в БД —
/// это покрыто интеграционным тестом репозитория).
/// </summary>
public class BondSyncServiceCashMovementTests
{
    private const ulong AccountId = 42;
    private const string BrokerAccountId = "BA-1";

    private readonly Mock<ITInvestPortfolioClient> _tInvest = new();
    private readonly Mock<IAccountRepository> _accounts = new();
    private readonly Mock<IOperationRepository> _operations = new();
    private readonly Mock<IAccountCashMovementRepository> _movements = new();
    private readonly Mock<ICurrencyRateLookup> _rates = new();
    private readonly List<AccountCashMovement> _saved = [];

    public BondSyncServiceCashMovementTests()
    {
        _accounts.Setup(a => a.GetByIdAsync(AccountId)).ReturnsAsync(new Account { Id = AccountId, BrokerAccountId = BrokerAccountId });
        _tInvest.Setup(c => c.GetPortfolioAsync(BrokerAccountId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TInvestPortfolio { Bonds = [] });
        _tInvest.Setup(c => c.GetQuotesAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, TInvestQuote>());
        _operations.Setup(o => o.UpsertManyByExternalIdAsync(It.IsAny<IEnumerable<Operation>>()))
            .ReturnsAsync((IEnumerable<Operation> o) => o.Count());
        _movements.Setup(m => m.UpsertManyAsync(It.IsAny<IReadOnlyCollection<AccountCashMovement>>()))
            .Callback((IReadOnlyCollection<AccountCashMovement> m) => { _saved.Clear(); _saved.AddRange(m); })
            .ReturnsAsync((IReadOnlyCollection<AccountCashMovement> m) => m.Count);
    }

    private BondSyncService CreateService() => new(
        _tInvest.Object, new Mock<IMoexIssClient>().Object, new Mock<IInstrumentRepository>().Object,
        new Mock<ICouponScheduleRepository>().Object, new Mock<IAmortizationScheduleRepository>().Object, new Mock<IOfferScheduleRepository>().Object,
        new Mock<IMarketQuoteRepository>().Object, new Mock<IYieldCurveRepository>().Object, new Mock<IPositionRepository>().Object,
        _operations.Object, _accounts.Object, NullLogger<BondSyncService>.Instance,
        fundSync: null, currencySync: null, rateLookup: _rates.Object, cashMovements: _movements.Object);

    private void SetupOperations(params TInvestOperation[] ops) =>
        _tInvest.Setup(c => c.GetOperationsAsync(BrokerAccountId, It.IsAny<DateTime?>(), It.IsAny<CancellationToken>())).ReturnsAsync(ops.ToList());

    private static DateTime DaysAgo(int days) => DateTime.UtcNow.Date.AddDays(-days);

    [Fact]
    public async Task SyncAsync_InputOutput_StoredAsCashMovements_WithBrokerSignAndRubles()
    {
        SetupOperations(
            new TInvestOperation { Id = "in-1", OperationType = "Input", Date = DaysAgo(10), PaymentRub = 50_000m, PaymentCurrency = "rub" },
            new TInvestOperation { Id = "out-1", OperationType = "Output", Date = DaysAgo(3), PaymentRub = -12_000.5m, PaymentCurrency = "rub" },
            new TInvestOperation { Id = "in-card", OperationType = "InputAcquiring", Date = DaysAgo(2), PaymentRub = 1_000m, PaymentCurrency = null });

        var result = await CreateService().SyncAsync(AccountId);

        result.HasErrors.Should().BeFalse();
        _saved.Should().HaveCount(3);
        var deposit = _saved.Single(m => m.ExternalId == "in-1");
        deposit.Type.Should().Be(CashMovementType.Deposit);
        deposit.Amount.Should().Be(50_000m);
        deposit.AmountRub.Should().Be(50_000m);
        deposit.Currency.Should().Be("RUB");
        deposit.AccountId.Should().Be(AccountId);

        var withdrawal = _saved.Single(m => m.ExternalId == "out-1");
        withdrawal.Type.Should().Be(CashMovementType.Withdrawal);
        withdrawal.Amount.Should().Be(-12_000.5m, "знак брокера сохраняется");
        withdrawal.AmountRub.Should().Be(-12_000.5m);

        _saved.Single(m => m.ExternalId == "in-card").Currency.Should().Be("RUB", "валюта не указана - рубль");
    }

    [Fact]
    public async Task SyncAsync_InputInForeignCurrency_AmountInOwnCurrency_RublesByDayRate()
    {
        var date = DaysAgo(5);
        _rates.Setup(r => r.GetRubPerUnitAsync("usd", It.IsAny<DateOnly>(), It.IsAny<CancellationToken>())).ReturnsAsync((decimal?)null);
        _rates.Setup(r => r.GetRubPerUnitAsync("USD", DateOnly.FromDateTime(date), It.IsAny<CancellationToken>())).ReturnsAsync(80m);
        _rates.Setup(r => r.GetRubPerUnitAsync("EUR", It.IsAny<DateOnly>(), It.IsAny<CancellationToken>())).ReturnsAsync((decimal?)null);
        SetupOperations(
            new TInvestOperation { Id = "usd-in", OperationType = "InputSwift", Date = date, PaymentRub = 100m, PaymentCurrency = "usd" },
            new TInvestOperation { Id = "eur-out", OperationType = "OutputSwift", Date = date, PaymentRub = -50m, PaymentCurrency = "eur" });

        var result = await CreateService().SyncAsync(AccountId);

        result.HasErrors.Should().BeFalse("нет курса - не ошибка цикла");
        var usd = _saved.Single(m => m.ExternalId == "usd-in");
        usd.Currency.Should().Be("USD");
        usd.Amount.Should().Be(100m, "сумма в своей валюте, без пересчёта");
        usd.AmountRub.Should().Be(8_000m, "100 USD x 80 = 8000 RUB");
        var eur = _saved.Single(m => m.ExternalId == "eur-out");
        eur.Amount.Should().Be(-50m);
        eur.AmountRub.Should().BeNull("курса EUR нет");
    }

    [Fact]
    public async Task SyncAsync_SecurityTransfersAndInternalTransfers_NotCashMovements_AndNothingGoesToBondJournal()
    {
        SetupOperations(
            new TInvestOperation { Id = "sec-in", OperationType = "InputSecurities", Date = DaysAgo(4), PaymentRub = 0m, PaymentCurrency = "rub" },
            new TInvestOperation { Id = "multi", OperationType = "InpMulti", Date = DaysAgo(4), PaymentRub = 500m, PaymentCurrency = "rub" },
            new TInvestOperation { Id = "dep", OperationType = "Input", Date = DaysAgo(4), PaymentRub = 700m, PaymentCurrency = "rub" });

        await CreateService().SyncAsync(AccountId);

        _saved.Select(m => m.ExternalId).Should().Equal("dep");
        _operations.Verify(o => o.UpsertManyByExternalIdAsync(It.Is<IEnumerable<Operation>>(ops => ops.Any(x => x.ExternalId == "dep" || x.ExternalId == "multi"))), Times.Never,
            "пополнения не попадают в облигационный журнал");
    }

    [Fact]
    public async Task SyncAsync_RepeatedSync_ProducesSameExternalIds_SoUpsertIsIdempotent()
    {
        SetupOperations(new TInvestOperation { Id = "in-1", OperationType = "Input", Date = DaysAgo(10), PaymentRub = 1_000m, PaymentCurrency = "rub" });

        await CreateService().SyncAsync(AccountId);
        var first = _saved.Select(m => (m.AccountId, m.ExternalId)).ToList();
        await CreateService().SyncAsync(AccountId);

        _saved.Select(m => (m.AccountId, m.ExternalId)).Should().Equal(first);
        _movements.Verify(m => m.UpsertManyAsync(It.IsAny<IReadOnlyCollection<AccountCashMovement>>()), Times.Exactly(2));
    }

    [Fact]
    public async Task SyncAsync_StoringMovementsFails_BondJournalStillSynced_ErrorReported()
    {
        _movements.Setup(m => m.UpsertManyAsync(It.IsAny<IReadOnlyCollection<AccountCashMovement>>())).ThrowsAsync(new InvalidOperationException("db down"));
        SetupOperations(new TInvestOperation { Id = "in-1", OperationType = "Input", Date = DaysAgo(1), PaymentRub = 10m, PaymentCurrency = "rub" });

        var result = await CreateService().SyncAsync(AccountId);

        result.Errors.Should().ContainSingle(e => e.Contains("пополнения"));
        result.JournalSynced.Should().BeTrue("сбой побочной таблицы не отменяет синк журнала");
    }
}

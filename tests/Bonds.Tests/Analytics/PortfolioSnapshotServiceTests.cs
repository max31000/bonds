using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Bonds.Infrastructure.Analytics;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Bonds.Tests.Analytics;

/// <summary>
/// Тесты снимка портфеля "на сейчас" (plan/06 B1) с замоканными репозиториями. Проверяет
/// расчёт MarketValueRub (с НКД), деградацию при отсутствии котировки (не падает), и что
/// при сохранении используется upsert-репозиторий по (AccountId, AsOf).
/// </summary>
public class PortfolioSnapshotServiceTests
{
    private readonly Mock<IPositionRepository> _positions = new();
    private readonly Mock<IInstrumentRepository> _instruments = new();
    private readonly Mock<IMarketQuoteRepository> _quotes = new();
    private readonly Mock<IOperationRepository> _operations = new();
    private readonly Mock<IPortfolioValueSnapshotRepository> _snapshots = new();

    private const ulong AccountId = 1;
    private static readonly DateOnly AsOf = new(2025, 6, 1);

    private readonly Mock<ICouponScheduleRepository> _coupons = new();
    private readonly Mock<IAmortizationScheduleRepository> _amortizations = new();
    private readonly Mock<IOfferScheduleRepository> _offers = new();
    private readonly Mock<IYieldCurveRepository> _yieldCurve = new();

    private PortfolioSnapshotService CreateService() => new(
        new PortfolioHoldingsBuilder(
            _positions.Object, _instruments.Object, _coupons.Object, _amortizations.Object,
            _offers.Object, _quotes.Object, _yieldCurve.Object, _operations.Object),
        _instruments.Object, _operations.Object, _snapshots.Object,
        NullLogger<PortfolioSnapshotService>.Instance);

    private static Instrument LiveInstrument(ulong id) => new()
    {
        Id = id, FaceValue = 1000m, MaturityDate = AsOf.AddYears(3),
    };

    private static Operation Op(OperationType type, decimal amount) => new()
    {
        AccountId = AccountId,
        Type = type,
        Date = AsOf.AddDays(-10).ToDateTime(TimeOnly.MinValue),
        AmountRub = amount,
        ExternalId = Guid.NewGuid().ToString(),
    };

    [Fact]
    public async Task ComputeSnapshotAsync_UsesDirtyPriceTimesQuantity_ForMarketValue()
    {
        var position = new Position { Id = 1, AccountId = AccountId, InstrumentId = 10, Quantity = 5, Accrued = 1m };
        _positions.Setup(r => r.GetByAccountIdAsync(AccountId)).ReturnsAsync(new[] { position });
        _quotes.Setup(r => r.GetLatestAsync(10)).ReturnsAsync(new MarketQuote
        {
            InstrumentId = 10, AsOf = AsOf, CleanPrice = 980m, DirtyPrice = 990m,
        });
        _instruments.Setup(r => r.GetByIdAsync(10)).ReturnsAsync(LiveInstrument(10));
        _operations.Setup(r => r.GetByAccountIdAsync(AccountId, null, null)).ReturnsAsync(Array.Empty<Operation>());

        var snapshot = await CreateService().ComputeSnapshotAsync(AccountId, AsOf);

        snapshot.MarketValueRub.Should().Be(4950m, "990 (грязная цена) * 5 облигаций");
        snapshot.AccountId.Should().Be(AccountId);
        snapshot.AsOf.Should().Be(AsOf);
    }

    [Fact]
    public async Task ComputeSnapshotAsync_MissingQuote_ExcludesPositionButDoesNotThrow()
    {
        var withQuote = new Position { Id = 1, AccountId = AccountId, InstrumentId = 10, Quantity = 1 };
        var withoutQuote = new Position { Id = 2, AccountId = AccountId, InstrumentId = 20, Quantity = 1 };

        _positions.Setup(r => r.GetByAccountIdAsync(AccountId)).ReturnsAsync(new[] { withQuote, withoutQuote });
        _quotes.Setup(r => r.GetLatestAsync(10)).ReturnsAsync(new MarketQuote { InstrumentId = 10, AsOf = AsOf, DirtyPrice = 1000m });
        _quotes.Setup(r => r.GetLatestAsync(20)).ReturnsAsync((MarketQuote?)null);
        _instruments.Setup(r => r.GetByIdAsync(10)).ReturnsAsync(LiveInstrument(10));
        _instruments.Setup(r => r.GetByIdAsync(20)).ReturnsAsync(LiveInstrument(20));
        _operations.Setup(r => r.GetByAccountIdAsync(AccountId, null, null)).ReturnsAsync(Array.Empty<Operation>());

        var snapshot = await CreateService().ComputeSnapshotAsync(AccountId, AsOf);

        snapshot.MarketValueRub.Should().Be(1000m, "позиция без котировки исключена, но расчёт не падает (spec §4.4)");
    }

    [Fact]
    public async Task ComputeSnapshotAsync_CalculatesInvestedRub_NetOfSalesAndPrincipalReturns()
    {
        _positions.Setup(r => r.GetByAccountIdAsync(AccountId)).ReturnsAsync(Array.Empty<Position>());
        _operations.Setup(r => r.GetByAccountIdAsync(AccountId, null, null)).ReturnsAsync(new[]
        {
            Op(OperationType.Buy, -10000m), // брокер отдаёт покупку со знаком минус (отток со счёта)
            Op(OperationType.Amortization, 2000m), // возврат тела — приток, знак плюс
            Op(OperationType.Coupon, 500m), // купон не уменьшает вложенное тело
        });

        var snapshot = await CreateService().ComputeSnapshotAsync(AccountId, AsOf);

        snapshot.InvestedRub.Should().Be(8000m, "10000 покупка − 2000 амортизация; купон не входит в тело");
    }

    [Fact]
    public async Task ComputeAndStoreSnapshotAsync_CallsUpsert()
    {
        _positions.Setup(r => r.GetByAccountIdAsync(AccountId)).ReturnsAsync(Array.Empty<Position>());
        _operations.Setup(r => r.GetByAccountIdAsync(AccountId, null, null)).ReturnsAsync(Array.Empty<Operation>());
        _snapshots.Setup(r => r.UpsertAsync(It.IsAny<PortfolioValueSnapshot>())).Returns(Task.CompletedTask);

        await CreateService().ComputeAndStoreSnapshotAsync(AccountId, AsOf);

        _snapshots.Verify(r => r.UpsertAsync(It.Is<PortfolioValueSnapshot>(s => s.AccountId == AccountId && s.AsOf == AsOf)), Times.Once);
    }

    [Fact]
    public async Task GetHistoryAsync_DelegatesToRepository()
    {
        _snapshots.Setup(r => r.GetByAccountIdAsync(AccountId, null, null))
            .ReturnsAsync(new[] { new PortfolioValueSnapshot { AccountId = AccountId, AsOf = AsOf, MarketValueRub = 100m, InvestedRub = 90m } });

        var history = await CreateService().GetHistoryAsync(AccountId);

        history.Should().ContainSingle();
    }

    [Fact]
    public async Task ComputeSnapshotAsync_RedeemedPosition_ExcludedFromMarketValue_NoDoubleCountInXirr()
    {
        // plan/39 B2: у брокера бумага провисла после погашения, Redemption уже в журнале (приток учтён
        // в XIRR). Терминальная стоимость той же бумаги дала бы двойной учёт — должна быть 0.
        var position = new Position { Id = 1, AccountId = AccountId, InstrumentId = 10, Quantity = 10 };
        _positions.Setup(r => r.GetByAccountIdAsync(AccountId)).ReturnsAsync(new[] { position });
        _instruments.Setup(r => r.GetByIdAsync(10)).ReturnsAsync(new Instrument
        {
            Id = 10, FaceValue = 1000m, MaturityDate = AsOf.AddDays(-1),
        });
        _quotes.Setup(r => r.GetLatestAsync(10)).ReturnsAsync(new MarketQuote { InstrumentId = 10, AsOf = AsOf, DirtyPrice = 1000m });
        _operations.Setup(r => r.GetByAccountIdAsync(AccountId, null, null)).ReturnsAsync(new[]
        {
            InstrumentOp(OperationType.Buy, -10000m, 10, -40),
            InstrumentOp(OperationType.Redemption, 10000m, 10, -1),
        });

        var snapshot = await CreateService().ComputeSnapshotAsync(AccountId, AsOf);

        snapshot.MarketValueRub.Should().Be(0m);
        snapshot.XirrToDate.Should().NotBeNull();
        snapshot.XirrToDate!.Value.Should().BeApproximately(0m, 0.001m, "10000 вложено, 10000 вернулось, терминальной стоимости нет");
    }

    [Fact]
    public async Task ComputeSnapshotAsync_MaturedWithoutRedemptionYet_ValuedAtNominal_NotLastQuote()
    {
        var position = new Position { Id = 1, AccountId = AccountId, InstrumentId = 10, Quantity = 3 };
        _positions.Setup(r => r.GetByAccountIdAsync(AccountId)).ReturnsAsync(new[] { position });
        _instruments.Setup(r => r.GetByIdAsync(10)).ReturnsAsync(new Instrument
        {
            Id = 10, FaceValue = 1000m, MaturityDate = AsOf.AddDays(-1),
        });
        _quotes.Setup(r => r.GetLatestAsync(10)).ReturnsAsync(new MarketQuote { InstrumentId = 10, AsOf = AsOf.AddDays(-30), DirtyPrice = 985m });
        _operations.Setup(r => r.GetByAccountIdAsync(AccountId, null, null)).ReturnsAsync(Array.Empty<Operation>());

        var snapshot = await CreateService().ComputeSnapshotAsync(AccountId, AsOf);

        snapshot.MarketValueRub.Should().Be(3000m, "номинал 1000 x 3, а не устаревшая котировка 985");
    }

    [Fact]
    public async Task ComputeSnapshotAsync_MaturityDay_PositionStillValuedByQuote()
    {
        var position = new Position { Id = 1, AccountId = AccountId, InstrumentId = 10, Quantity = 3 };
        _positions.Setup(r => r.GetByAccountIdAsync(AccountId)).ReturnsAsync(new[] { position });
        _instruments.Setup(r => r.GetByIdAsync(10)).ReturnsAsync(new Instrument
        {
            Id = 10, FaceValue = 1000m, MaturityDate = AsOf, // в день погашения бумага ещё в портфеле
        });
        _quotes.Setup(r => r.GetLatestAsync(10)).ReturnsAsync(new MarketQuote { InstrumentId = 10, AsOf = AsOf, DirtyPrice = 999m });
        _operations.Setup(r => r.GetByAccountIdAsync(AccountId, null, null)).ReturnsAsync(Array.Empty<Operation>());

        var snapshot = await CreateService().ComputeSnapshotAsync(AccountId, AsOf);

        snapshot.MarketValueRub.Should().Be(2997m);
    }

    private static Operation InstrumentOp(OperationType type, decimal amount, ulong instrumentId, int daysFromAsOf) => new()
    {
        AccountId = AccountId,
        InstrumentId = instrumentId,
        Type = type,
        Date = AsOf.AddDays(daysFromAsOf).ToDateTime(TimeOnly.MinValue),
        AmountRub = amount,
        ExternalId = Guid.NewGuid().ToString(),
    };
}

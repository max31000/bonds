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
    private readonly Mock<IMarketQuoteRepository> _quotes = new();
    private readonly Mock<IOperationRepository> _operations = new();
    private readonly Mock<IPortfolioValueSnapshotRepository> _snapshots = new();

    private const ulong AccountId = 1;
    private static readonly DateOnly AsOf = new(2025, 6, 1);

    private PortfolioSnapshotService CreateService() => new(
        _positions.Object, _quotes.Object, _operations.Object, _snapshots.Object,
        NullLogger<PortfolioSnapshotService>.Instance);

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
}

using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Bonds.Infrastructure.Analytics;
using Bonds.Infrastructure.Connectors.Moex;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Bonds.Tests.Analytics;

/// <summary>
/// Тесты оркестратора бэкфилла (plan/15 §B.3): собирает вход из моков репозиториев/MOEX-клиента,
/// проверяет, что бэкфилл идемпотентен — не перезаписывает уже существующие снапшоты (живые или
/// от предыдущего запуска бэкфилла), и что цена ISS (% от номинала) переводится в рубли верно.
/// </summary>
public class PortfolioHistoryBackfillServiceTests
{
    private const ulong AccountId = 1;
    private const ulong InstrumentId = 10;
    private static readonly DateOnly BaseDate = new(2025, 1, 1);

    private readonly Mock<IOperationRepository> _operations = new();
    private readonly Mock<IInstrumentRepository> _instruments = new();
    private readonly Mock<IPortfolioValueSnapshotRepository> _snapshots = new();
    private readonly Mock<IMoexIssClient> _moex = new();

    private PortfolioHistoryBackfillService CreateService() => new(
        _operations.Object, _instruments.Object, _snapshots.Object, _moex.Object,
        NullLogger<PortfolioHistoryBackfillService>.Instance);

    private static Operation Op(OperationType type, DateOnly date, decimal amount, decimal? quantity, ulong? instrumentId = InstrumentId) => new()
    {
        AccountId = AccountId,
        InstrumentId = instrumentId,
        Type = type,
        Date = date.ToDateTime(TimeOnly.MinValue),
        AmountRub = amount,
        Quantity = quantity,
        ExternalId = Guid.NewGuid().ToString(),
    };

    private void SetupInstrument(string? secid = "RU000A10AZ45", decimal faceValue = 1000m)
    {
        _instruments.Setup(r => r.GetByIdAsync(InstrumentId)).ReturnsAsync(new Instrument
        {
            Id = InstrumentId,
            Isin = "RU000A10AZ45",
            Secid = secid,
            FaceValue = faceValue,
        });
    }

    [Fact]
    public async Task BackfillAsync_NoOperations_ReturnsZero_DoesNotCallMoex()
    {
        _operations.Setup(r => r.GetByAccountIdAsync(AccountId, null, null)).ReturnsAsync(Array.Empty<Operation>());

        var written = await CreateService().BackfillAsync(AccountId, BaseDate);

        written.Should().Be(0);
        _moex.Verify(m => m.GetHistoryPricesAsync(It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task BackfillAsync_ConvertsIssPercentPriceToRub_AndWritesSnapshots()
    {
        var operations = new[] { Op(OperationType.Buy, BaseDate, -10000m, quantity: 10m) };
        _operations.Setup(r => r.GetByAccountIdAsync(AccountId, null, null)).ReturnsAsync(operations);
        SetupInstrument(faceValue: 1000m);

        var asOf = BaseDate.AddDays(7);
        // 101.5% номинала (1000) + 2 руб НКД = 1017 руб грязная цена за бумагу.
        _moex.Setup(m => m.GetHistoryPricesAsync("RU000A10AZ45", BaseDate, asOf, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MoexHistoryPricePoint> { new(BaseDate, 101.5m, 2m) });

        _snapshots.Setup(r => r.GetByAccountIdAsync(AccountId, null, null)).ReturnsAsync(Array.Empty<PortfolioValueSnapshot>());

        PortfolioValueSnapshot? captured = null;
        _snapshots.Setup(r => r.UpsertAsync(It.IsAny<PortfolioValueSnapshot>()))
            .Callback<PortfolioValueSnapshot>(s => captured ??= s)
            .Returns(Task.CompletedTask);

        var written = await CreateService().BackfillAsync(AccountId, asOf);

        written.Should().Be(2, "два чекпоинта: BaseDate и asOf=BaseDate+7");
        captured.Should().NotBeNull();
        captured!.MarketValueRub.Should().Be(10170m, "10 шт × (101.5% × 1000 + 2 НКД) = 10 × 1017");
    }

    [Fact]
    public async Task BackfillAsync_SkipsDatesThatAlreadyHaveASnapshot_LiveSnapshotWins()
    {
        var operations = new[] { Op(OperationType.Buy, BaseDate, -10000m, quantity: 10m) };
        _operations.Setup(r => r.GetByAccountIdAsync(AccountId, null, null)).ReturnsAsync(operations);
        SetupInstrument();

        var asOf = BaseDate.AddDays(7);
        _moex.Setup(m => m.GetHistoryPricesAsync(It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MoexHistoryPricePoint> { new(BaseDate, 100m, 0m) });

        // Один из двух чекпоинтов (asOf) уже имеет "живой" снапшот — не должен быть перезаписан.
        _snapshots.Setup(r => r.GetByAccountIdAsync(AccountId, null, null)).ReturnsAsync(new[]
        {
            new PortfolioValueSnapshot { AccountId = AccountId, AsOf = asOf, MarketValueRub = 99999m, XirrToDate = 0.5m },
        });

        var written = await CreateService().BackfillAsync(AccountId, asOf);

        written.Should().Be(1, "только BaseDate дозаполняется — asOf уже занят живым снапшотом");
        _snapshots.Verify(r => r.UpsertAsync(It.Is<PortfolioValueSnapshot>(s => s.AsOf == asOf)), Times.Never,
            "живой снапшот на asOf не должен перезаписываться бэкфиллом");
        _snapshots.Verify(r => r.UpsertAsync(It.Is<PortfolioValueSnapshot>(s => s.AsOf == BaseDate)), Times.Once);
    }

    [Fact]
    public async Task BackfillAsync_ResolvesSecidByIsin_WhenInstrumentSecidIsMissing()
    {
        var operations = new[] { Op(OperationType.Buy, BaseDate, -10000m, quantity: 10m) };
        _operations.Setup(r => r.GetByAccountIdAsync(AccountId, null, null)).ReturnsAsync(operations);
        SetupInstrument(secid: null);

        _moex.Setup(m => m.ResolveSecidByIsinAsync("RU000A10AZ45", It.IsAny<CancellationToken>())).ReturnsAsync("RESOLVED123");
        _moex.Setup(m => m.GetHistoryPricesAsync("RESOLVED123", It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MoexHistoryPricePoint> { new(BaseDate, 100m, 0m) });
        _snapshots.Setup(r => r.GetByAccountIdAsync(AccountId, null, null)).ReturnsAsync(Array.Empty<PortfolioValueSnapshot>());

        var written = await CreateService().BackfillAsync(AccountId, BaseDate);

        written.Should().Be(1);
        _moex.Verify(m => m.ResolveSecidByIsinAsync("RU000A10AZ45", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task BackfillAsync_NoSecidResolvable_SkipsInstrumentGracefully_DoesNotThrow()
    {
        var operations = new[] { Op(OperationType.Buy, BaseDate, -10000m, quantity: 10m) };
        _operations.Setup(r => r.GetByAccountIdAsync(AccountId, null, null)).ReturnsAsync(operations);
        SetupInstrument(secid: null);
        _moex.Setup(m => m.ResolveSecidByIsinAsync("RU000A10AZ45", It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
        _snapshots.Setup(r => r.GetByAccountIdAsync(AccountId, null, null)).ReturnsAsync(Array.Empty<PortfolioValueSnapshot>());

        var written = await CreateService().BackfillAsync(AccountId, BaseDate);

        // Точка всё равно пишется (Approximate), просто без стоимости этой бумаги.
        written.Should().Be(1);
    }
}

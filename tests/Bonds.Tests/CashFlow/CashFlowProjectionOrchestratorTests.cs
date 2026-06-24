using Bonds.Core.CashFlow;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Bonds.Infrastructure.CashFlow;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Bonds.Tests.CashFlow;

/// <summary>
/// Тесты оркестратора проекции (plan/06 Часть A) с замоканными репозиториями — без реальной БД.
/// Проверяет координацию: сборку входа из репозиториев, идемпотентную замену по позиции,
/// устойчивость к сбою на одной позиции (продолжает с остальными).
/// </summary>
public class CashFlowProjectionOrchestratorTests
{
    private readonly Mock<IPositionRepository> _positions = new();
    private readonly Mock<IInstrumentRepository> _instruments = new();
    private readonly Mock<ICouponScheduleRepository> _coupons = new();
    private readonly Mock<IAmortizationScheduleRepository> _amortizations = new();
    private readonly Mock<IOfferScheduleRepository> _offers = new();
    private readonly Mock<IProjectedCashFlowRepository> _projectedCashFlows = new();

    private const ulong AccountId = 1;
    private static readonly DateOnly AsOf = new(2025, 1, 1);

    private CashFlowProjectionOrchestrator CreateService() => new(
        _positions.Object,
        _instruments.Object,
        _coupons.Object,
        _amortizations.Object,
        _offers.Object,
        _projectedCashFlows.Object,
        NullLogger<CashFlowProjectionOrchestrator>.Instance);

    private static Instrument MakeInstrument(ulong id, decimal faceValue, DateOnly maturity) => new()
    {
        Id = id,
        Isin = $"ISIN{id}",
        FaceValue = faceValue,
        MaturityDate = maturity,
        CouponType = CouponType.Fixed,
    };

    [Fact]
    public async Task ProjectAccountAsync_ReplacesProjectionForEachPosition()
    {
        var maturity = AsOf.AddDays(365);
        var position = new Position { Id = 10, AccountId = AccountId, InstrumentId = 5, Quantity = 3 };

        _positions.Setup(r => r.GetByAccountIdAsync(AccountId)).ReturnsAsync(new[] { position });
        _instruments.Setup(r => r.GetByIdAsync(5)).ReturnsAsync(MakeInstrument(5, 1000m, maturity));
        _coupons.Setup(r => r.GetByInstrumentIdAsync(5))
            .ReturnsAsync(new[] { new CouponSchedule { InstrumentId = 5, CouponDate = maturity, ValueRub = 50m, IsKnown = true } });
        _amortizations.Setup(r => r.GetByInstrumentIdAsync(5)).ReturnsAsync(Array.Empty<AmortizationSchedule>());
        _offers.Setup(r => r.GetByInstrumentIdAsync(5)).ReturnsAsync(Array.Empty<OfferSchedule>());

        IReadOnlyList<ProjectedCashFlow>? captured = null;
        _projectedCashFlows
            .Setup(r => r.ReplaceForPositionAsync(10, It.IsAny<IEnumerable<ProjectedCashFlow>>()))
            .Callback<ulong, IEnumerable<ProjectedCashFlow>>((_, flows) => captured = flows.ToList())
            .Returns(Task.CompletedTask);

        var result = await CreateService().ProjectAccountAsync(AccountId, AsOf);

        result.PositionsProjected.Should().Be(1);
        result.HasErrors.Should().BeFalse();
        captured.Should().NotBeNull();
        captured!.Should().HaveCount(2, "купон + погашение");
        captured!.Single(f => f.FlowType == CashFlowType.Coupon).GrossRub.Should().Be(150m, "50 * 3 облигации");
    }

    [Fact]
    public async Task ProjectAccountAsync_OneFailingPosition_DoesNotStopTheRest()
    {
        var maturity = AsOf.AddDays(365);
        var failingPosition = new Position { Id = 1, AccountId = AccountId, InstrumentId = 100, Quantity = 1 };
        var okPosition = new Position { Id = 2, AccountId = AccountId, InstrumentId = 200, Quantity = 1 };

        _positions.Setup(r => r.GetByAccountIdAsync(AccountId)).ReturnsAsync(new[] { failingPosition, okPosition });

        // Инструмент для failingPosition не найден — должно бросить контролируемое исключение, не упасть на весь батч.
        _instruments.Setup(r => r.GetByIdAsync(100)).ReturnsAsync((Instrument?)null);

        _instruments.Setup(r => r.GetByIdAsync(200)).ReturnsAsync(MakeInstrument(200, 1000m, maturity));
        _coupons.Setup(r => r.GetByInstrumentIdAsync(200))
            .ReturnsAsync(new[] { new CouponSchedule { InstrumentId = 200, CouponDate = maturity, ValueRub = 10m, IsKnown = true } });
        _amortizations.Setup(r => r.GetByInstrumentIdAsync(200)).ReturnsAsync(Array.Empty<AmortizationSchedule>());
        _offers.Setup(r => r.GetByInstrumentIdAsync(200)).ReturnsAsync(Array.Empty<OfferSchedule>());
        _projectedCashFlows
            .Setup(r => r.ReplaceForPositionAsync(2, It.IsAny<IEnumerable<ProjectedCashFlow>>()))
            .Returns(Task.CompletedTask);

        var result = await CreateService().ProjectAccountAsync(AccountId, AsOf);

        result.PositionsProjected.Should().Be(1, "только okPosition обработана успешно");
        result.HasErrors.Should().BeTrue();
        _projectedCashFlows.Verify(r => r.ReplaceForPositionAsync(1, It.IsAny<IEnumerable<ProjectedCashFlow>>()), Times.Never);
    }
}

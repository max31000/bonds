using Bonds.Core.Analytics;
using Bonds.Core.Models;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Analytics;

/// <summary>
/// Тесты cost basis по позиции (plan/14 §A) — средняя цена по двум лотам, частичная продажа,
/// купоны, "продано больше, чем куплено в журнале" (HasUnknownLots), пустой журнал.
/// Знак <see cref="Operation.AmountRub"/> задаётся явно на входе, как реально приходит от
/// брокера (см. doc-comment <see cref="PositionCostBasisService"/>) — покупка отрицательная,
/// купон/продажа положительные.
/// </summary>
public class PositionCostBasisServiceTests
{
    private static readonly DateOnly BaseDate = new(2025, 1, 1);
    private static ulong _nextId = 1;

    private static Operation Op(OperationType type, DateOnly date, decimal amount, decimal? quantity = null) => new()
    {
        Id = _nextId++,
        AccountId = 1,
        InstrumentId = 100,
        Type = type,
        Date = date.ToDateTime(TimeOnly.MinValue),
        AmountRub = amount,
        Quantity = quantity,
        ExternalId = Guid.NewGuid().ToString(),
    };

    [Fact]
    public void Calculate_TwoLotsAtDifferentPrices_ReturnsWeightedAverage()
    {
        // Лот 1: 10 шт по 1000 (итого -10000). Лот 2: 10 шт по 1100 (итого -11000).
        // Средняя = (10000 + 11000) / 20 = 1050.
        var operations = new[]
        {
            Op(OperationType.Buy, BaseDate, -10_000m, quantity: 10m),
            Op(OperationType.Buy, BaseDate.AddDays(10), -11_000m, quantity: 10m),
        };

        var result = PositionCostBasisService.Calculate(operations, currentQuantity: 20m, currentMarketValueRub: 22_000m);

        result.AverageCostRub.Should().Be(1050m);
        result.InvestedRub.Should().Be(21_000m);
        result.UnrealizedPnlRub.Should().Be(1_000m); // 22000 - 21000
        result.UnrealizedPnlPercent.Should().BeApproximately(1_000m / 21_000m, 1e-9m);
        result.HasUnknownLots.Should().BeFalse();
    }

    [Fact]
    public void Calculate_PartialSell_KeepsAverageCost_ButReducesInvested()
    {
        // Купили 20 по 1000 (средняя 1000). Продали 5 (остаток 15). Средняя цена не меняется.
        var operations = new[]
        {
            Op(OperationType.Buy, BaseDate, -20_000m, quantity: 20m),
            Op(OperationType.Sell, BaseDate.AddDays(30), 5_600m, quantity: 5m),
        };

        var result = PositionCostBasisService.Calculate(operations, currentQuantity: 15m, currentMarketValueRub: 16_500m);

        result.AverageCostRub.Should().Be(1000m); // не изменилась после частичной продажи
        result.InvestedRub.Should().Be(15_000m); // 1000 * 15
        result.HasUnknownLots.Should().BeFalse();
    }

    [Fact]
    public void Calculate_CouponsSummed_IncludedInTotalReturn_NotInUnrealizedPnl()
    {
        var operations = new[]
        {
            Op(OperationType.Buy, BaseDate, -10_000m, quantity: 10m),
            Op(OperationType.Coupon, BaseDate.AddDays(90), 250m),
            Op(OperationType.Coupon, BaseDate.AddDays(180), 250m),
        };

        var result = PositionCostBasisService.Calculate(operations, currentQuantity: 10m, currentMarketValueRub: 10_000m);

        result.CouponsReceivedRub.Should().Be(500m);
        result.UnrealizedPnlRub.Should().Be(0m); // рыночная стоимость = вложенное
        result.TotalReturnRub.Should().Be(500m); // только купоны
        result.TotalReturnPercent.Should().BeApproximately(500m / 10_000m, 1e-9m);
    }

    [Fact]
    public void Calculate_SoldMoreThanBoughtInJournal_SetsHasUnknownLots()
    {
        // Журнал начинается с продажи 10 шт, которых по журналу не покупали (позиция куплена до начала истории).
        var operations = new[]
        {
            Op(OperationType.Sell, BaseDate, 11_000m, quantity: 10m),
        };

        var result = PositionCostBasisService.Calculate(operations, currentQuantity: 0m, currentMarketValueRub: 0m);

        result.HasUnknownLots.Should().BeTrue();
    }

    [Fact]
    public void Calculate_CurrentQuantityDoesNotMatchJournal_SetsHasUnknownLots()
    {
        // Журнал показывает 10 купленных, но текущий остаток позиции — 25 (докуплено до начала истории синка).
        var operations = new[]
        {
            Op(OperationType.Buy, BaseDate, -10_000m, quantity: 10m),
        };

        var result = PositionCostBasisService.Calculate(operations, currentQuantity: 25m, currentMarketValueRub: 27_500m);

        result.HasUnknownLots.Should().BeTrue();
        // Средняя цена всё равно считается по известной части журнала применительно к текущему остатку.
        result.AverageCostRub.Should().Be(1000m);
    }

    [Fact]
    public void Calculate_EmptyJournal_ReturnsNullMetrics_ButZeroCoupons_AndUnknownLotsIfPositionNonEmpty()
    {
        var result = PositionCostBasisService.Calculate(Array.Empty<Operation>(), currentQuantity: 10m, currentMarketValueRub: 11_000m);

        result.AverageCostRub.Should().BeNull();
        result.InvestedRub.Should().BeNull();
        result.UnrealizedPnlRub.Should().BeNull();
        result.UnrealizedPnlPercent.Should().BeNull();
        result.TotalReturnRub.Should().BeNull();
        result.TotalReturnPercent.Should().BeNull();
        result.CouponsReceivedRub.Should().Be(0m);
        result.HasUnknownLots.Should().BeTrue("позиция непустая, а журнал пуст — история не покрывает остаток");
    }

    [Fact]
    public void Calculate_EmptyJournal_EmptyPosition_ReturnsNullMetrics_NoUnknownLots()
    {
        var result = PositionCostBasisService.Calculate(Array.Empty<Operation>(), currentQuantity: 0m, currentMarketValueRub: 0m);

        result.AverageCostRub.Should().BeNull();
        result.CouponsReceivedRub.Should().Be(0m);
        result.HasUnknownLots.Should().BeFalse("и журнал, и позиция пусты — расхождения нет");
    }

    [Fact]
    public void Calculate_OperationsForOtherInstrumentIgnoredByCaller_ThisServiceTrustsInputAsSingleInstrument()
    {
        // Сервис не фильтрует по InstrumentId — это ответственность вызывающего слоя (см. doc-comment).
        // Здесь просто проверяем, что операция без Quantity (например, комиссия) не ломает расчёт.
        var operations = new[]
        {
            Op(OperationType.Buy, BaseDate, -10_000m, quantity: 10m),
            Op(OperationType.Fee, BaseDate, -50m, quantity: null),
        };

        var result = PositionCostBasisService.Calculate(operations, currentQuantity: 10m, currentMarketValueRub: 10_000m);

        result.AverageCostRub.Should().Be(1000m);
        result.HasUnknownLots.Should().BeFalse();
    }
}

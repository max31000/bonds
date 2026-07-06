using Bonds.Core.Analytics;
using Bonds.Core.Models;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Analytics;

/// <summary>
/// Тесты оценки фактической ставки комиссии из журнала операций (plan/22 часть A). Чистый
/// сервис — вход/выход через записи, без I/O.
/// </summary>
public class CommissionRateEstimatorTests
{
    private static Operation Op(OperationType type, DateTime date, decimal amountRub) => new()
    {
        AccountId = 1,
        Type = type,
        Date = date,
        AmountRub = amountRub,
        ExternalId = Guid.NewGuid().ToString(),
    };

    [Fact]
    public void Estimate_TwoTradesTwoFees_ReturnsExactRate()
    {
        var asOf = new DateTime(2026, 7, 6);
        var operations = new List<Operation>
        {
            Op(OperationType.Buy, asOf.AddDays(-10), -100_000m),
            Op(OperationType.Fee, asOf.AddDays(-10), -46m), // 0.046%
            Op(OperationType.Sell, asOf.AddDays(-5), 50_000m),
            Op(OperationType.Fee, asOf.AddDays(-5), -23m), // 0.046%
        };

        var result = CommissionRateEstimator.Estimate(operations, asOf);

        result.Should().NotBeNull();
        result!.TurnoverRub.Should().Be(150_000m);
        result.FeeTotalRub.Should().Be(69m);
        result.Rate.Should().BeApproximately(69m / 150_000m, 0.0000001m);
        result.TradeCount.Should().Be(2);
        result.WindowMonths.Should().Be(6);
    }

    [Fact]
    public void Estimate_FewTrades_ExpandsWindowToFullHistory()
    {
        var asOf = new DateTime(2026, 7, 6);
        var operations = new List<Operation>
        {
            // Только 2 сделки в последние 6 мес, но есть более старые за пределами окна.
            Op(OperationType.Buy, asOf.AddDays(-30), -10_000m),
            Op(OperationType.Fee, asOf.AddDays(-30), -5m),
            Op(OperationType.Sell, asOf.AddDays(-20), 5_000m),
            Op(OperationType.Fee, asOf.AddDays(-20), -2.5m),
            // Старая сделка вне 6-месячного окна — должна попасть в расширенное окно "вся история".
            Op(OperationType.Buy, asOf.AddYears(-2), -20_000m),
            Op(OperationType.Fee, asOf.AddYears(-2), -10m),
        };

        var result = CommissionRateEstimator.Estimate(operations, asOf);

        result.Should().NotBeNull();
        result!.TradeCount.Should().Be(3, "сделок в 6-месячном окне < 5, окно расширяется на всю историю");
        result.TurnoverRub.Should().Be(35_000m);
        result.FeeTotalRub.Should().Be(17.5m);
        result.WindowMonths.Should().BeGreaterThan(6);
    }

    [Fact]
    public void Estimate_NoOperations_ReturnsNull()
    {
        var result = CommissionRateEstimator.Estimate([], new DateTime(2026, 7, 6));

        result.Should().BeNull();
    }

    [Fact]
    public void Estimate_NoTrades_ReturnsNull()
    {
        var asOf = new DateTime(2026, 7, 6);
        var operations = new List<Operation>
        {
            Op(OperationType.Coupon, asOf.AddDays(-5), 1000m),
            Op(OperationType.Tax, asOf.AddDays(-5), -130m),
        };

        var result = CommissionRateEstimator.Estimate(operations, asOf);

        result.Should().BeNull("нет ни одной сделки Buy/Sell — оборот равен нулю");
    }

    [Fact]
    public void Estimate_FeeWithoutTrades_ReturnsNull()
    {
        // Оборот 0 (нет Buy/Sell), но есть Fee-операция (например, сервисный сбор без сделок) —
        // делить не на что, результат null, а не деление на ноль/бесконечная ставка.
        var asOf = new DateTime(2026, 7, 6);
        var operations = new List<Operation>
        {
            Op(OperationType.Fee, asOf.AddDays(-5), -100m),
        };

        var result = CommissionRateEstimator.Estimate(operations, asOf);

        result.Should().BeNull();
    }

    [Fact]
    public void Estimate_SignOfAmountRub_DoesNotMatter()
    {
        // Buy обычно отрицательный (отток), Sell обычно положительный (приток) — знак сам по себе
        // не должен влиять на оценку, важен модуль суммы (оборот считается по |AmountRub|).
        var asOf = new DateTime(2026, 7, 6);
        var operationsNegativeSell = new List<Operation>
        {
            Op(OperationType.Buy, asOf.AddDays(-10), -100_000m),
            Op(OperationType.Fee, asOf.AddDays(-10), -30m),
            Op(OperationType.Sell, asOf.AddDays(-5), -50_000m), // необычный знак, не должен ломать расчёт
            Op(OperationType.Fee, asOf.AddDays(-5), -15m),
        };

        var result = CommissionRateEstimator.Estimate(operationsNegativeSell, asOf);

        result.Should().NotBeNull();
        result!.TurnoverRub.Should().Be(150_000m);
        result.FeeTotalRub.Should().Be(45m);
    }

    [Fact]
    public void Estimate_OperationsOutsideAccount_AreIgnoredByCaller()
    {
        // CommissionRateEstimator не фильтрует по AccountId — предполагается, что вызывающий код
        // уже передаёт журнал одного счёта (как и другие чистые сервисы этого слоя).
        var asOf = new DateTime(2026, 7, 6);
        var operations = new List<Operation>
        {
            Op(OperationType.Buy, asOf.AddDays(-10), -100_000m),
            Op(OperationType.Fee, asOf.AddDays(-10), -30m),
        };

        var result = CommissionRateEstimator.Estimate(operations, asOf);

        result.Should().NotBeNull();
    }
}

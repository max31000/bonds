using Bonds.Core.Analytics;
using Bonds.Core.Models;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Analytics;

/// <summary>
/// Тесты XIRR портфеля по журналу операций (plan/06 B1, spec §6.9). Эталонный журнал зеркалит
/// тест движка <c>XirrCalculatorTests.Calculate_SimpleJournal_MatchesHandComputedReference</c>
/// (покупка -1000 / купон +50 / купон +50 + терминал +1000), но на уровне Operation-журнала,
/// чтобы подтвердить, что конвенция знака (Buy/Tax/Fee минус, остальное плюс) воспроизводит
/// тот же результат, что движок даёт на уже подписанных потоках.
/// </summary>
public class PortfolioXirrServiceTests
{
    private const decimal Tolerance = 1e-4m;
    private static readonly DateOnly BaseDate = new(2025, 1, 1);

    private static Operation Op(OperationType type, DateOnly date, decimal amount) => new()
    {
        AccountId = 1,
        Type = type,
        Date = date.ToDateTime(TimeOnly.MinValue),
        AmountRub = amount,
        ExternalId = Guid.NewGuid().ToString(),
    };

    [Fact]
    public void Calculate_ReferenceJournal_MatchesHandComputedXirr()
    {
        var operations = new[]
        {
            Op(OperationType.Buy, BaseDate, 1000m), // абсолютное значение — знак выставляет сервис
            Op(OperationType.Coupon, BaseDate.AddDays(182), 50m),
            Op(OperationType.Coupon, BaseDate.AddDays(365), 50m),
        };

        // Терминальная стоимость на ту же дату последнего купона, как в эталонном тесте движка.
        var result = PortfolioXirrService.Calculate(operations, currentMarketValueRub: 1000m, asOf: BaseDate.AddDays(365));

        result.Should().NotBeNull();
        result!.Value.Rate.Should().BeApproximately(0.10250718991246802m, Tolerance);
    }

    [Fact]
    public void Calculate_NegatesBuyTaxAndFee_RegardlessOfStoredSign()
    {
        // Хранится "как пришло от брокера" — здесь умышленно с разным знаком на входе,
        // чтобы подтвердить, что сервис нормализует через Abs(), а не доверяет входному знаку.
        var operations = new[]
        {
            Op(OperationType.Buy, BaseDate, -1000m),
            Op(OperationType.Fee, BaseDate, 5m), // комиссия — должна стать отрицательной
            Op(OperationType.Tax, BaseDate.AddDays(180), 13m), // налог — должен стать отрицательным
            Op(OperationType.Coupon, BaseDate.AddDays(180), -100m), // купон — должен стать положительным
        };

        var result = PortfolioXirrService.Calculate(operations, currentMarketValueRub: 950m, asOf: BaseDate.AddDays(365));

        result.Should().NotBeNull("после нормализации знака есть и положительные, и отрицательные потоки");
    }

    [Fact]
    public void Calculate_NoOperations_NoTerminalValue_ReturnsNull()
    {
        PortfolioXirrService.Calculate(Array.Empty<Operation>(), currentMarketValueRub: 0m, asOf: BaseDate).Should().BeNull();
    }

    [Fact]
    public void Calculate_OnlyBuy_NoTerminalValue_ReturnsNull()
    {
        var operations = new[] { Op(OperationType.Buy, BaseDate, 1000m) };

        PortfolioXirrService.Calculate(operations, currentMarketValueRub: 0m, asOf: BaseDate).Should().BeNull(
            "единственный поток без терминальной стоимости — нет смены знака, корня нет");
    }

    [Fact]
    public void Calculate_BuyPlusTerminalValue_ReturnsPositiveRate()
    {
        var operations = new[] { Op(OperationType.Buy, BaseDate, 1000m) };

        var result = PortfolioXirrService.Calculate(operations, currentMarketValueRub: 1100m, asOf: BaseDate.AddDays(365));

        result.Should().NotBeNull();
        result!.Value.Rate.Should().BeApproximately(0.10m, 1e-3m);
    }
}

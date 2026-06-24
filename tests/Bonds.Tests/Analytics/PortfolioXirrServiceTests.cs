using Bonds.Core.Analytics;
using Bonds.Core.Models;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Analytics;

/// <summary>
/// Тесты XIRR портфеля по журналу операций (plan/06 B1, spec §6.9). Эталонный журнал зеркалит
/// тест движка <c>XirrCalculatorTests.Calculate_SimpleJournal_MatchesHandComputedReference</c>
/// (покупка -1000 / купон +50 / купон +50 + терминал +1000), но на уровне Operation-журнала.
/// <para>
/// Знак <see cref="Operation.AmountRub"/> в этих тестах задаётся ЯВНО на входе (как реально
/// приходит от T-Invest — брокер уже отдаёт сумму со знаком потока, см. doc-comment
/// <see cref="PortfolioXirrService"/>) — сервис больше не переписывает знак по типу операции
/// (пересмотрено при ревью этапов 04-06, см. историю файла/коммита).
/// </para>
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
            Op(OperationType.Buy, BaseDate, -1000m), // брокер отдаёт покупку со знаком минус
            Op(OperationType.Coupon, BaseDate.AddDays(182), 50m),
            Op(OperationType.Coupon, BaseDate.AddDays(365), 50m),
        };

        // Терминальная стоимость на ту же дату последнего купона, как в эталонном тесте движка.
        var result = PortfolioXirrService.Calculate(operations, currentMarketValueRub: 1000m, asOf: BaseDate.AddDays(365));

        result.Should().NotBeNull();
        result!.Value.Rate.Should().BeApproximately(0.10250718991246802m, Tolerance);
    }

    [Fact]
    public void Calculate_TrustsStoredSign_DoesNotRewriteByOperationType()
    {
        // Знак берётся напрямую из AmountRub, как пришло от брокера — сервис не подменяет его
        // по типу операции. Здесь налоговая корректировка (тип Tax) намеренно положительная —
        // это легитимный случай (возврат/коррекция переплаченного налога), который старая
        // реализация (Abs()+принудительный минус для Tax) считала бы неверно как отток.
        var operations = new[]
        {
            Op(OperationType.Buy, BaseDate, -1000m),
            Op(OperationType.Fee, BaseDate, -5m),
            Op(OperationType.Tax, BaseDate.AddDays(180), 13m), // возврат/коррекция налога — приток
            Op(OperationType.Coupon, BaseDate.AddDays(180), 100m),
        };

        var result = PortfolioXirrService.Calculate(operations, currentMarketValueRub: 950m, asOf: BaseDate.AddDays(365));

        result.Should().NotBeNull("есть и положительные, и отрицательные потоки — корень существует");
    }

    [Fact]
    public void Calculate_NoOperations_NoTerminalValue_ReturnsNull()
    {
        PortfolioXirrService.Calculate(Array.Empty<Operation>(), currentMarketValueRub: 0m, asOf: BaseDate).Should().BeNull();
    }

    [Fact]
    public void Calculate_OnlyBuy_NoTerminalValue_ReturnsNull()
    {
        var operations = new[] { Op(OperationType.Buy, BaseDate, -1000m) };

        PortfolioXirrService.Calculate(operations, currentMarketValueRub: 0m, asOf: BaseDate).Should().BeNull(
            "единственный поток без терминальной стоимости — нет смены знака, корня нет");
    }

    [Fact]
    public void Calculate_BuyPlusTerminalValue_ReturnsPositiveRate()
    {
        var operations = new[] { Op(OperationType.Buy, BaseDate, -1000m) };

        var result = PortfolioXirrService.Calculate(operations, currentMarketValueRub: 1100m, asOf: BaseDate.AddDays(365));

        result.Should().NotBeNull();
        result!.Value.Rate.Should().BeApproximately(0.10m, 1e-3m);
    }
}

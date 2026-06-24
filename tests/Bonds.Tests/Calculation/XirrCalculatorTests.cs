using Bonds.Core.Calculation;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Calculation;

/// <summary>
/// Тесты XIRR (spec §6.9, plan/05 Часть A.8/D). Эталон: покупка -1000 в день 0, купон +50
/// в день 182, купон +50 и терминальный поток +1000 (текущая стоимость) в день 365 —
/// посчитан вручную бисекцией по той же формуле NPV (Act/365), что реализована в калькуляторе;
/// результат ≈ 0.10250718991246802 (см. комментарий в коде расчёта эталона, продублирован
/// в истории работы агента). Допуск ±1e-4.
/// </summary>
public class XirrCalculatorTests
{
    private const decimal Tolerance = 1e-4m;
    private static readonly DateOnly BaseDate = new(2025, 1, 1);

    [Fact]
    public void Calculate_SimpleJournal_MatchesHandComputedReference()
    {
        var flows = new List<XirrCalculator.CashFlow>
        {
            new(BaseDate, -1000m),
            new(BaseDate.AddDays(182), 50m),
            new(BaseDate.AddDays(365), 1050m), // купон 50 + терминальная стоимость 1000
        };

        var result = XirrCalculator.Calculate(flows);

        result.Should().NotBeNull();
        result!.Value.Rate.Should().BeApproximately(0.10250718991246802m, Tolerance);
    }

    [Fact]
    public void Calculate_IrregularDates_StaysStable()
    {
        // Нерегулярные интервалы между датами — не должно влиять на устойчивость решателя.
        var flows = new List<XirrCalculator.CashFlow>
        {
            new(BaseDate, -1000m),
            new(BaseDate.AddDays(17), 30m),
            new(BaseDate.AddDays(203), 40m),
            new(BaseDate.AddDays(204), 0m), // дублирующая/нулевая дата-поток — не должно ломать расчёт
            new(BaseDate.AddDays(900), 1100m),
        };

        var result = XirrCalculator.Calculate(flows);

        result.Should().NotBeNull();
        // Не проверяем точное число (эталон не выводился вручную для этого кейса) — проверяем,
        // что решение лежит в разумных пределах и решатель не упал.
        result!.Value.Rate.Should().BeInRange(-0.5m, 1.0m);
    }

    [Fact]
    public void Calculate_AllPositiveFlows_ReturnsNull()
    {
        var flows = new List<XirrCalculator.CashFlow>
        {
            new(BaseDate, 100m),
            new(BaseDate.AddDays(100), 100m),
        };

        XirrCalculator.Calculate(flows).Should().BeNull();
    }

    [Fact]
    public void Calculate_SingleFlow_ReturnsNull()
    {
        var flows = new List<XirrCalculator.CashFlow> { new(BaseDate, -1000m) };

        XirrCalculator.Calculate(flows).Should().BeNull();
    }

    [Fact]
    public void Calculate_ConvergesByNewton_OnSmoothInput()
    {
        var flows = new List<XirrCalculator.CashFlow>
        {
            new(BaseDate, -1000m),
            new(BaseDate.AddDays(365), 1100m),
        };

        var result = XirrCalculator.Calculate(flows);

        result.Should().NotBeNull();
        result!.Value.ConvergedByNewton.Should().BeTrue();
    }
}

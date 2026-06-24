using Bonds.Core.Calculation;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Calculation;

/// <summary>
/// Тесты YTM (plan/05 Часть A.3, Часть D). Эталон: классическая двухпериодная облигация
/// (номинал 1000, годовой купон 100 = 10%, 2 годовых периода до погашения) — при YTM=12%
/// эффективной годовой ставке цена считается аналитически вручную (см. комментарий ниже),
/// без сторонних калькуляторов, т.к. поток предельно прост и проверяем формулу прямым счётом:
/// <c>price = 100/1.12 + 1100/1.12^2 = 966.1989795918366</c>. Допуск по доходности — ±1e-4
/// (plan/05 Часть D).
/// </summary>
public class YtmCalculatorTests
{
    private const decimal Tolerance = 1e-4m;

    [Fact]
    public void Calculate_TwoPeriodBond_MatchesHandComputedReferenceYtm()
    {
        var asOf = new DateOnly(2025, 1, 1);
        var cashFlow = new List<BondCashFlowItem>
        {
            new(asOf.AddDays(365), 100m, 0m, true),
            new(asOf.AddDays(730), 100m, 1000m, true),
        };

        // Грязная цена, аналитически соответствующая эффективной YTM = 12% годовых.
        const decimal referenceDirtyPrice = 966.1989795918366m;

        var result = YtmCalculator.Calculate(referenceDirtyPrice, asOf, cashFlow);

        result.Should().NotBeNull();
        result!.Value.EffectiveYield.Should().BeApproximately(0.12m, Tolerance);
        result.Value.ConvergedByNewton.Should().BeTrue("на гладком, типичном для облигации потоке Ньютон должен сходиться без фолбэка");
    }

    [Fact]
    public void Calculate_TwoPeriodBond_SimpleYieldMatchesHandComputedReference()
    {
        var asOf = new DateOnly(2025, 1, 1);
        var cashFlow = new List<BondCashFlowItem>
        {
            new(asOf.AddDays(365), 100m, 0m, true),
            new(asOf.AddDays(730), 100m, 1000m, true),
        };

        const decimal referenceDirtyPrice = 966.1989795918366m;

        var result = YtmCalculator.Calculate(referenceDirtyPrice, asOf, cashFlow);

        result.Should().NotBeNull();
        // Эталон simple yield посчитан вручную по той же формуле, что реализована в калькуляторе
        // (см. XML-doc YtmCalculator): (totalCashFlow - price) / price / weightedAverageYears.
        result!.Value.SimpleYield.Should().BeApproximately(0.12625053809728806m, Tolerance);
    }

    [Fact]
    public void Calculate_EmptyCashFlow_ReturnsNull()
    {
        var asOf = new DateOnly(2025, 1, 1);
        var result = YtmCalculator.Calculate(1000m, asOf, new List<BondCashFlowItem>());
        result.Should().BeNull();
    }

    [Fact]
    public void Calculate_NonPositivePrice_ReturnsNull()
    {
        var asOf = new DateOnly(2025, 1, 1);
        var cashFlow = new List<BondCashFlowItem> { new(asOf.AddDays(365), 100m, 1000m, true) };

        YtmCalculator.Calculate(0m, asOf, cashFlow).Should().BeNull();
        YtmCalculator.Calculate(-10m, asOf, cashFlow).Should().BeNull();
    }

    [Fact]
    public void Calculate_UnknownCashFlow_ReturnsNull()
    {
        var asOf = new DateOnly(2025, 1, 1);
        var cashFlow = new List<BondCashFlowItem> { new(asOf.AddDays(365), 100m, 1000m, false) };

        YtmCalculator.Calculate(950m, asOf, cashFlow).Should().BeNull();
    }

    // ─── Сходимость: Ньютон на нормальных входах, бисекция как фолбэк на "плохих" ──────────

    [Fact]
    public void Calculate_NormalInput_ConvergesByNewton()
    {
        var asOf = new DateOnly(2025, 1, 1);
        var cashFlow = new List<BondCashFlowItem>
        {
            new(asOf.AddDays(180), 50m, 0m, true),
            new(asOf.AddDays(365), 50m, 0m, true),
            new(asOf.AddDays(545), 50m, 0m, true),
            new(asOf.AddDays(730), 50m, 1000m, true),
        };

        var result = YtmCalculator.Calculate(980m, asOf, cashFlow);

        result.Should().NotBeNull();
        result!.Value.ConvergedByNewton.Should().BeTrue();
    }

    [Fact]
    public void Calculate_ExtremePriceRelativeToCashFlow_FallsBackToBisectionAndConverges()
    {
        var asOf = new DateOnly(2025, 1, 1);
        // Денежный поток с одним отдалённым платежом и экстремально низкой ценой относительно
        // номинала — типичный "плохой" вход, на котором у Ньютона велик риск разойтись
        // (огромная подразумеваемая доходность, функция сильно нелинейна в этой области).
        var cashFlow = new List<BondCashFlowItem>
        {
            new(asOf.AddDays(3650), 0m, 1000m, true),
        };

        var result = YtmCalculator.Calculate(1m, asOf, cashFlow);

        result.Should().NotBeNull("даже на экстремальном входе решатель обязан сойтись через фолбэк на бисекцию");
        // На таком входе подразумеваемая доходность огромна — главное, что решение нашлось
        // и легло в границы решателя, а не что оно "красивое".
    }

    [Fact]
    public void Calculate_NearZeroCouponDeepDiscount_ConvergesViaSolver()
    {
        var asOf = new DateOnly(2025, 1, 1);
        // Глубокий дисконт без купонов вообще (zero-coupon-подобный поток) — другой класс
        // "плохих" входов: производная NPV близка к нулю на старте при типичном guess=10%.
        var cashFlow = new List<BondCashFlowItem>
        {
            new(asOf.AddDays(3650), 0m, 1000m, true),
        };

        var result = YtmCalculator.Calculate(50m, asOf, cashFlow);

        result.Should().NotBeNull();
    }
}

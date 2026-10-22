using Bonds.Core.Currencies;
using Bonds.Core.Models;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Currencies;

/// <summary>Задача 55: чистые функции валюты - номинал MOEX, курс на дату, платёж не в рублях, единицы операции, разбивка денег, окна изменения курса.</summary>
public class CurrencyUnitsTests
{
    private static readonly DateOnly Today = new(2026, 10, 22);

    [Theory]
    [InlineData("12.2255", "1", "12.2255")]   // CNYRUB_TOM: FACEVALUE 1 - рубли за юань как есть
    [InlineData("8.2", "100", "0.082")]       // JPYRUB_TOM: CLOSE - рубли за 100 иен, за 1 иену в 100 раз меньше
    [InlineData("2.35", "100", "0.0235")]     // KZTRUB_TOM: тоже за 100
    public void RubPerUnit_DividesByFaceValue(string close, string face, string expected)
        => CurrencyRateUnits.RubPerUnit(decimal.Parse(close, System.Globalization.CultureInfo.InvariantCulture),
                decimal.Parse(face, System.Globalization.CultureInfo.InvariantCulture))
            .Should().Be(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public void RubPerUnit_NonPositiveFaceValue_Throws(string face)
        => FluentActions.Invoking(() => CurrencyRateUnits.RubPerUnit(10m, decimal.Parse(face)))
            .Should().Throw<ArgumentOutOfRangeException>();

    [Fact]
    public void RateOnOrBefore_ForwardFillsWeekends_AndRejectsStaleOrFuturePoints()
    {
        var history = new List<CurrencyRatePoint>
        {
            new(new DateOnly(2026, 10, 16), 12.46m), // пятница
            new(new DateOnly(2026, 10, 19), 12.4185m),
            new(new DateOnly(2026, 11, 2), 99m),     // будущее относительно запрашиваемой даты
        };

        CurrencyRateUnits.RateOnOrBefore(history, new DateOnly(2026, 10, 18)).Should().Be(12.46m, "суббота - курс пятницы");
        CurrencyRateUnits.RateOnOrBefore(history, new DateOnly(2026, 10, 19)).Should().Be(12.4185m);
        CurrencyRateUnits.RateOnOrBefore(history, new DateOnly(2026, 10, 15)).Should().BeNull("раньше первой точки курса нет");
        CurrencyRateUnits.RateOnOrBefore(history, new DateOnly(2026, 10, 31)).Should().BeNull("последняя точка старше 10 дней");
    }

    [Fact]
    public void ToRub_Rub_PassesThroughUnchanged_IncludingUnknownCurrency()
    {
        PaymentCurrencyConverter.ToRub(-13000m, "rub", null).Should().Be(new PaymentConversion(-13000m, false));
        PaymentCurrencyConverter.ToRub(-13000m, "RUB", 99m).Should().Be(new PaymentConversion(-13000m, false));
        PaymentCurrencyConverter.ToRub(5m, null, null).AmountRub.Should().Be(5m, "валюта платежа не указана - прежнее поведение (рубли)");
    }

    [Fact]
    public void ToRub_Cny_ConvertsByRate_KeepingBrokerSign()
    {
        // комиссия 10 юаней по курсу дня 12.5 -> 125 руб, знак брокера (списание) сохраняется
        var conv = PaymentCurrencyConverter.ToRub(-10m, "cny", 12.5m);
        conv.AmountRub.Should().Be(-125m);
        conv.WasConverted.Should().BeTrue();
        conv.Skipped.Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0")]
    [InlineData("-1")]
    public void ToRub_ForeignWithoutRate_IsSkipped_NotWrittenAsRubles(string? rate)
    {
        var conv = PaymentCurrencyConverter.ToRub(-10m, "cny", rate is null ? null : decimal.Parse(rate));
        conv.Skipped.Should().BeTrue();
        conv.AmountRub.Should().BeNull();
    }

    [Fact]
    public void OperationUnits_UsesQuantity_ElseDerivesFromPaymentAndPrice()
    {
        CurrencyOperationUnits.Resolve(1000m, -13000m, 13m).Should().Be(1000m);
        CurrencyOperationUnits.Resolve(null, -13000m, 13m).Should().Be(1000m, "|платёж| / цена за единицу");
        CurrencyOperationUnits.Resolve(0m, 5200m, 13m).Should().Be(400m);
        CurrencyOperationUnits.Resolve(null, -13000m, null).Should().BeNull();
        CurrencyOperationUnits.Resolve(null, 0m, 13m).Should().BeNull();
    }

    [Fact]
    public void OperationUnits_QuantityInLots_CrossCheckUsesPaymentOverPrice()
    {
        // покупка 1000 CNY по 12.5, платёж -12 500; брокер отдал quantity=1 (лот) -> 1000 единиц
        CurrencyOperationUnits.Resolve(1m, -12_500m, 12.5m, out var mismatch).Should().Be(1000m);
        mismatch.Should().BeTrue();
    }

    [Fact]
    public void OperationUnits_QuantityInUnits_UnchangedWithinTolerance()
    {
        CurrencyOperationUnits.Resolve(1000m, -12_500m, 12.5m, out var m1).Should().Be(1000m);
        m1.Should().BeFalse();
        // комиссия/округление цены в пределах 5% - не расхождение
        CurrencyOperationUnits.Resolve(1000m, -12_700m, 12.5m, out var m2).Should().Be(1000m);
        m2.Should().BeFalse();
    }

    [Fact]
    public void OperationUnits_NoPrice_QuantityAsIs()
    {
        CurrencyOperationUnits.Resolve(1m, -12_500m, null, out var mismatch).Should().Be(1m);
        mismatch.Should().BeFalse();
    }

    [Fact]
    public void CashRubOnly_SubtractsCurrency_FlooredAtZero_AndSumsBackToCash()
    {
        var cash = 100_000m;
        var currency = 12_000m;
        var only = CashBreakdown.CashRubOnly(cash, currency);
        only.Should().Be(88_000m);
        (only + currency).Should().Be(cash, "cashRubOnly + currencyValueRub = cashRub: итог не задваивается");
        CashBreakdown.CashRubOnly(5_000m, 12_000m).Should().Be(0m, "не ниже нуля (курс позиции мог разойтись с оценкой брокера)");
    }

    [Theory]
    [InlineData("CNYRUB_TOM", "CNY", "CNYRUB_TOM")]
    [InlineData("usd000utstom", "USD", "USD000UTSTOM")]
    [InlineData(null, "CNY", "CNYRUB_TOM")]
    [InlineData("CNY", "CNY", "CNYRUB_TOM")]      // тикер без суффикса расчётов - выводим из ISO
    [InlineData(null, "USD", "USD000UTSTOM")]
    [InlineData("JPYRUB_TOM", "jpy", "JPYRUB_TOM")]
    public void MoexSecid_Derive(string? ticker, string iso, string expected)
        => CurrencyMoexSecid.Derive(ticker, iso).Should().Be(expected);

    [Fact]
    public void MoexSecid_NoIso_NoTicker_ReturnsNull() => CurrencyMoexSecid.Derive(null, " ").Should().BeNull();

    [Fact]
    public void RateChange_ComputesFractionsPerWindow_NullWhenHistoryTooShort()
    {
        var history = new List<CurrencyRatePoint>
        {
            new(Today.AddMonths(-3), 11.0m),
            new(Today.AddMonths(-1), 12.0m),
            new(Today.AddDays(-1), 12.2m),
        };

        var change = CurrencyRateChangeCalculator.Compute(history, current: 12.6m, Today);

        change.M1.Should().Be(12.6m / 12.0m - 1m, "ДОЛЯ: +5%");
        change.M3.Should().Be(12.6m / 11.0m - 1m);
        change.M12.Should().BeNull("история короче года");
    }

    [Fact]
    public void RateChange_NoCurrentRate_AllNull()
        => CurrencyRateChangeCalculator.Compute([new CurrencyRatePoint(Today.AddMonths(-1), 12m)], null, Today)
            .Should().Be(new CurrencyRateChange(null, null, null));
}

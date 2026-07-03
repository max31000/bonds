using Bonds.Core.Analytics;
using Bonds.Core.Models;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Analytics;

/// <summary>
/// Юнит-тесты сборки суммарного интрадей-ряда из разреженных тиков по инструментам (plan/16
/// часть A) — план явно предупреждает "там легко ошибиться", поэтому краевые кейсы: сдвинутые
/// времена тиков между инструментами (forward-fill), инструмент без тиков вовсе, пустой вход,
/// один инструмент, тики строго в один момент времени.
/// </summary>
public class IntradaySeriesBuilderTests
{
    private static IntradayQuote Tick(ulong instrumentId, DateTime tsUtc, decimal dirtyPrice) =>
        new() { InstrumentId = instrumentId, TsUtc = tsUtc, DirtyPriceRub = dirtyPrice };

    private static readonly DateTime T0 = new(2026, 7, 3, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void EmptyInput_ReturnsEmptySeries()
    {
        var result = IntradaySeriesBuilder.Build(
            new Dictionary<ulong, IReadOnlyList<IntradayQuote>>(),
            new Dictionary<ulong, decimal>());

        result.Should().BeEmpty();
    }

    [Fact]
    public void SingleInstrument_ProducesOnePointPerTick()
    {
        var quotes = new Dictionary<ulong, IReadOnlyList<IntradayQuote>>
        {
            [1] = [Tick(1, T0, 1000m), Tick(1, T0.AddMinutes(1), 1010m)],
        };
        var quantities = new Dictionary<ulong, decimal> { [1] = 10m };

        var result = IntradaySeriesBuilder.Build(quotes, quantities);

        result.Should().HaveCount(2);
        result[0].TsUtc.Should().Be(T0);
        result[0].TotalMarketValueRub.Should().Be(10000m);
        result[1].TsUtc.Should().Be(T0.AddMinutes(1));
        result[1].TotalMarketValueRub.Should().Be(10100m);
    }

    // ─── Ключевой краевой кейс плана: сдвинутые времена двух инструментов → forward-fill ─────

    [Fact]
    public void TwoInstruments_OffsetTimestamps_ForwardFillsMissingInstrumentAtEachTimestamp()
    {
        // Инструмент 1 тикает в T0 и T0+2мин; инструмент 2 — в T0+1мин и T0+3мин (сдвинуты).
        var quotes = new Dictionary<ulong, IReadOnlyList<IntradayQuote>>
        {
            [1] = [Tick(1, T0, 1000m), Tick(1, T0.AddMinutes(2), 1020m)],
            [2] = [Tick(2, T0.AddMinutes(1), 500m), Tick(2, T0.AddMinutes(3), 510m)],
        };
        var quantities = new Dictionary<ulong, decimal> { [1] = 10m, [2] = 20m };

        var result = IntradaySeriesBuilder.Build(quotes, quantities);

        // Объединённые моменты: T0, T0+1, T0+2, T0+3
        result.Should().HaveCount(4);

        // T0: только инструмент 1 имеет данные (10 × 1000 = 10000); инструмент 2 ещё не тикнул — не участвует.
        result[0].TsUtc.Should().Be(T0);
        result[0].TotalMarketValueRub.Should().Be(10000m);

        // T0+1: инструмент 1 forward-fill (1000 × 10 = 10000) + инструмент 2 новый тик (500 × 20 = 10000) = 20000.
        result[1].TsUtc.Should().Be(T0.AddMinutes(1));
        result[1].TotalMarketValueRub.Should().Be(20000m);

        // T0+2: инструмент 1 новый тик (1020 × 10 = 10200) + инструмент 2 forward-fill (500 × 20 = 10000) = 20200.
        result[2].TsUtc.Should().Be(T0.AddMinutes(2));
        result[2].TotalMarketValueRub.Should().Be(20200m);

        // T0+3: инструмент 1 forward-fill (1020 × 10 = 10200) + инструмент 2 новый тик (510 × 20 = 10200) = 20400.
        result[3].TsUtc.Should().Be(T0.AddMinutes(3));
        result[3].TotalMarketValueRub.Should().Be(20400m);
    }

    [Fact]
    public void InstrumentWithNoTicksAtAll_IsExcludedFromEveryPoint_NotTreatedAsZero()
    {
        var quotes = new Dictionary<ulong, IReadOnlyList<IntradayQuote>>
        {
            [1] = [Tick(1, T0, 1000m)],
            [2] = [], // Инструмент без единого тика — например, сбой сети по этому FIGI на всём интервале.
        };
        var quantities = new Dictionary<ulong, decimal> { [1] = 10m, [2] = 5m };

        var result = IntradaySeriesBuilder.Build(quotes, quantities);

        result.Should().HaveCount(1);
        // Сумма учитывает только инструмент 1 — инструмент 2 не тянет сумму к заниженному значению нулём.
        result[0].TotalMarketValueRub.Should().Be(10000m);
    }

    [Fact]
    public void TicksAtExactSameTimestamp_ProduceSinglePointWithBothIncluded()
    {
        var quotes = new Dictionary<ulong, IReadOnlyList<IntradayQuote>>
        {
            [1] = [Tick(1, T0, 1000m)],
            [2] = [Tick(2, T0, 500m)],
        };
        var quantities = new Dictionary<ulong, decimal> { [1] = 10m, [2] = 20m };

        var result = IntradaySeriesBuilder.Build(quotes, quantities);

        result.Should().HaveCount(1);
        result[0].TotalMarketValueRub.Should().Be(20000m); // 10×1000 + 20×500
    }

    [Fact]
    public void UnorderedInputTicks_AreSortedBeforeForwardFill()
    {
        // Тики переданы не в хронологическом порядке — билдер должен сам отсортировать перед forward-fill.
        var quotes = new Dictionary<ulong, IReadOnlyList<IntradayQuote>>
        {
            [1] = [Tick(1, T0.AddMinutes(2), 1020m), Tick(1, T0, 1000m), Tick(1, T0.AddMinutes(1), 1010m)],
        };
        var quantities = new Dictionary<ulong, decimal> { [1] = 1m };

        var result = IntradaySeriesBuilder.Build(quotes, quantities);

        result.Should().HaveCount(3);
        result.Select(p => p.TotalMarketValueRub).Should().ContainInOrder(1000m, 1010m, 1020m);
    }

    [Fact]
    public void MissingQuantity_TreatsInstrumentAsZeroContribution()
    {
        // Инструмент есть в тиках, но не в словаре количеств (например, позиция уже закрыта между
        // сборкой quantityByInstrument и запросом истории) — не должен упасть, вклад = 0.
        var quotes = new Dictionary<ulong, IReadOnlyList<IntradayQuote>>
        {
            [1] = [Tick(1, T0, 1000m)],
        };
        var quantities = new Dictionary<ulong, decimal>();

        var result = IntradaySeriesBuilder.Build(quotes, quantities);

        result.Should().HaveCount(1);
        result[0].TotalMarketValueRub.Should().Be(0m);
    }
}

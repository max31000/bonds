using Bonds.Infrastructure.Quotes;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Quotes;

/// <summary>
/// Регрессия на баг единиц цены (продакшн: ОФЗ номиналом 1000 ₽ и котировкой 96.4% писалась в
/// intraday_quotes как 96.4 ₽ вместо ~964 ₽, портфель на графике занижался в разы). T-Invest
/// marketdata (TInvestQuote.LastPrice) отдаёт цену в пунктах (% от номинала), не в рублях —
/// см. doc-comment LiveQuoteConverter/TInvestQuote.LastPrice.
/// </summary>
public class LiveQuoteConverterTests
{
    [Fact]
    public void TryComputeDirtyPriceRub_ConvertsPointsToRubles_AndAddsAccrued()
    {
        // 96.4% от номинала 1000 = 964 ₽ чистыми + 64 ₽ НКД = 1028 ₽ грязными.
        var result = LiveQuoteConverter.TryComputeDirtyPriceRub(
            lastPricePoints: 96.4m,
            faceValue: 1000m,
            accruedRub: 64m,
            isOutOfScopeCurrency: false);

        result.Should().Be(1028.0m);
    }

    [Fact]
    public void TryComputeDirtyPriceRub_OutOfScopeCurrency_ReturnsNull()
    {
        // Валютный номинал — конвертация в рубли без курса невозможна, позиция должна быть
        // пропущена лёгким контуром (fallback на статичную цену полного синка), а не считаться неверно.
        var result = LiveQuoteConverter.TryComputeDirtyPriceRub(
            lastPricePoints: 96.4m,
            faceValue: 1000m,
            accruedRub: 64m,
            isOutOfScopeCurrency: true);

        result.Should().BeNull();
    }

    [Fact]
    public void TryComputeDirtyPriceRub_ZeroAccrued_ReturnsCleanPriceOnly()
    {
        var result = LiveQuoteConverter.TryComputeDirtyPriceRub(
            lastPricePoints: 100m,
            faceValue: 1000m,
            accruedRub: 0m,
            isOutOfScopeCurrency: false);

        result.Should().Be(1000.0m);
    }

    [Fact]
    public void TryComputeCleanPriceRub_ConvertsPointsToRubles()
    {
        var result = LiveQuoteConverter.TryComputeCleanPriceRub(
            lastPricePoints: 96.4m,
            faceValue: 1000m,
            isOutOfScopeCurrency: false);

        result.Should().Be(964.0m);
    }

    [Fact]
    public void TryComputeCleanPriceRub_OutOfScopeCurrency_ReturnsNull()
    {
        var result = LiveQuoteConverter.TryComputeCleanPriceRub(
            lastPricePoints: 96.4m,
            faceValue: 1000m,
            isOutOfScopeCurrency: true);

        result.Should().BeNull();
    }
}

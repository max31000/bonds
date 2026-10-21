using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Bonds.Infrastructure.Connectors.Moex;
using Bonds.Infrastructure.Connectors.TInvest;
using Bonds.Infrastructure.Sync;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Bonds.Tests.Sync;

/// <summary>Задача 51: суточный шаг рыночных данных фондов — ворота «раз в сутки», единицы, изоляция сбоев.</summary>
public class FundMarketDataRefresherTests
{
    private readonly Mock<IFundRepository> _funds = new();
    private readonly Mock<IFundMarketDataRepository> _market = new();
    private readonly Mock<IMoexIssClient> _moex = new();
    private readonly Mock<ITInvestPortfolioClient> _tInvest = new();

    // Среда 2026-10-21: завершённая сессия — вторник 2026-10-20.
    private static readonly DateOnly Today = new(2026, 10, 21);

    public FundMarketDataRefresherTests() => FundMarketDataRefresher.EmptyAttemptedOn.Clear();

    private FundMarketDataRefresher Create() => new(_funds.Object, _market.Object, _moex.Object, _tInvest.Object, NullLogger<FundMarketDataRefresher>.Instance);

    private void SetupFund()
    {
        _funds.Setup(f => f.GetAllAsync()).ReturnsAsync(new List<Fund> { new() { Id = 5, Figi = "F", Isin = "I", Ticker = "TPAY", Secid = "TPAY" } });
        _market.Setup(m => m.GetLastIndexDateAsync("RGBI", It.IsAny<CancellationToken>())).ReturnsAsync(Today.AddDays(-1));
    }

    [Theory]
    [InlineData("2026-10-21", "2026-10-20")] // среда -> вторник
    [InlineData("2026-10-19", "2026-10-16")] // понедельник -> пятница
    [InlineData("2026-10-24", "2026-10-23")] // суббота -> пятница
    [InlineData("2026-10-25", "2026-10-23")] // воскресенье -> пятница
    public void LastCompletedSessionDate_SkipsWeekends(string today, string expected)
        => FundMarketDataRefresher.LastCompletedSessionDate(DateOnly.Parse(today)).Should().Be(DateOnly.Parse(expected));

    [Fact]
    public async Task Refresh_FirstRun_LoadsTwoYearsOfPricesDistributionsAndRgbi()
    {
        SetupFund();
        _market.Setup(m => m.GetLastPriceDateAsync(5, It.IsAny<CancellationToken>())).ReturnsAsync((DateOnly?)null);
        _market.Setup(m => m.GetLastIndexDateAsync("RGBI", It.IsAny<CancellationToken>())).ReturnsAsync((DateOnly?)null);
        _moex.Setup(m => m.GetSharesHistoryAsync("TPAY", Today.AddYears(-2), Today, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MoexSharePricePoint(Today.AddDays(-1), 101.08m), new MoexSharePricePoint(Today.AddDays(-2), null)]);
        _tInvest.Setup(c => c.GetFundDistributionsAsync("F", Today.AddYears(-2), Today, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new TInvestFundDistribution(Today.AddDays(-30), Today.AddDays(-25), 0.85m)]);
        _moex.Setup(m => m.GetIndexHistoryAsync("RGBI", Today.AddYears(-2), Today, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MoexIndexPoint(Today.AddDays(-1), 111.2m, 0.1608m, 4.5m)]);

        var result = await Create().RefreshIfDueAsync(Today, tInvestAvailable: true);

        result.Should().Be(new FundMarketDataRefreshResult(1, 1, 1, 1));
        _market.Verify(m => m.UpsertPricesAsync(5, It.Is<IReadOnlyList<FundPricePoint>>(l => l.Count == 1 && l[0].CloseRub == 101.08m), It.IsAny<CancellationToken>()), Times.Once);
        _market.Verify(m => m.UpsertDistributionsAsync(5, It.Is<IReadOnlyList<FundDistribution>>(l => l.Single().AmountPerShareRub == 0.85m), It.IsAny<CancellationToken>()), Times.Once);
        _market.Verify(m => m.UpsertIndexHistoryAsync("RGBI", It.Is<IReadOnlyList<MarketIndexPoint>>(l => l.Single().YieldFraction == 0.1608m), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Refresh_FundWithoutAnyData_IsNotRetriedWithinTheSameDay()
    {
        SetupFund();
        _market.Setup(m => m.GetLastPriceDateAsync(5, It.IsAny<CancellationToken>())).ReturnsAsync((DateOnly?)null);
        _moex.Setup(m => m.GetSharesHistoryAsync("TPAY", It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        await Create().RefreshIfDueAsync(Today, tInvestAvailable: false);
        await Create().RefreshIfDueAsync(Today, tInvestAvailable: false);

        _moex.Verify(m => m.GetSharesHistoryAsync("TPAY", It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()), Times.Once);

        // На следующий день — новая попытка.
        await Create().RefreshIfDueAsync(Today.AddDays(1), tInvestAvailable: false);
        _moex.Verify(m => m.GetSharesHistoryAsync("TPAY", It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Refresh_DataAlreadyFresh_SkipsEverything()
    {
        SetupFund();
        _market.Setup(m => m.GetLastPriceDateAsync(5, It.IsAny<CancellationToken>())).ReturnsAsync(Today.AddDays(-1));

        var result = await Create().RefreshIfDueAsync(Today, tInvestAvailable: true);

        result.Should().Be(new FundMarketDataRefreshResult(0, 0, 0, 0));
        _moex.Verify(m => m.GetSharesHistoryAsync(It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()), Times.Never);
        _tInvest.Verify(c => c.GetFundDistributionsAsync(It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Refresh_Incremental_FetchesFromLastDate()
    {
        SetupFund();
        var last = Today.AddDays(-5);
        _market.Setup(m => m.GetLastPriceDateAsync(5, It.IsAny<CancellationToken>())).ReturnsAsync(last);
        _moex.Setup(m => m.GetSharesHistoryAsync("TPAY", last, Today, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MoexSharePricePoint(Today.AddDays(-1), 102m)]);

        await Create().RefreshIfDueAsync(Today, tInvestAvailable: false);

        _moex.Verify(m => m.GetSharesHistoryAsync("TPAY", last, Today, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Refresh_NoTInvestToken_SkipsDistributionsButLoadsPrices()
    {
        SetupFund();
        _market.Setup(m => m.GetLastPriceDateAsync(5, It.IsAny<CancellationToken>())).ReturnsAsync((DateOnly?)null);
        _moex.Setup(m => m.GetSharesHistoryAsync("TPAY", It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MoexSharePricePoint(Today.AddDays(-1), 101m)]);

        var result = await Create().RefreshIfDueAsync(Today, tInvestAvailable: false);

        result.PricePointsWritten.Should().Be(1);
        _tInvest.Verify(c => c.GetFundDistributionsAsync(It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Refresh_OneFundFails_DoesNotThrow_IndexStillRefreshed()
    {
        SetupFund();
        _market.Setup(m => m.GetLastPriceDateAsync(5, It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("db"));
        _market.Setup(m => m.GetLastIndexDateAsync("RGBI", It.IsAny<CancellationToken>())).ReturnsAsync((DateOnly?)null);
        _moex.Setup(m => m.GetIndexHistoryAsync("RGBI", It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MoexIndexPoint(Today.AddDays(-1), 111m, 0.16m, 4m)]);

        var result = await Create().RefreshIfDueAsync(Today, tInvestAvailable: true);

        result.IndexPointsWritten.Should().Be(1);
    }

    [Fact]
    public async Task Refresh_NoFunds_DoesNothing()
    {
        _funds.Setup(f => f.GetAllAsync()).ReturnsAsync(new List<Fund>());

        var result = await Create().RefreshIfDueAsync(Today, tInvestAvailable: true);

        result.Should().Be(new FundMarketDataRefreshResult(0, 0, 0, 0));
        _moex.VerifyNoOtherCalls();
    }
}

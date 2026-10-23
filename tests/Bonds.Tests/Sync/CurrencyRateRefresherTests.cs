using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Bonds.Infrastructure.Connectors.Cbr;
using Bonds.Infrastructure.Connectors.Moex;
using Bonds.Infrastructure.Sync;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Bonds.Tests.Sync;

/// <summary>Задача 55: суточный шаг истории курса валют - ворота «раз в сутки», деление на FACEVALUE, изоляция сбоев.</summary>
[Collection(CurrencyRateStaticCollection.Name)]
public class CurrencyRateRefresherTests
{
    private readonly Mock<ICurrencyRepository> _currencies = new();
    private readonly Mock<ICurrencyMarketDataRepository> _market = new();
    private readonly Mock<IMoexIssClient> _moex = new();

    // Среда 2026-10-21: завершённая сессия - вторник 2026-10-20.
    private static readonly DateOnly Today = new(2026, 10, 21);

    public CurrencyRateRefresherTests() => CurrencyRateRefresher.EmptyAttemptedOn.Clear();

    private readonly Mock<ICbrRatesClient> _cbr = new();

    private CurrencyRateRefresher Create(params string[] alwaysTracked) => new(
        _currencies.Object, _market.Object, _moex.Object, _cbr.Object,
        Options.Create(new CurrencyRateOptions { AlwaysTrackedCurrencies = alwaysTracked }),
        NullLogger<CurrencyRateRefresher>.Instance);

    private void SetupCurrency(decimal face = 1m, string secid = "CNYRUB_TOM", ulong id = 3)
        => _currencies.Setup(c => c.GetAllAsync()).ReturnsAsync(new List<Currency>
        {
            new() { Id = id, Figi = "F", IsoCode = "CNY", Ticker = "CNYRUB_TOM", MoexSecid = secid, FaceValue = face },
        });

    [Fact]
    public async Task Refresh_FirstRun_LoadsTwoYears_AndStoresRubPerUnit()
    {
        SetupCurrency();
        _market.Setup(m => m.GetLastRateDateAsync(3, It.IsAny<CancellationToken>())).ReturnsAsync((DateOnly?)null);
        _moex.Setup(m => m.GetCurrencyHistoryAsync("CNYRUB_TOM", Today.AddYears(-2), Today, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MoexCurrencyClosePoint(Today.AddDays(-1), 12.345m), new MoexCurrencyClosePoint(Today.AddDays(-2), null)]);

        var result = await Create().RefreshIfDueAsync(Today);

        result.Should().Be(new CurrencyRateRefreshResult(1, 1));
        _market.Verify(m => m.UpsertRatesAsync(3, It.Is<IReadOnlyList<CurrencyRatePoint>>(l => l.Count == 1 && l[0].CloseRub == 12.345m), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Refresh_JpyWithFaceValue100_ConvertsClosePer100ToRubPerYen()
    {
        SetupCurrency(face: 100m, secid: "JPYRUB_TOM");
        _market.Setup(m => m.GetLastRateDateAsync(3, It.IsAny<CancellationToken>())).ReturnsAsync((DateOnly?)null);
        _moex.Setup(m => m.GetCurrencyHistoryAsync("JPYRUB_TOM", It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MoexCurrencyClosePoint(Today.AddDays(-1), 8.2m)]);

        await Create().RefreshIfDueAsync(Today);

        _market.Verify(m => m.UpsertRatesAsync(3, It.Is<IReadOnlyList<CurrencyRatePoint>>(l => l.Single().CloseRub == 0.082m), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Refresh_Incremental_StartsFromLastStoredDate()
    {
        SetupCurrency();
        var last = Today.AddDays(-5);
        _market.Setup(m => m.GetLastRateDateAsync(3, It.IsAny<CancellationToken>())).ReturnsAsync(last);
        _moex.Setup(m => m.GetCurrencyHistoryAsync("CNYRUB_TOM", last, Today, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MoexCurrencyClosePoint(last, 12.1m), new MoexCurrencyClosePoint(Today.AddDays(-1), 12.2m)]);

        var result = await Create().RefreshIfDueAsync(Today);

        result.RatePointsWritten.Should().Be(2, "повтор последней даты идемпотентен");
        _moex.Verify(m => m.GetCurrencyHistoryAsync("CNYRUB_TOM", last, Today, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Refresh_UpToDate_SkipsMoex()
    {
        SetupCurrency();
        _market.Setup(m => m.GetLastRateDateAsync(3, It.IsAny<CancellationToken>())).ReturnsAsync(Today.AddDays(-1));

        var result = await Create().RefreshIfDueAsync(Today);

        result.Should().Be(new CurrencyRateRefreshResult(0, 0));
        _moex.Verify(m => m.GetCurrencyHistoryAsync(It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Refresh_EmptyCurrency_TriedOncePerCalendarDay()
    {
        SetupCurrency();
        _market.Setup(m => m.GetLastRateDateAsync(3, It.IsAny<CancellationToken>())).ReturnsAsync((DateOnly?)null);
        _moex.Setup(m => m.GetCurrencyHistoryAsync(It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var service = Create();

        await service.RefreshIfDueAsync(Today);
        await service.RefreshIfDueAsync(Today);
        _moex.Verify(m => m.GetCurrencyHistoryAsync(It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()), Times.Once);

        await service.RefreshIfDueAsync(Today.AddDays(1));
        _moex.Verify(m => m.GetCurrencyHistoryAsync(It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Refresh_OneCurrencyFails_OthersStillRefreshed()
    {
        _currencies.Setup(c => c.GetAllAsync()).ReturnsAsync(new List<Currency>
        {
            new() { Id = 3, Figi = "A", IsoCode = "CNY", MoexSecid = "CNYRUB_TOM" },
            new() { Id = 4, Figi = "B", IsoCode = "HKD", MoexSecid = "HKDRUB_TOM" },
        });
        _market.Setup(m => m.GetLastRateDateAsync(It.IsAny<ulong>(), It.IsAny<CancellationToken>())).ReturnsAsync((DateOnly?)null);
        _moex.Setup(m => m.GetCurrencyHistoryAsync("CNYRUB_TOM", It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("boom"));
        _moex.Setup(m => m.GetCurrencyHistoryAsync("HKDRUB_TOM", It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MoexCurrencyClosePoint(Today.AddDays(-1), 1.6m)]);

        var result = await Create().RefreshIfDueAsync(Today);

        result.Should().Be(new CurrencyRateRefreshResult(1, 1));
        _market.Verify(m => m.UpsertRatesAsync(4, It.IsAny<IReadOnlyList<CurrencyRatePoint>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Refresh_NoCurrencies_DoesNothing()
    {
        _currencies.Setup(c => c.GetAllAsync()).ReturnsAsync(new List<Currency>());

        (await Create().RefreshIfDueAsync(Today)).Should().Be(new CurrencyRateRefreshResult(0, 0));
    }
    // --- Задача 60: всегда отслеживаемые USD/EUR/GBP ---

    [Fact]
    public async Task Refresh_AlwaysTracked_SeedsMissingCurrencies_WithPlaceholderFigi_AndMoexSecidOnlyForUsd()
    {
        _currencies.Setup(c => c.GetByIsoAsync(It.IsAny<string>())).ReturnsAsync((Currency?)null);
        _currencies.Setup(c => c.GetAllAsync()).ReturnsAsync(new List<Currency>());

        await Create("USD", "EUR", "GBP").RefreshIfDueAsync(Today);

        _currencies.Verify(c => c.UpsertAsync(It.Is<Currency>(x => x.IsoCode == "USD" && x.Figi == "FX:USD" && x.MoexSecid == "USD000UTSTOM")), Times.Once);
        _currencies.Verify(c => c.UpsertAsync(It.Is<Currency>(x => x.IsoCode == "EUR" && x.Figi == "FX:EUR" && x.MoexSecid == null)), Times.Once);
        _currencies.Verify(c => c.UpsertAsync(It.Is<Currency>(x => x.IsoCode == "GBP" && x.Figi == "FX:GBP" && x.MoexSecid == null)), Times.Once);
    }

    [Fact]
    public async Task Refresh_AlwaysTracked_ExistingCurrencyRow_IsNotOverwritten()
    {
        _currencies.Setup(c => c.GetByIsoAsync("USD")).ReturnsAsync(new Currency { Id = 1, Figi = "BBG0013HGFT4", IsoCode = "USD" });
        _currencies.Setup(c => c.GetAllAsync()).ReturnsAsync(new List<Currency>());

        await Create("USD").RefreshIfDueAsync(Today);

        _currencies.Verify(c => c.UpsertAsync(It.IsAny<Currency>()), Times.Never);
    }

    [Fact]
    public async Task Refresh_AlwaysTracked_UnknownIso_IsSkippedWithoutFailing()
    {
        _currencies.Setup(c => c.GetAllAsync()).ReturnsAsync(new List<Currency>());

        await Create("XXX").RefreshIfDueAsync(Today);

        _currencies.Verify(c => c.UpsertAsync(It.IsAny<Currency>()), Times.Never);
    }

    [Fact]
    public async Task Refresh_Eur_UsesCbrOnly_AndNeverCallsMoex()
    {
        // EUR на CETS не торгуется (EUR_RUB__TOM: CLOSE = 0) - источник только ЦБ; MOEX с выведенным EURRUB_TOM не опрашивается.
        _currencies.Setup(c => c.GetAllAsync()).ReturnsAsync(new List<Currency> { new() { Id = 5, Figi = "FX:EUR", IsoCode = "EUR" } });
        _market.Setup(m => m.GetLastRateDateAsync(5, It.IsAny<CancellationToken>())).ReturnsAsync((DateOnly?)null);
        _cbr.Setup(c => c.GetRatesAsync("R01239", Today.AddYears(-2), Today, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new CbrRatePoint(Today.AddDays(-1), 91.1364m)]);

        var result = await Create().RefreshIfDueAsync(Today);

        result.Should().Be(new CurrencyRateRefreshResult(1, 1));
        _market.Verify(m => m.UpsertRatesAsync(5, It.Is<IReadOnlyList<CurrencyRatePoint>>(l => l.Single().CloseRub == 91.1364m), It.IsAny<CancellationToken>()), Times.Once);
        _moex.Verify(m => m.GetCurrencyHistoryAsync(It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Refresh_Usd_PrefersMoex_AndFallsBackToCbrWhenMoexIsEmpty()
    {
        _currencies.Setup(c => c.GetAllAsync()).ReturnsAsync(new List<Currency> { new() { Id = 6, Figi = "FX:USD", IsoCode = "USD", MoexSecid = "USD000UTSTOM" } });
        _market.Setup(m => m.GetLastRateDateAsync(6, It.IsAny<CancellationToken>())).ReturnsAsync((DateOnly?)null);
        _moex.Setup(m => m.GetCurrencyHistoryAsync("USD000UTSTOM", It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MoexCurrencyClosePoint(Today.AddDays(-1), 0m)]);
        _cbr.Setup(c => c.GetRatesAsync("R01235", It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new CbrRatePoint(Today.AddDays(-1), 83.9497m)]);

        await Create().RefreshIfDueAsync(Today);

        _market.Verify(m => m.UpsertRatesAsync(6, It.Is<IReadOnlyList<CurrencyRatePoint>>(l => l.Single().CloseRub == 83.9497m), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Refresh_Usd_MoexHasData_CbrNotCalled()
    {
        _currencies.Setup(c => c.GetAllAsync()).ReturnsAsync(new List<Currency> { new() { Id = 6, Figi = "FX:USD", IsoCode = "USD", MoexSecid = "USD000UTSTOM" } });
        _market.Setup(m => m.GetLastRateDateAsync(6, It.IsAny<CancellationToken>())).ReturnsAsync((DateOnly?)null);
        _moex.Setup(m => m.GetCurrencyHistoryAsync("USD000UTSTOM", It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MoexCurrencyClosePoint(Today.AddDays(-1), 84.18m)]);

        await Create().RefreshIfDueAsync(Today);

        _cbr.Verify(c => c.GetRatesAsync(It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}

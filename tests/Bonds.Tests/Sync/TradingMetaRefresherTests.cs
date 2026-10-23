using Bonds.Core.Credentials;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Bonds.Infrastructure.Connectors.Freedom;
using Bonds.Infrastructure.Connectors.Freedom.Models;
using Bonds.Infrastructure.Connectors.TInvest;
using Bonds.Infrastructure.Sync;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Bonds.Tests.Sync;

/// <summary>
/// Задача 61: <see cref="TradingMetaRefresher"/> с фейковыми клиентами брокеров и in-memory репозиторием метаданных — без сети и БД.
/// Живые факты (счёт T-Invest, 2026-10): LQDT TQBR api=false / SPBRU api=true; TMON@ SPBRU lot 1; TPAY платит помесячно, TMON@/TBRU@/SBFR —
/// нет; SBMM/AKMM TQBR api=false.
/// </summary>
public class TradingMetaRefresherTests
{
    private static readonly DateTime Now = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);

    private readonly Mock<ITInvestPortfolioClient> _tInvest = new();
    private readonly Mock<IFreedomApiClient> _freedom = new();
    private readonly Mock<IUserCredentialStore> _credentials = new();
    private readonly Mock<IAccountRepository> _accounts = new();
    private readonly Mock<IPositionRepository> _positions = new();
    private readonly Mock<IInstrumentRepository> _instruments = new();
    private readonly Mock<IFundPositionRepository> _fundPositions = new();
    private readonly Mock<IFundRepository> _funds = new();
    private readonly Mock<IBrokerPositionRepository> _brokerPositions = new();
    private readonly InMemoryMetaRepository _meta = new();
    private readonly TradingMetaOptions _options = new() { FundUniverse = [], FreedomExtraTickers = [], QueryDelay = TimeSpan.Zero };

    public TradingMetaRefresherTests()
    {
        _tInvest.Setup(c => c.GetInstrumentListingsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _tInvest.Setup(c => c.CountDistributionsAsync(It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
        _credentials.Setup(c => c.ListUserIdsWithValidAsync(CredentialProviders.Freedom)).ReturnsAsync(Array.Empty<ulong>());
        _accounts.Setup(a => a.GetPrimaryAccountIdAsync()).ReturnsAsync((ulong?)null);
        _positions.Setup(p => p.GetByAccountIdAsync(It.IsAny<ulong>())).ReturnsAsync(Array.Empty<Position>());
        _fundPositions.Setup(p => p.GetByAccountAsync(It.IsAny<ulong>())).ReturnsAsync(Array.Empty<FundPosition>());
    }

    private TradingMetaRefresher Create() => new(
        _tInvest.Object, _freedom.Object, _credentials.Object, _accounts.Object, _positions.Object, _instruments.Object,
        _fundPositions.Object, _funds.Object, _brokerPositions.Object, _meta, Options.Create(_options), NullLogger<TradingMetaRefresher>.Instance);

    private static TInvestInstrumentListing Fund(string ticker, string classCode, bool api, string? isin = null, decimal step = 0.01m, decimal lot = 1m) => new()
    {
        Uid = $"uid-{ticker}-{classCode}", Figi = $"figi-{ticker}", Ticker = ticker, ClassCode = classCode, Isin = isin ?? $"RU-{ticker}", IsBond = false,
        Lot = lot, MinPriceIncrementRaw = step, Nominal = null, Currency = "RUB", ApiTradeAvailable = api, BuyAvailable = true, SellAvailable = true,
    };

    private static TInvestInstrumentListing Bond(string isin, decimal stepPoints, decimal nominal, bool api = true, string nominalCurrency = "RUB") => new()
    {
        Uid = "uid-" + isin, Figi = "figi-" + isin, Ticker = "SU" + isin[^4..], ClassCode = "TQOB", Isin = isin, IsBond = true,
        Lot = 1m, MinPriceIncrementRaw = stepPoints, Nominal = nominal, NominalCurrency = nominalCurrency, Currency = "RUB", ApiTradeAvailable = api, BuyAvailable = true, SellAvailable = true,
    };

    private void SetListings(string query, params TInvestInstrumentListing[] listings) =>
        _tInvest.Setup(c => c.GetInstrumentListingsAsync(query, It.IsAny<CancellationToken>())).ReturnsAsync(listings);

    // --- T-Invest ---

    [Fact]
    public async Task FundUniverse_Lqdt_PicksSpbruListing_WithApiTrue()
    {
        _options.FundUniverse = ["LQDT"];
        SetListings("LQDT", Fund("LQDT", "TQBR", api: false, isin: "RU000A0JVW48"), Fund("LQDT", "SPBRU", api: true, isin: "RU000A0JVW48"));

        var result = await Create().RefreshIfDueAsync(Now, tInvestAvailable: true);

        result.TInvestUpserted.Should().Be(1);
        var row = _meta.Rows.Single();
        row.Broker.Should().Be("tinvest");
        row.InstrumentKey.Should().Be("RU000A0JVW48", "ключ - ISIN");
        row.ClassCode.Should().Be("SPBRU");
        row.ApiTradeAvailable.Should().BeTrue();
        row.Uid.Should().Be("uid-LQDT-SPBRU");
    }

    [Fact]
    public async Task Fund_TqbrOnlyApiFalse_StoredWithApiFalse()
    {
        _options.FundUniverse = ["SBMM"];
        SetListings("SBMM", Fund("SBMM", "TQBR", api: false));

        await Create().RefreshIfDueAsync(Now, tInvestAvailable: true);

        _meta.Rows.Single().ApiTradeAvailable.Should().BeFalse();
        _meta.Rows.Single().ClassCode.Should().Be("TQBR");
    }

    [Fact]
    public async Task PaysDistributions_FromCountOverYear_TpayTrue_TmonFalse()
    {
        _options.FundUniverse = ["TPAY", "TMON@"];
        SetListings("TPAY", Fund("TPAY", "TQBR", api: true));
        SetListings("TMON@", Fund("TMON@", "SPBRU", api: true));
        _tInvest.Setup(c => c.CountDistributionsAsync("uid-TPAY-TQBR", It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>())).ReturnsAsync(12);
        _tInvest.Setup(c => c.CountDistributionsAsync("uid-TMON@-SPBRU", It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>())).ReturnsAsync(0);

        await Create().RefreshIfDueAsync(Now, tInvestAvailable: true);

        _meta.Rows.Single(r => r.Ticker == "TPAY").PaysDistributions.Should().BeTrue();
        _meta.Rows.Single(r => r.Ticker == "TMON@").PaysDistributions.Should().BeFalse();
        _tInvest.Verify(c => c.CountDistributionsAsync("uid-TPAY-TQBR", Now.Date.AddDays(-365).ToDateOnlyUtc(), Now.Date.ToDateOnlyUtc(), It.IsAny<CancellationToken>()), Times.Once,
            "окно - 365 дней до сегодня");
    }

    [Fact]
    public async Task PaysDistributions_LookupFailed_KeepsPreviousValue_NotZero()
    {
        _options.FundUniverse = ["TPAY"];
        _meta.Rows.Add(new InstrumentTradingMeta
        {
            Broker = "tinvest", InstrumentKey = "RU-TPAY", Ticker = "TPAY", Lot = 1, MinPriceIncrement = 0.01m, Currency = "RUB",
            PaysDistributions = true, UpdatedAt = Now.AddDays(-3),
        });
        SetListings("TPAY", Fund("TPAY", "TQBR", api: true));
        _tInvest.Setup(c => c.CountDistributionsAsync(It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>())).ReturnsAsync((int?)null);

        await Create().RefreshIfDueAsync(Now, tInvestAvailable: true);

        _meta.Rows.Single().PaysDistributions.Should().BeTrue("не удалось узнать != нет выплат");
    }

    [Fact]
    public async Task Bond_StepConvertedFromPointsToRubles_RawPointsKept()
    {
        var accountId = 5UL;
        _accounts.Setup(a => a.GetPrimaryAccountIdAsync()).ReturnsAsync(accountId);
        _positions.Setup(p => p.GetByAccountIdAsync(accountId)).ReturnsAsync(new[] { new Position { AccountId = accountId, InstrumentId = 9, Quantity = 10 } });
        _instruments.Setup(i => i.GetByIdAsync(9)).ReturnsAsync(new Instrument { Id = 9, Isin = "RU000A1038V6" });
        SetListings("RU000A1038V6", Bond("RU000A1038V6", stepPoints: 0.01m, nominal: 1000m));

        await Create().RefreshIfDueAsync(Now, tInvestAvailable: true);

        var row = _meta.Rows.Single();
        row.InstrumentKey.Should().Be("RU000A1038V6");
        row.MinPriceIncrement.Should().Be(0.1m, "0.01 пункта при номинале 1000 = 0.1 ₽");
        row.MinPriceIncrementPoints.Should().Be(0.01m);
        row.Lot.Should().Be(1m);
        row.PaysDistributions.Should().BeNull("у облигации признак не применим");
        _tInvest.Verify(c => c.CountDistributionsAsync(It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void MapTInvest_BondWithNonRubNominal_RubStepNull_PointsKept()
    {
        // Живой эталон: номинал 100 USD, шаг 0.0001 пункта - рублёвого шага нет, торговать по сетке пунктов.
        var meta = TradingMetaRefresher.MapTInvest(Bond("XS0000000001", 0.0001m, 100m, nominalCurrency: "USD"), Now);

        meta.Should().NotBeNull();
        meta!.MinPriceIncrement.Should().BeNull("номинал не в рублях - рублёвый шаг не считаем");
        meta.MinPriceIncrementPoints.Should().Be(0.0001m);
    }

    [Fact]
    public void MapTInvest_BondRubNominal_RubStepComputed()
    {
        var meta = TradingMetaRefresher.MapTInvest(Bond("RU000A1038V6", 0.01m, 1000m), Now);

        meta!.MinPriceIncrement.Should().Be(0.1m);
        meta.MinPriceIncrementPoints.Should().Be(0.01m);
    }

    [Fact]
    public async Task FailedTicker_NotRetriedWithinBackoff_RetriedAfter()
    {
        _options.FundUniverse = ["FAILBACKOFF1"];
        _tInvest.Setup(c => c.GetInstrumentListingsAsync("FAILBACKOFF1", It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("boom"));

        var first = await Create().RefreshIfDueAsync(Now, tInvestAvailable: true);
        var second = await Create().RefreshIfDueAsync(Now.AddMinutes(30), tInvestAvailable: true);
        await Create().RefreshIfDueAsync(Now.AddMinutes(61), tInvestAvailable: true);

        first.Errors.Should().ContainSingle();
        second.Errors.Should().BeEmpty("тикер в бэкоффе 1 час");
        _tInvest.Verify(c => c.GetInstrumentListingsAsync("FAILBACKOFF1", It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task QueryCap_LimitsListingQueriesPerCycle_RestOnNextCycle()
    {
        _options.FundUniverse = ["CAPA", "CAPB", "CAPC"];
        _options.MaxQueriesPerCycle = 2;
        foreach (var t in _options.FundUniverse) SetListings(t, Fund(t, "TQBR", api: true));

        var first = await Create().RefreshIfDueAsync(Now, tInvestAvailable: true);
        var second = await Create().RefreshIfDueAsync(Now.AddMinutes(1), tInvestAvailable: true);

        first.TInvestUpserted.Should().Be(2);
        second.TInvestUpserted.Should().Be(1, "оставшийся тикер добирается следующим циклом, свежие не перезапрашиваются");
        _meta.Rows.Should().HaveCount(3);
    }

    [Fact]
    public async Task Fund_Step_StoredAsIs_InRublesPerShare_WithoutPoints()
    {
        _options.FundUniverse = ["TMON@"];
        SetListings("TMON@", Fund("TMON@", "SPBRU", api: true, step: 0.01m));

        await Create().RefreshIfDueAsync(Now, tInvestAvailable: true);

        var row = _meta.Rows.Single();
        row.MinPriceIncrement.Should().Be(0.01m);
        row.MinPriceIncrementPoints.Should().BeNull();
        row.Lot.Should().Be(1m);
    }

    [Fact]
    public async Task HeldFundsAndUniverse_Deduplicated_OneLookupPerTicker()
    {
        var accountId = 5UL;
        _options.FundUniverse = ["TPAY"];
        _accounts.Setup(a => a.GetPrimaryAccountIdAsync()).ReturnsAsync(accountId);
        _fundPositions.Setup(p => p.GetByAccountAsync(accountId)).ReturnsAsync(new[] { new FundPosition { AccountId = accountId, FundId = 3, Quantity = 5 } });
        _funds.Setup(f => f.GetByIdAsync(3)).ReturnsAsync(new Fund { Id = 3, Isin = "RU-TPAY", Ticker = "TPAY" });
        SetListings("RU-TPAY", Fund("TPAY", "TQBR", api: true));
        SetListings("TPAY", Fund("TPAY", "TQBR", api: true));

        await Create().RefreshIfDueAsync(Now, tInvestAvailable: true);

        _meta.Rows.Should().ContainSingle("ISIN-ключ и тикер указывают на одну строку");
    }

    [Fact]
    public async Task FreshRows_NotRefetched_StaleRows_Are()
    {
        _options.FundUniverse = ["TPAY", "TMON@"];
        _meta.Rows.Add(new InstrumentTradingMeta { Broker = "tinvest", InstrumentKey = "RU-TPAY", Ticker = "TPAY", Lot = 1, MinPriceIncrement = 0.01m, Currency = "RUB", UpdatedAt = Now.AddHours(-2) });
        _meta.Rows.Add(new InstrumentTradingMeta { Broker = "tinvest", InstrumentKey = "RU-TMON@", Ticker = "TMON@", Lot = 1, MinPriceIncrement = 0.01m, Currency = "RUB", UpdatedAt = Now.AddHours(-30) });
        SetListings("TPAY", Fund("TPAY", "TQBR", api: true));
        SetListings("TMON@", Fund("TMON@", "SPBRU", api: true));

        await Create().RefreshIfDueAsync(Now, tInvestAvailable: true);

        _tInvest.Verify(c => c.GetInstrumentListingsAsync("TPAY", It.IsAny<CancellationToken>()), Times.Never, "запись моложе 20 ч");
        _tInvest.Verify(c => c.GetInstrumentListingsAsync("TMON@", It.IsAny<CancellationToken>()), Times.Once);
        _meta.Rows.Single(r => r.Ticker == "TMON@").UpdatedAt.Should().Be(Now);
    }

    [Fact]
    public async Task OneInstrumentFails_OthersStillRefreshed_ErrorReported_NoThrow()
    {
        _options.FundUniverse = ["BAD", "TPAY"];
        _tInvest.Setup(c => c.GetInstrumentListingsAsync("BAD", It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("boom"));
        SetListings("TPAY", Fund("TPAY", "TQBR", api: true));

        var result = await Create().RefreshIfDueAsync(Now, tInvestAvailable: true);

        result.TInvestUpserted.Should().Be(1);
        result.Errors.Should().ContainSingle(e => e.Contains("BAD"));
    }

    [Fact]
    public async Task TInvestUnavailable_NoCallsToTInvest()
    {
        _options.FundUniverse = ["TPAY"];

        await Create().RefreshIfDueAsync(Now, tInvestAvailable: false);

        _tInvest.Verify(c => c.GetInstrumentListingsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void MapTInvest_BondWithoutNominal_IsRejected_NotStoredWithWrongStep()
    {
        TradingMetaRefresher.MapTInvest(Bond("RU000A1038V6", 0.01m, 0m), Now).Should().BeNull();
    }

    // --- Freedom ---

    private const ulong FreedomUser = 77;

    private void SetupFreedomUser(string[] positionTickers, params FreedomQuote[] quotes)
    {
        _credentials.Setup(c => c.ListUserIdsWithValidAsync(CredentialProviders.Freedom)).ReturnsAsync(new ulong[] { FreedomUser });
        _credentials.Setup(c => c.GetSecretResultAsync(FreedomUser, CredentialProviders.Freedom)).ReturnsAsync(new CredentialSecretResult(
            CredentialReadOutcome.Ok, new Dictionary<string, string> { ["publicKey"] = "pub", ["privateKey"] = "priv" }));
        _accounts.Setup(a => a.GetByUserIdAndBrokerAsync(FreedomUser, Bonds.Core.Models.Brokers.Freedom)).ReturnsAsync(new[] { new Account { Id = 900, UserId = FreedomUser, Broker = Bonds.Core.Models.Brokers.Freedom } });
        _brokerPositions.Setup(p => p.GetByAccountAsync(900)).ReturnsAsync(positionTickers.Select(t => new BrokerPosition { AccountId = 900, Ticker = t }).ToArray());
        _freedom.Setup(f => f.GetQuotesAsync(It.IsAny<FreedomCredentials>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(quotes);
    }

    [Fact]
    public async Task Freedom_PositionsPlusExtras_Lot1_MinStepFromQuote_ItsTwinDetected()
    {
        _options.FreedomExtraTickers = ["SPYM.US"];
        SetupFreedomUser(["VUAA.EU"],
            new FreedomQuote("SPYM.US", 700m, 701m, 700.5m, "USD", 0.01m),
            new FreedomQuote("SPYM.ITS", 699m, 702m, 700m, "USD", 0.01m),
            new FreedomQuote("VUAA.EU", 100m, 100.1m, 100m, "EUR", 0.005m),
            new FreedomQuote("VUAA.ITS", null, null, null, null, null)); // запись есть, но пустая - двойника нет

        var result = await Create().RefreshIfDueAsync(Now, tInvestAvailable: false);

        result.FreedomUpserted.Should().Be(2);
        var spym = _meta.Rows.Single(r => r.InstrumentKey == "SPYM.US");
        spym.Broker.Should().Be("freedom");
        spym.Lot.Should().Be(1m);
        spym.MinPriceIncrement.Should().Be(0.01m);
        spym.Currency.Should().Be("USD");
        spym.ItsTwin.Should().BeTrue();
        spym.ApiTradeAvailable.Should().BeTrue();
        var vuaa = _meta.Rows.Single(r => r.InstrumentKey == "VUAA.EU");
        vuaa.ItsTwin.Should().BeFalse("пустая запись котировки - не двойник");
        vuaa.MinPriceIncrement.Should().Be(0.005m);
        vuaa.Currency.Should().Be("EUR");
    }

    [Fact]
    public async Task Freedom_TwinAbsentFromResponse_ItsTwinFalse()
    {
        SetupFreedomUser(["ROBO.EU"], new FreedomQuote("ROBO.EU", 25m, 25.1m, 25m, "EUR", 0.01m));

        await Create().RefreshIfDueAsync(Now, tInvestAvailable: false);

        _meta.Rows.Single().ItsTwin.Should().BeFalse();
    }

    [Fact]
    public async Task Freedom_UnknownTicker_EmptyQuote_NoRow()
    {
        SetupFreedomUser(["NOPE.US"], new FreedomQuote("NOPE.US", null, null, null, null, null));

        var result = await Create().RefreshIfDueAsync(Now, tInvestAvailable: false);

        result.FreedomUpserted.Should().Be(0);
        _meta.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Freedom_ItsTicker_QuoteOnly_NotTradable_NoTwinLookup()
    {
        SetupFreedomUser(["SPYM.ITS"], new FreedomQuote("SPYM.ITS", 699m, 702m, 700m, "USD", 0.01m));

        await Create().RefreshIfDueAsync(Now, tInvestAvailable: false);

        var row = _meta.Rows.Single();
        row.ApiTradeAvailable.Should().BeFalse("*.ITS только котировочные");
        row.BuyAvailable.Should().BeFalse();
        row.ItsTwin.Should().BeNull();
    }

    [Fact]
    public async Task Freedom_NoCredentials_NoApiCalls()
    {
        _options.FreedomExtraTickers = ["SPYM.US"];

        await Create().RefreshIfDueAsync(Now, tInvestAvailable: false);

        _freedom.Verify(f => f.GetQuotesAsync(It.IsAny<FreedomCredentials>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("SPYM.US", "SPYM.ITS")]
    [InlineData("VUAA.EU", "VUAA.ITS")]
    [InlineData("LUV", "LUV.ITS")]
    public void ItsTwinOf_ReplacesExchangeSuffix(string ticker, string expected) => TradingMetaRefresher.ItsTwinOf(ticker).Should().Be(expected);

    // --- in-memory репозиторий ---

    private sealed class InMemoryMetaRepository : IInstrumentTradingMetaRepository
    {
        public List<InstrumentTradingMeta> Rows { get; } = [];

        public Task UpsertAsync(InstrumentTradingMeta meta)
        {
            Rows.RemoveAll(r => r.Broker == meta.Broker && string.Equals(r.InstrumentKey, meta.InstrumentKey, StringComparison.OrdinalIgnoreCase));
            Rows.Add(meta);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<InstrumentTradingMeta>> GetByBrokerAsync(string broker) =>
            Task.FromResult<IReadOnlyList<InstrumentTradingMeta>>(Rows.Where(r => r.Broker == broker).ToList());

        public Task<IReadOnlyList<InstrumentTradingMeta>> GetByKeysAsync(string broker, IReadOnlyCollection<string> keys) =>
            Task.FromResult<IReadOnlyList<InstrumentTradingMeta>>(Rows.Where(r => r.Broker == broker && keys.Contains(r.InstrumentKey, StringComparer.OrdinalIgnoreCase)).ToList());
    }
}

internal static class DateTimeTestExtensions
{
    public static DateOnly ToDateOnlyUtc(this DateTime value) => DateOnly.FromDateTime(value);
}

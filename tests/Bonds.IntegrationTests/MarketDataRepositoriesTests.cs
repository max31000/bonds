using Bonds.Core.Models;
using Bonds.Infrastructure.Repositories;
using Bonds.IntegrationTests.Infrastructure;
using FluentAssertions;
using Xunit;

namespace Bonds.IntegrationTests;

/// <summary>
/// Round-trip тесты для IMarketQuoteRepository (временной ряд котировок) и IYieldCurveRepository
/// (снимки Gcurve), включая upsert-идемпотентность по уникальным ключам (plan/03 §B/§D).
/// </summary>
[Collection("Integration")]
public class MarketDataRepositoriesTests
{
    private readonly TestWebApplicationFactory _factory;

    public MarketDataRepositoriesTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private async Task<ulong> CreateInstrumentAsync()
    {
        var repo = new InstrumentRepository(_factory.Database.ConnectionString);
        var isin = $"RU{Guid.NewGuid():N}".Substring(0, 12);
        return await repo.UpsertAsync(new Instrument
        {
            Isin = isin,
            FaceValue = 1000m,
            Currency = "RUB",
            CouponType = CouponType.Fixed,
            MaturityDate = new DateOnly(2031, 1, 1),
        });
    }

    [Fact]
    public async Task MarketQuote_Upsert_Then_GetLatest_RoundTrips()
    {
        var instrumentId = await CreateInstrumentAsync();
        var repo = new MarketQuoteRepository(_factory.Database.ConnectionString);

        var quote = new MarketQuote
        {
            InstrumentId = instrumentId,
            AsOf = new DateOnly(2025, 6, 1),
            CleanPrice = 985.30m,
            DirtyPrice = 990.10m,
            Accrued = 4.80m,
            Volume = 1_000_000m,
            Source = MarketQuoteSource.TInvest,
        };

        await repo.UpsertAsync(quote);

        var latest = await repo.GetLatestAsync(instrumentId);
        latest.Should().NotBeNull();
        latest!.CleanPrice.Should().Be(985.30m);
        latest.Source.Should().Be(MarketQuoteSource.TInvest);
    }

    [Fact]
    public async Task MarketQuote_UpsertSameInstrumentAsOfSource_UpdatesInPlace_NoDuplicate()
    {
        var instrumentId = await CreateInstrumentAsync();
        var repo = new MarketQuoteRepository(_factory.Database.ConnectionString);
        var asOf = new DateOnly(2025, 6, 2);

        await repo.UpsertAsync(new MarketQuote
        {
            InstrumentId = instrumentId,
            AsOf = asOf,
            CleanPrice = 100m,
            Source = MarketQuoteSource.Moex,
        });

        await repo.UpsertAsync(new MarketQuote
        {
            InstrumentId = instrumentId,
            AsOf = asOf,
            CleanPrice = 101.5m,
            Source = MarketQuoteSource.Moex,
        });

        var history = await repo.GetHistoryAsync(instrumentId, asOf, asOf);
        history.Should().HaveCount(1, "повторная загрузка котировки на ту же дату и источник не должна дублировать строку");
        history.Single().CleanPrice.Should().Be(101.5m);
    }

    [Fact]
    public async Task MarketQuote_SameInstrumentAsOf_DifferentSource_KeepsBothRows()
    {
        var instrumentId = await CreateInstrumentAsync();
        var repo = new MarketQuoteRepository(_factory.Database.ConnectionString);
        var asOf = new DateOnly(2025, 6, 3);

        await repo.UpsertAsync(new MarketQuote { InstrumentId = instrumentId, AsOf = asOf, CleanPrice = 100m, Source = MarketQuoteSource.TInvest });
        await repo.UpsertAsync(new MarketQuote { InstrumentId = instrumentId, AsOf = asOf, CleanPrice = 102m, Source = MarketQuoteSource.Moex });

        var history = await repo.GetHistoryAsync(instrumentId, asOf, asOf);
        history.Should().HaveCount(2, "T-Invest и MOEX — разные источники на одну дату, оба должны сохраняться (plan/00 §4)");
    }

    [Fact]
    public async Task YieldCurve_Upsert_Then_GetByDate_RoundTrips_AndIsIdempotent()
    {
        var repo = new YieldCurveRepository(_factory.Database.ConnectionString);
        var asOf = new DateOnly(2025, 6, 1);

        var snapshot = new YieldCurveSnapshot
        {
            AsOf = asOf,
            B1 = 7.5m, B2 = -2.1m, B3 = 1.3m, T1 = 1.7m,
            G1 = 7.1m, G2 = 7.3m, G3 = 7.5m, G4 = 7.6m, G5 = 7.7m,
            G6 = 7.8m, G7 = 7.9m, G8 = 8.0m, G9 = 8.1m,
        };

        await repo.UpsertAsync(snapshot);
        await repo.UpsertAsync(snapshot); // повторная загрузка той же даты — не должна дублировать

        var loaded = await repo.GetByDateAsync(asOf);
        loaded.Should().NotBeNull();
        loaded!.B1.Should().Be(7.5m);

        var history = await repo.GetHistoryAsync(asOf, asOf);
        history.Should().HaveCount(1);
    }
}

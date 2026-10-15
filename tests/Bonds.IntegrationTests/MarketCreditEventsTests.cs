using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bonds.Core.Analytics;
using Bonds.Core.Models;
using Bonds.Core.Signals;
using Bonds.Core.Time;
using Bonds.Infrastructure.Repositories;
using Bonds.Infrastructure.Universe;
using Bonds.IntegrationTests.Infrastructure;
using Dapper;
using FluentAssertions;
using MySqlConnector;
using Xunit;

namespace Bonds.IntegrationTests;

/// <summary>
/// Задача 49 часть B на реальной БД: новые типы событий хранятся (ENUM миграции 022), батчевые методы банка
/// (по ISIN / история по secid) работают одним запросом, сборка входов + движок дают события, пороги — настройки пользователя.
/// История пишется на искусственные даты далеко в будущем (вне окон других тестов) и удаляется в finally.
/// </summary>
[Collection("Integration")]
public class MarketCreditEventsTests
{
    private readonly TestWebApplicationFactory _factory;

    public MarketCreditEventsTests(TestWebApplicationFactory factory) => _factory = factory;

    private string Conn => _factory.Database.ConnectionString;

    private static string Marker() => Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    private static BondUniverseEntry Bond(string secid, string isin, decimal gspread, decimal price = 100m) => new()
    {
        Secid = secid, Isin = isin, ShortName = secid, FaceValue = 1000m, Sector = "Корпоративные", YieldFraction = 0.15m,
        DurationYears = 2m, PricePercent = price, TurnoverRub = 5_000_000m, BidPercent = 99.9m, OfferPercent = 100m, ListLevel = 1,
        GspreadOfficialFraction = gspread, MaturityDate = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(3)), UpdatedAt = DateTime.UtcNow,
    };

    // ─── Хранение новых типов ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(SignalType.SpreadWidening, SignalSeverity.Warning)]
    [InlineData(SignalType.PriceDrawdown, SignalSeverity.Warning)]
    [InlineData(SignalType.IssuerDefaultEvent, SignalSeverity.Critical)]
    public async Task SignalRepository_StoresNewEventTypes(SignalType type, SignalSeverity severity)
    {
        var userId = await new UserRepository(Conn).CreateAsync(new User { TelegramId = Random.Shared.NextInt64(1, long.MaxValue) });
        var accountId = await new AccountRepository(Conn).CreateAsync(new Account { UserId = userId, Name = "Счёт" });
        var repo = new SignalRepository(Conn);
        var key = Signal.BuildKey(type, $"X{Marker()}", null);

        await repo.InsertAsync(new Signal
        {
            AccountId = accountId, EventKey = key, Type = type, Severity = severity, Subject = "Тест",
            FirstSeenAt = DateTime.UtcNow, LastSeenAt = DateTime.UtcNow,
        });

        var stored = (await repo.GetByAccountIdAsync(accountId)).Should().ContainSingle().Subject;
        stored.Type.Should().Be(type);
        stored.Severity.Should().Be(severity);
    }

    // ─── Батчевые методы банка ────────────────────────────────────────────────────────────

    [Fact]
    public async Task BondUniverseRepository_GetByIsinsAsync_ReturnsOnlyRequested_InOneCall()
    {
        var marker = Marker();
        var repo = new BondUniverseRepository(Conn);
        await repo.UpsertSnapshotBatchAsync([Bond($"A{marker}", $"IA{marker}", 0.02m), Bond($"B{marker}", $"IB{marker}", 0.02m), Bond($"C{marker}", $"IC{marker}", 0.02m)]);

        var found = await repo.GetByIsinsAsync([$"IA{marker}", $"IC{marker}", "NOPE000000"]);

        found.Select(e => e.Secid).Should().BeEquivalentTo([$"A{marker}", $"C{marker}"]);
        (await repo.GetByIsinsAsync([])).Should().BeEmpty();
    }

    [Fact]
    public async Task BondUniverseRepository_GetRecentHistoryForSecids_FiltersSecids_AndLimitsDistinctDates()
    {
        var marker = Marker();
        var repo = new BondUniverseRepository(Conn);
        var mine = Bond($"M{marker}", $"IM{marker}", 0.02m);
        var other = Bond($"O{marker}", $"IO{marker}", 0.03m);
        await repo.UpsertSnapshotBatchAsync([mine, other]);

        // Три искусственные даты далеко в будущем — они «самые свежие» в общей таблице.
        var dates = new[] { new DateOnly(2099, 1, 1), new DateOnly(2099, 1, 2), new DateOnly(2099, 1, 3) };
        try
        {
            foreach (var date in dates)
            {
                await repo.AppendDailyHistorySnapshotAsync(date, retentionDays: 200_000);
            }

            var history = await repo.GetRecentHistoryForSecidsAsync([mine.Secid], tradingDaysBack: 2);

            history.Should().OnlyContain(h => h.Secid == mine.Secid, "чужие secid не возвращаются");
            history.Select(h => h.SnapshotDate).Should().BeEquivalentTo([dates[2], dates[1]], "2 САМЫХ ПОСЛЕДНИХ даты снимка");
            history.Should().OnlyContain(h => h.ResolvedGspreadFraction == 0.02m);
            (await repo.GetRecentHistoryForSecidsAsync([], 5)).Should().BeEmpty();
        }
        finally
        {
            await using var conn = new MySqlConnection(Conn);
            await conn.ExecuteAsync("DELETE FROM bond_universe_history WHERE snapshot_date >= '2099-01-01'");
        }
    }

    // ─── Сборка входов + движок на реальной БД ────────────────────────────────────────────

    [Fact]
    public async Task Builder_And_Engine_ProduceSpreadWideningAndIssuerDefault_FromRealData()
    {
        var marker = Marker();
        var isin = $"RU{marker}000000"[..12];
        var secid = $"E{marker}";
        var repo = new BondUniverseRepository(Conn);
        var today = BusinessClock.MoscowToday();

        // Сегодня спред 0.0300; в истории (12 прошлых дней) был 0.0200 → расширение +100 б.п. > 30 б.п.
        await repo.UpsertSnapshotBatchAsync([Bond(secid, isin, gspread: 0.0200m, price: 100m)]);
        var emitentId = ReliabilitySeed.NewEmitentId();
        await ReliabilitySeed.AttachIssuerAsync(Conn, secid, emitentId, null);
        await ReliabilitySeed.SeedCardAsync(Conn, secid, isin, emitentId, hasDefault: false, hasTechnicalDefault: true);

        var historyDates = Enumerable.Range(0, 12).Select(i => new DateOnly(2098, 6, 1).AddDays(i)).ToList();
        try
        {
            foreach (var date in historyDates)
            {
                await repo.AppendDailyHistorySnapshotAsync(date, retentionDays: 200_000);
            }

            // Банк «сегодня» = 0.0300 (после записи истории со старым значением).
            await repo.UpsertSnapshotBatchAsync([Bond(secid, isin, gspread: 0.0300m, price: 100m)]);

            // Даты 2098 — «будущие» относительно asOf: чтобы проверить срез < asOf, берём asOf после них.
            var builder = new PositionMarketContextBuilder(repo, new SecurityDetailsRepository(Conn));
            var holding = new PortfolioHolding
            {
                PositionId = 1, InstrumentId = 77, Quantity = 5m, MarketValueRub = 5000m, Isin = isin, Issuer = "Эмитент Е", Name = "Бумага Е",
                CouponType = CouponType.Fixed, MaturityDate = today.AddYears(3), HorizonDate = today.AddYears(3),
                IsCalculatedToOffer = false, IsFloater = false, IsIndexed = false, IsEstimated = false, DataIncomplete = false,
            };

            // asOf далеко в будущем → все 2098-е даты считаются «прошлыми торговыми днями» (дата таблицы глобальна: в ней
            // могут быть и 2099-е строки других тестов, поэтому берём asOf = 2200 и проверяем только свои secid).
            var inputs = await builder.BuildAsync([holding], new DateOnly(2200, 1, 1));

            var market = inputs.Market.Should().ContainSingle().Subject;
            market.GSpreadFraction.Should().Be(0.0300m);
            market.GSpreadHistory.Should().NotBeEmpty();
            inputs.IssuerDefaults.Should().ContainSingle(d => d.EmitentId == emitentId && d.HasTechnicalDefault && !d.HasDefault);

            var signals = SignalsEngine.Evaluate(new SignalEvaluationInput
            {
                AccountId = 1,
                AsOf = today,
                Positions = [new SignalPositionContext { PositionId = 1, InstrumentId = 77, Issuer = "Эмитент Е", Name = "Бумага Е", MaturityDate = today.AddYears(3), Quantity = 5m }],
                MarketContexts = [market with { GSpreadHistory = Enumerable.Repeat(0.0200m, 12).ToList(), PriceHistory = Enumerable.Repeat(100m, 12).ToList() }],
                IssuerDefaults = inputs.IssuerDefaults,
            });

            signals.Select(s => s.Type).Should().BeEquivalentTo([SignalType.SpreadWidening, SignalType.IssuerDefaultEvent]);
            signals.Single(s => s.Type == SignalType.IssuerDefaultEvent).Severity.Should().Be(SignalSeverity.Critical);
        }
        finally
        {
            await using var conn = new MySqlConnection(Conn);
            await conn.ExecuteAsync("DELETE FROM bond_universe_history WHERE snapshot_date >= '2098-06-01'");
        }
    }

    // ─── Пороги в настройках ──────────────────────────────────────────────────────────────

    private async Task<HttpClient> AuthorizedClientAsync()
    {
        var telegramId = Random.Shared.NextInt64(1, long.MaxValue);
        var userId = await new UserRepository(Conn).CreateAsync(new User { TelegramId = telegramId });
        await new AccountRepository(Conn).CreateAsync(new Account { UserId = userId, Name = "Счёт" });
        var client = _factory.CreateClient();
        Mvv42TestAuth.Authorize(client, telegramId);
        return client;
    }

    [Fact]
    public async Task Settings_Default_ExposeEngineDefaultsAsFractions()
    {
        var client = await AuthorizedClientAsync();

        var body = await (await client.GetAsync("/api/settings")).Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("spreadWideningThresholdFraction").GetDecimal().Should().Be(0.0030m, "ДОЛЯ: 30 б.п. — раннее предупреждение, ступень ниже SpreadShock скоркарты (50 б.п.)");
        body.GetProperty("priceDrawdownThresholdFraction").GetDecimal().Should().Be(0.05m, "ДОЛЯ: 5%");
    }

    [Fact]
    public async Task Settings_PutThresholds_PersistAndOtherPutsDoNotReset()
    {
        var client = await AuthorizedClientAsync();

        var put = await client.PutAsJsonAsync("/api/settings", new { spreadWideningThresholdFraction = 0.0080m, priceDrawdownThresholdFraction = 0.08m });
        put.StatusCode.Should().Be(HttpStatusCode.OK);
        await client.PutAsJsonAsync("/api/settings", new { upcomingEventDaysThreshold = 20 });

        var body = await (await client.GetAsync("/api/settings")).Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("spreadWideningThresholdFraction").GetDecimal().Should().Be(0.0080m);
        body.GetProperty("priceDrawdownThresholdFraction").GetDecimal().Should().Be(0.08m);
    }

    [Theory]
    [InlineData(0.0, 0.05)]
    [InlineData(-0.001, 0.05)]
    [InlineData(0.06, 0.05)]
    [InlineData(0.005, 0.0)]
    [InlineData(0.005, 0.6)]
    public async Task Settings_PutInvalidThresholds_Returns422(double spread, double drawdown)
    {
        var client = await AuthorizedClientAsync();

        var response = await client.PutAsJsonAsync("/api/settings", new { spreadWideningThresholdFraction = spread, priceDrawdownThresholdFraction = drawdown });

        response.StatusCode.Should().Be((HttpStatusCode)422);
    }
}

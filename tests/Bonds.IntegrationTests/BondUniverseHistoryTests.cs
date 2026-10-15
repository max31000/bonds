using Bonds.Core.Models;
using Bonds.Infrastructure.Repositories;
using Bonds.IntegrationTests.Infrastructure;
using Dapper;
using FluentAssertions;
using MySqlConnector;
using Xunit;

namespace Bonds.IntegrationTests;

/// <summary>
/// Задача 42 часть B.5 — дневной срез <c>bond_universe_history</c> содержит bid/offer/list_level и
/// эффективный G-спред (биржевой с fallback на приближённый). Общий контейнер: срез пишется на
/// искусственную дату далеко в прошлом (вне окна «последних 5 торговых дней» других тестов) и
/// удаляется в finally; retention не срабатывает (cutoff уходит в глубокое прошлое).
/// </summary>
[Collection("Integration")]
public class BondUniverseHistoryTests
{
    private readonly TestWebApplicationFactory _factory;

    public BondUniverseHistoryTests(TestWebApplicationFactory factory) => _factory = factory;

    private static BondUniverseEntry Entry(string secid) => new()
    {
        Secid = secid,
        Isin = $"ISH{secid}",
        ShortName = secid,
        FaceValue = 1000m,
        Sector = "Корпоративные",
        YieldFraction = 0.15m,
        DurationYears = 2m,
        PricePercent = 99.5m,
        TurnoverRub = 1_000_000m,
        BidPercent = 99.1m,
        OfferPercent = 99.9m,
        ListLevel = 2,
        NumTrades = 10,
        MaturityDate = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(3)),
        UpdatedAt = DateTime.UtcNow,
    };

    private sealed class HistoryRow
    {
        public decimal? Bid { get; set; }
        public decimal? Offer { get; set; }
        public int? ListLevel { get; set; }
        public decimal? GspreadEffectiveFraction { get; set; }
        public decimal? GspreadApproxFraction { get; set; }
    }

    [Fact]
    public async Task AppendDailyHistorySnapshot_StoresBidOfferListLevelAndEffectiveGspread()
    {
        var repo = new BondUniverseRepository(_factory.Database.ConnectionString);
        var marker = Guid.NewGuid().ToString("N")[..6];

        var official = Entry($"HO{marker}");
        official.GspreadOfficialFraction = 0.0035m;
        official.GspreadApproxFraction = 0.0100m;
        var approxOnly = Entry($"HA{marker}");
        approxOnly.GspreadApproxFraction = 0.0100m;
        var noSpread = Entry($"HN{marker}");
        await repo.UpsertSnapshotBatchAsync([official, approxOnly, noSpread]);

        var snapshotDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-3000);
        await using var conn = new MySqlConnection(_factory.Database.ConnectionString);
        try
        {
            await repo.AppendDailyHistorySnapshotAsync(snapshotDate, retentionDays: 100_000);

            async Task<HistoryRow> Load(string secid) => await conn.QuerySingleAsync<HistoryRow>(
                @"SELECT bid AS Bid, offer AS Offer, list_level AS ListLevel,
                         gspread_effective_fraction AS GspreadEffectiveFraction,
                         gspread_approx_fraction AS GspreadApproxFraction
                  FROM bond_universe_history WHERE snapshot_date = @Date AND secid = @Secid",
                new { Date = snapshotDate.ToDateTime(TimeOnly.MinValue), Secid = secid });

            var o = await Load(official.Secid);
            o.Bid.Should().Be(99.1m);
            o.Offer.Should().Be(99.9m);
            o.ListLevel.Should().Be(2);
            o.GspreadEffectiveFraction.Should().Be(0.0035m, "биржевой спред приоритетнее приближённого");
            o.GspreadApproxFraction.Should().Be(0.0100m, "приближённый сохраняется как есть");

            (await Load(approxOnly.Secid)).GspreadEffectiveFraction.Should().Be(0.0100m, "нет биржевого — fallback на приближённый");
            (await Load(noSpread.Secid)).GspreadEffectiveFraction.Should().BeNull();
        }
        finally
        {
            await conn.ExecuteAsync("DELETE FROM bond_universe_history WHERE snapshot_date = @Date",
                new { Date = snapshotDate.ToDateTime(TimeOnly.MinValue) });
        }
    }

    [Fact]
    public async Task GetRecentHistory_ReturnsNewColumns_ForRecentSnapshot()
    {
        var repo = new BondUniverseRepository(_factory.Database.ConnectionString);
        var marker = Guid.NewGuid().ToString("N")[..6];
        var entry = Entry($"HR{marker}");
        entry.GspreadOfficialFraction = 0.0020m;
        await repo.UpsertSnapshotBatchAsync([entry]);

        // Дата «из будущего», чтобы гарантированно попасть в топ-N последних дат независимо от соседей.
        var snapshotDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(3000);
        await using var conn = new MySqlConnection(_factory.Database.ConnectionString);
        try
        {
            await repo.AppendDailyHistorySnapshotAsync(snapshotDate, retentionDays: 100_000);

            var points = await repo.GetRecentHistoryAsync(1);

            var point = points.Single(p => p.Secid == entry.Secid);
            point.SnapshotDate.Should().Be(snapshotDate);
            point.Bid.Should().Be(99.1m);
            point.Offer.Should().Be(99.9m);
            point.ListLevel.Should().Be(2);
            point.GspreadEffectiveFraction.Should().Be(0.0020m);
            point.ResolvedGspreadFraction.Should().Be(0.0020m);
        }
        finally
        {
            await conn.ExecuteAsync("DELETE FROM bond_universe_history WHERE snapshot_date = @Date",
                new { Date = snapshotDate.ToDateTime(TimeOnly.MinValue) });
        }
    }
}

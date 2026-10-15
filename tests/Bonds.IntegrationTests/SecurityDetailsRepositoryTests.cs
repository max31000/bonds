using Bonds.Core.Models;
using Bonds.Infrastructure.Repositories;
using Bonds.IntegrationTests.Infrastructure;
using Dapper;
using FluentAssertions;
using MySqlConnector;
using Xunit;

namespace Bonds.IntegrationTests;

/// <summary>
/// Задача 43: репозиторий кэша карточек бумаг (<c>security_details</c>) и эмитенты в <c>bond_universe</c>.
/// Контейнер общий для коллекции — сущности маркируются Guid-суффиксом, тесты не полагаются на пустую БД
/// (приоритеты проверяются ОТНОСИТЕЛЬНЫМ порядком собственных SECID в полной очереди).
/// </summary>
[Collection("Integration")]
public class SecurityDetailsRepositoryTests
{
    private readonly TestWebApplicationFactory _factory;

    public SecurityDetailsRepositoryTests(TestWebApplicationFactory factory) => _factory = factory;

    private string Conn => _factory.Database.ConnectionString;

    private static string Marker() => Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private static SecurityDetails Card(string secid, int? emitentId = null, bool? hasDefault = false,
        bool? hasTechDefault = false, DateTime? fetchedAt = null) => new()
    {
        Secid = secid,
        Isin = $"RU{secid}"[..12],
        EmitentId = emitentId,
        HasDefault = hasDefault,
        HasTechnicalDefault = hasTechDefault,
        IsQualifiedInvestors = false,
        CouponBenchmark = "RUONIA",
        CouponBenchmarkSpreadFraction = 0.019m,
        EarlyRepayment = null,
        FetchedAt = fetchedAt ?? DateTime.UtcNow,
    };

    private static BondUniverseEntry Bank(string secid, string? isin = null) => new()
    {
        Secid = secid,
        Isin = isin ?? $"BK{secid}"[..12],
        ShortName = secid,
        FaceValue = 1000m,
        UpdatedAt = DateTime.UtcNow,
    };

    private int IdCounter = 0;

    private int NewEmitentId() => Random.Shared.Next(2_000_000, 2_000_000_000) + IdCounter++;

    // ─── upsert ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpsertBatch_IsIdempotent_UpdatesRowInsteadOfDuplicating_AndRoundTripsAllFields()
    {
        var repo = new SecurityDetailsRepository(Conn);
        var secid = $"UP{Marker()}";
        var first = Card(secid, emitentId: 4242, hasDefault: true, hasTechDefault: null, fetchedAt: DateTime.UtcNow.AddDays(-1));
        first.EarlyRepayment = true;

        await repo.UpsertBatchAsync([first]);
        await repo.UpsertBatchAsync([first]); // повтор — не дубль

        var second = Card(secid, emitentId: 4242, hasDefault: false, hasTechDefault: false);
        await repo.UpsertBatchAsync([second]);

        await using var conn = new MySqlConnection(Conn);
        (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM security_details WHERE secid = @Secid", new { Secid = secid }))
            .Should().Be(1);

        var loaded = (await repo.GetBySecidsAsync([secid])).Single();
        loaded.HasDefault.Should().BeFalse("второй upsert перезаписал флаг");
        loaded.HasTechnicalDefault.Should().BeFalse();
        loaded.EmitentId.Should().Be(4242);
        loaded.CouponBenchmark.Should().Be("RUONIA");
        loaded.CouponBenchmarkSpreadFraction.Should().Be(0.019m);
        loaded.IsQualifiedInvestors.Should().BeFalse();
        loaded.EarlyRepayment.Should().BeNull("второй upsert передал неизвестное значение — null, а не остаток прежнего true");
        loaded.FetchedAt.Should().BeCloseTo(second.FetchedAt, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task UpsertBatch_NullFlagsStayNull_NotCoercedToFalse()
    {
        var repo = new SecurityDetailsRepository(Conn);
        var secid = $"NL{Marker()}";
        var card = Card(secid, hasDefault: null, hasTechDefault: null);
        card.IsQualifiedInvestors = null;
        card.CouponBenchmark = null;
        card.CouponBenchmarkSpreadFraction = null;

        await repo.UpsertBatchAsync([card]);

        var loaded = (await repo.GetBySecidsAsync([secid])).Single();
        loaded.HasDefault.Should().BeNull();
        loaded.HasTechnicalDefault.Should().BeNull();
        loaded.IsQualifiedInvestors.Should().BeNull();
        loaded.CouponBenchmark.Should().BeNull();
        loaded.CouponBenchmarkSpreadFraction.Should().BeNull();
    }

    [Fact]
    public async Task UpsertBatch_LargeBatchAcrossChunks_AndDuplicateSecidInBatch_Works()
    {
        var repo = new SecurityDetailsRepository(Conn);
        var marker = Marker();
        var cards = Enumerable.Range(0, 450).Select(i => Card($"BT{marker}{i:D4}")).ToList();
        cards.Add(Card($"BT{marker}0000", hasDefault: true)); // дубль SECID в одном батче — последний выигрывает

        await repo.UpsertBatchAsync(cards);

        var loaded = await repo.GetBySecidsAsync(cards.Select(c => c.Secid).Distinct().ToList());
        loaded.Should().HaveCount(450);
        loaded.Single(c => c.Secid == $"BT{marker}0000").HasDefault.Should().BeTrue();
    }

    // ─── очередь обогащения ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EnrichmentQueue_PortfolioAndWatchlistFirst_ThenBankWithoutCard_ThenStale_FreshExcluded()
    {
        var detailsRepo = new SecurityDetailsRepository(Conn);
        var universeRepo = new BondUniverseRepository(Conn);
        var m = Marker();

        // Пользователь, счёт, две бумаги портфеля (одна в банке, другая — нет), одна бумага watchlist.
        var userId = await new UserRepository(Conn).CreateAsync(new User { TelegramId = Random.Shared.NextInt64(1, long.MaxValue), Username = "q" });
        var accountId = await new AccountRepository(Conn).CreateAsync(new Account { UserId = userId, Name = "Q" });
        var instrumentRepo = new InstrumentRepository(Conn);

        async Task<ulong> Instrument(string secid, string isin) => await instrumentRepo.UpsertAsync(new Instrument
        {
            Isin = isin, Secid = secid, FaceValue = 1000m, Currency = "RUB",
            CouponType = CouponType.Fixed, MaturityDate = new DateOnly(2031, 1, 1),
        });

        var portfolioInBank = $"PI{m}";
        var portfolioNotInBank = $"PN{m}";
        var watchSecid = $"WL{m}";
        var bankOnly = $"BO{m}";
        var bankStale = $"BS{m}";
        var bankFresh = $"BF{m}";
        var portfolioFresh = $"PF{m}";
        var closedPosition = $"CP{m}";

        var positionRepo = new PositionRepository(Conn);
        foreach (var (secid, qty) in new[] { (portfolioInBank, 5m), (portfolioNotInBank, 3m), (portfolioFresh, 1m), (closedPosition, 0m) })
        {
            var id = await Instrument(secid, $"IS{secid}"[..12]);
            await positionRepo.UpsertAsync(new Position { AccountId = accountId, InstrumentId = id, Quantity = qty, AvgPurchasePrice = 900m });
        }

        var watchIsin = $"WIL{m}0";
        await Instrument(watchSecid, watchIsin);
        await new WatchlistItemRepository(Conn).CreateAsync(new WatchlistItem { UserId = userId, Isin = watchIsin });

        await universeRepo.UpsertSnapshotBatchAsync(
        [
            Bank(portfolioInBank, $"IS{portfolioInBank}"[..12]), Bank(watchSecid, watchIsin), Bank(bankOnly), Bank(bankStale),
            Bank(bankFresh), Bank(portfolioFresh, $"IS{portfolioFresh}"[..12]), Bank(closedPosition, $"IS{closedPosition}"[..12]),
        ]);

        var staleBefore = DateTime.UtcNow.AddDays(-30);
        await detailsRepo.UpsertBatchAsync(
        [
            Card(bankStale, fetchedAt: DateTime.UtcNow.AddDays(-45)),
            Card(bankFresh, fetchedAt: DateTime.UtcNow.AddDays(-1)),
            Card(portfolioFresh, fetchedAt: DateTime.UtcNow.AddDays(-2)),
        ]);

        var queue = (await detailsRepo.GetEnrichmentQueueAsync(1_000_000, staleBefore)).ToList();

        int Pos(string secid) => queue.IndexOf(secid);

        Pos(portfolioInBank).Should().BeGreaterThanOrEqualTo(0);
        Pos(portfolioNotInBank).Should().BeGreaterThanOrEqualTo(0, "бумага портфеля попадает в очередь, даже если её нет в банке");
        Pos(watchSecid).Should().BeGreaterThanOrEqualTo(0);
        Pos(bankOnly).Should().BeGreaterThanOrEqualTo(0);
        Pos(bankStale).Should().BeGreaterThanOrEqualTo(0);

        queue.Should().NotContain(bankFresh, "свежая карточка не нуждается в обновлении");
        queue.Should().NotContain(portfolioFresh, "свежая карточка бумаги портфеля тоже не обновляется");

        // Приоритет 1: портфель + watchlist раньше банка без карточки; приоритет 2: банк без карточки раньше протухших.
        new[] { Pos(portfolioInBank), Pos(portfolioNotInBank), Pos(watchSecid) }.Max()
            .Should().BeLessThan(Pos(bankOnly), "портфель и watchlist обрабатываются раньше остального банка");
        Pos(bankOnly).Should().BeLessThan(Pos(bankStale), "бумаги без карточки — раньше записей старше 30 дней");

        // Закрытая позиция (quantity = 0) — обычная бумага банка без карточки, не приоритет портфеля.
        Pos(closedPosition).Should().BeGreaterThan(Pos(portfolioInBank));
        queue.Count(s => s == portfolioInBank).Should().Be(1, "бумага в портфеле и в банке одновременно не дублируется");

        // Лимит обрезает очередь, начиная с самого приоритетного.
        var limited = await detailsRepo.GetEnrichmentQueueAsync(1, staleBefore);
        limited.Should().HaveCount(1);
        (await detailsRepo.GetEnrichmentQueueAsync(0, staleBefore)).Should().BeEmpty();
    }

    [Fact]
    public async Task EnrichmentQueue_StalePortfolioCard_RefreshedBeforeBankWithoutCard()
    {
        var detailsRepo = new SecurityDetailsRepository(Conn);
        var universeRepo = new BondUniverseRepository(Conn);
        var m = Marker();

        var userId = await new UserRepository(Conn).CreateAsync(new User { TelegramId = Random.Shared.NextInt64(1, long.MaxValue), Username = "q2" });
        var accountId = await new AccountRepository(Conn).CreateAsync(new Account { UserId = userId, Name = "Q2" });
        var portfolioSecid = $"SP{m}";
        var instrumentId = await new InstrumentRepository(Conn).UpsertAsync(new Instrument
        {
            Isin = $"IS{portfolioSecid}"[..12], Secid = portfolioSecid, FaceValue = 1000m, Currency = "RUB",
            CouponType = CouponType.Fixed, MaturityDate = new DateOnly(2031, 1, 1),
        });
        await new PositionRepository(Conn).UpsertAsync(new Position { AccountId = accountId, InstrumentId = instrumentId, Quantity = 2m });

        var bankNoCard = $"BN{m}";
        await universeRepo.UpsertSnapshotBatchAsync([Bank(portfolioSecid, $"IS{portfolioSecid}"[..12]), Bank(bankNoCard)]);
        await detailsRepo.UpsertBatchAsync([Card(portfolioSecid, fetchedAt: DateTime.UtcNow.AddDays(-60))]);

        var queue = (await detailsRepo.GetEnrichmentQueueAsync(1_000_000, DateTime.UtcNow.AddDays(-30))).ToList();

        queue.IndexOf(portfolioSecid).Should().BeGreaterThanOrEqualTo(0);
        queue.IndexOf(portfolioSecid).Should().BeLessThan(queue.IndexOf(bankNoCard),
            "протухшая карточка бумаги портфеля приоритетнее обычной бумаги банка без карточки");
    }

    // ─── дефолт-история эмитента ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task EmitentDefaultHistory_TwoIssuesOneDefaulted_AggregateIsTrue_OthersFalseOrUnknown()
    {
        var repo = new SecurityDetailsRepository(Conn);
        var m = Marker();
        var defaulted = NewEmitentId();
        var clean = NewEmitentId();
        var technicalOnly = NewEmitentId();
        var unknownFlags = NewEmitentId();
        var noCards = NewEmitentId();

        await repo.UpsertBatchAsync(
        [
            Card($"D1{m}", defaulted, hasDefault: false, hasTechDefault: false),
            Card($"D2{m}", defaulted, hasDefault: true, hasTechDefault: true),
            Card($"C1{m}", clean, hasDefault: false, hasTechDefault: false),
            Card($"C2{m}", clean, hasDefault: false, hasTechDefault: false),
            Card($"T1{m}", technicalOnly, hasDefault: false, hasTechDefault: true),
            Card($"U1{m}", unknownFlags, hasDefault: null, hasTechDefault: null),
            Card($"U2{m}", unknownFlags, hasDefault: null, hasTechDefault: null),
            Card($"X1{m}", null, hasDefault: true), // без эмитента — ни в какой агрегат не попадает
        ]);

        var result = await repo.GetEmitentDefaultHistoryAsync([defaulted, clean, technicalOnly, unknownFlags, noCards]);

        result[defaulted].HasDefault.Should().BeTrue("у одного из двух выпусков эмитента допущен дефолт");
        result[defaulted].HasTechnicalDefault.Should().BeTrue();
        result[clean].HasDefault.Should().BeFalse();
        result[clean].HasTechnicalDefault.Should().BeFalse();
        result[technicalOnly].HasDefault.Should().BeFalse();
        result[technicalOnly].HasTechnicalDefault.Should().BeTrue();
        result[unknownFlags].HasDefault.Should().BeNull("карточки есть, но флаг ни по одной не отдан — неизвестно, не «чисто»");
        result.Should().NotContainKey(noCards, "по эмитенту без карточек данных нет");
        (await repo.GetEmitentDefaultHistoryAsync([])).Should().BeEmpty();
    }

    [Fact]
    public async Task EmitentDefaultHistory_MixedKnownAndUnknown_UsesKnownValues()
    {
        var repo = new SecurityDetailsRepository(Conn);
        var m = Marker();
        var emitent = NewEmitentId();
        await repo.UpsertBatchAsync(
        [
            Card($"M1{m}", emitent, hasDefault: null, hasTechDefault: null),
            Card($"M2{m}", emitent, hasDefault: false, hasTechDefault: false),
        ]);

        var result = await repo.GetEmitentDefaultHistoryAsync([emitent]);

        result[emitent].HasDefault.Should().BeFalse("MAX игнорирует NULL: известный флаг 0 побеждает неизвестный");
    }

    // ─── эмитенты в банке ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateEmitents_UpdatesOnlyExistingBankRows_SurvivesSnapshotUpsert_NullsAllowed()
    {
        var repo = new BondUniverseRepository(Conn);
        var m = Marker();
        var withEmitent = $"EM{m}";
        var withoutEmitent = $"EN{m}";
        var notInBank = $"EX{m}";
        await repo.UpsertSnapshotBatchAsync([Bank(withEmitent), Bank(withoutEmitent)]);

        var updated = await repo.UpdateEmitentsAsync(
        [
            new UniverseEmitent(withEmitent, 777001, "0010000025", "Эмитент \"Тест\""),
            new UniverseEmitent(withoutEmitent, null, null, null),
            new UniverseEmitent(notInBank, 1, "1", "нет в банке"),
        ]);

        updated.Should().BeGreaterThanOrEqualTo(2);
        (await repo.GetAllAsync()).Should().NotContain(e => e.Secid == notInBank, "UPDATE не создаёт новых строк банка");

        var all = await repo.GetAllAsync();
        var e1 = all.Single(e => e.Secid == withEmitent);
        e1.EmitentId.Should().Be(777001);
        e1.EmitentInn.Should().Be("0010000025", "ведущие нули ИНН сохраняются");
        e1.EmitentTitle.Should().Be("Эмитент \"Тест\"");
        var e2 = all.Single(e => e.Secid == withoutEmitent);
        e2.EmitentId.Should().BeNull();
        e2.EmitentInn.Should().BeNull();

        // Часовой upsert снимка НЕ затирает эмитента.
        await repo.UpsertSnapshotBatchAsync([Bank(withEmitent)]);
        (await repo.GetAllAsync()).Single(e => e.Secid == withEmitent).EmitentTitle.Should().Be("Эмитент \"Тест\"");

        (await repo.CountWithEmitentAsync()).Should().BeGreaterThanOrEqualTo(1);
    }
}

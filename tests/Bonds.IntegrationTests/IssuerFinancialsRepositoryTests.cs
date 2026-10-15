using Bonds.Core.Models;
using Bonds.Infrastructure.Connectors.GirBo;
using Bonds.Infrastructure.Universe;
using Bonds.Infrastructure.Repositories;
using Bonds.IntegrationTests.Infrastructure;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MySqlConnector;
using Xunit;

namespace Bonds.IntegrationTests;

/// <summary>
/// Задача 45: хранилище отчётности эмитентов (<c>issuer_financials</c>/<c>issuer_financials_status</c>).
/// Контейнер общий для коллекции — ИНН уникальны на тест (случайные 10 цифр), проверки очереди — относительные.
/// </summary>
[Collection("Integration")]
public class IssuerFinancialsRepositoryTests
{
    private readonly TestWebApplicationFactory _factory;

    public IssuerFinancialsRepositoryTests(TestWebApplicationFactory factory) => _factory = factory;

    private string Conn => _factory.Database.ConnectionString;

    private static string NewInn() => Random.Shared.NextInt64(1_000_000_000, 9_999_999_999).ToString();

    private static string Marker() => Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private static IssuerFinancials Fin(string inn, int period, string source = IssuerFinancialsSources.GirBo,
        decimal? revenue = 1_000_000m, DateTime? fetchedAt = null) => new()
    {
        Inn = inn, Period = period, Source = source, FetchedAt = fetchedAt ?? DateTime.UtcNow,
        Revenue = revenue, OperatingProfit = 100m, InterestPaid = 50m, NetProfit = 10m, Equity = 500m, TotalAssets = 2_000m,
        CurrentAssets = 900m, CurrentLiabilities = 700m, Cash = 80m, LongDebt = 300m, ShortDebt = 200m,
        RetainedEarnings = 400m, HasAudit = true,
    };

    // ─── upsert ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Upsert_IsIdempotent_UpdatesInsteadOfDuplicating_RoundTripsAllFields()
    {
        var repo = new IssuerFinancialsRepository(Conn);
        var inn = NewInn();
        var first = Fin(inn, 2024);

        await repo.UpsertAsync([first]);
        await repo.UpsertAsync([first]); // повтор — не дубль

        var second = Fin(inn, 2024, revenue: 2_000_000m);
        second.HasAudit = null;
        second.Cash = null;
        await repo.UpsertAsync([second]);

        await using var conn = new MySqlConnection(Conn);
        (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM issuer_financials WHERE inn = @Inn", new { Inn = inn })).Should().Be(1);

        var loaded = (await repo.GetLatestByInnsAsync([inn]))[inn].Single();
        loaded.Revenue.Should().Be(2_000_000m);
        loaded.Cash.Should().BeNull("второй upsert передал неизвестное значение — null, а не остаток прежнего");
        loaded.HasAudit.Should().BeNull();
        loaded.OperatingProfit.Should().Be(100m);
        loaded.InterestPaid.Should().Be(50m);
        loaded.NetProfit.Should().Be(10m);
        loaded.Equity.Should().Be(500m);
        loaded.TotalAssets.Should().Be(2_000m);
        loaded.CurrentAssets.Should().Be(900m);
        loaded.CurrentLiabilities.Should().Be(700m);
        loaded.LongDebt.Should().Be(300m);
        loaded.ShortDebt.Should().Be(200m);
        loaded.RetainedEarnings.Should().Be(400m);
        loaded.Source.Should().Be(IssuerFinancialsSources.GirBo);
        loaded.FetchedAt.Should().BeCloseTo(second.FetchedAt, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Upsert_LargeRubleValues_KeepPrecision()
    {
        var repo = new IssuerFinancialsRepository(Conn);
        var inn = NewInn();
        var row = Fin(inn, 2024, revenue: 495_877_663_000m); // 495,9 млрд ₽ — сходится с DECIMAL(20,2)

        await repo.UpsertAsync([row]);

        (await repo.GetLatestByInnsAsync([inn]))[inn].Single().Revenue.Should().Be(495_877_663_000m);
    }

    [Fact]
    public async Task Upsert_ManualRowsAreNotOverwrittenByGirBoLoad_BecauseSourceIsPartOfKey()
    {
        var repo = new IssuerFinancialsRepository(Conn);
        var inn = NewInn();
        await repo.UpsertAsync([Fin(inn, 2023, IssuerFinancialsSources.Manual, revenue: 777m)]);

        await repo.UpsertAsync([Fin(inn, 2023, IssuerFinancialsSources.GirBo, revenue: 111m)]);
        await repo.UpsertAsync([Fin(inn, 2023, IssuerFinancialsSources.GirBo, revenue: 222m)]); // повторная загрузка girbo

        await using var conn = new MySqlConnection(Conn);
        var rows = (await conn.QueryAsync<(string Source, decimal Revenue)>(
            "SELECT source AS Source, revenue AS Revenue FROM issuer_financials WHERE inn = @Inn ORDER BY source", new { Inn = inn })).ToList();
        rows.Should().Equal(new[] { (IssuerFinancialsSources.GirBo, 222m), (IssuerFinancialsSources.Manual, 777m) });
    }

    [Fact]
    public async Task Upsert_Batch_DuplicateKeyInBatch_LastWins()
    {
        var repo = new IssuerFinancialsRepository(Conn);
        var inn = NewInn();

        await repo.UpsertAsync([Fin(inn, 2022, revenue: 1m), Fin(inn, 2022, revenue: 2m), Fin(inn, 2021, revenue: 3m)]);

        var loaded = (await repo.GetLatestByInnsAsync([inn]))[inn];
        loaded.Select(r => (r.Period, r.Revenue)).Should().Equal((2022, 2m), (2021, 3m));
    }

    // ─── GetLatestByInnsAsync ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetLatestByInns_ReturnsThreeLatestYearsPerInn_DescendingByPeriod_InOneCall()
    {
        var repo = new IssuerFinancialsRepository(Conn);
        var a = NewInn();
        var b = NewInn();
        var c = NewInn();
        await repo.UpsertAsync(
        [
            Fin(a, 2021), Fin(a, 2022), Fin(a, 2023), Fin(a, 2024), Fin(a, 2020),
            Fin(b, 2024), Fin(b, 2023),
        ]);

        var result = await repo.GetLatestByInnsAsync([a, b, c]);

        result[a].Select(r => r.Period).Should().Equal(2024, 2023, 2022);
        result[b].Select(r => r.Period).Should().Equal(2024, 2023);
        result.Should().NotContainKey(c, "по ИНН без данных ключа нет");
        (await repo.GetLatestByInnsAsync([])).Should().BeEmpty();
        (await repo.GetLatestByInnsAsync([a], yearsPerInn: 1))[a].Should().ContainSingle().Which.Period.Should().Be(2024);
    }

    [Fact]
    public async Task GetLatestByInns_ManualWinsOverGirBoForSamePeriod_AndCountsAsOneYear()
    {
        var repo = new IssuerFinancialsRepository(Conn);
        var inn = NewInn();
        await repo.UpsertAsync(
        [
            Fin(inn, 2024, IssuerFinancialsSources.GirBo, revenue: 100m),
            Fin(inn, 2024, IssuerFinancialsSources.Manual, revenue: 999m),
            Fin(inn, 2023, IssuerFinancialsSources.GirBo, revenue: 50m),
            Fin(inn, 2022, IssuerFinancialsSources.GirBo, revenue: 40m),
            Fin(inn, 2021, IssuerFinancialsSources.GirBo, revenue: 30m),
        ]);

        var rows = (await repo.GetLatestByInnsAsync([inn]))[inn];

        rows.Select(r => r.Period).Should().Equal(2024, 2023, 2022);
        rows[0].Source.Should().Be(IssuerFinancialsSources.Manual);
        rows[0].Revenue.Should().Be(999m);
    }

    // ─── статусы ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpsertStatus_IsIdempotent_AndOverwrites()
    {
        var repo = new IssuerFinancialsRepository(Conn);
        var inn = NewInn();
        var t1 = DateTime.UtcNow.AddDays(-3);
        var t2 = DateTime.UtcNow;

        await repo.UpsertStatusAsync(new IssuerFinancialsStatus(inn, IssuerFinancialsStatuses.Hidden, t1));
        await repo.UpsertStatusAsync(new IssuerFinancialsStatus(inn, IssuerFinancialsStatuses.Ok, t2));

        var status = (await repo.GetStatusesAsync([inn, NewInn()]))[inn];
        status.Status.Should().Be(IssuerFinancialsStatuses.Ok);
        status.CheckedAt.Should().BeCloseTo(t2, TimeSpan.FromSeconds(1));
        (await repo.GetStatusesAsync([])).Should().BeEmpty();
    }

    // ─── очередь обновления ───────────────────────────────────────────────────────────────────

    private async Task<(string Secid, string Isin)> SeedBond(string emitentInn, string marker, string tag)
    {
        var secid = $"{tag}{marker}";
        var isin = $"RU{tag}{marker}"[..12];
        await new BondUniverseRepository(Conn).UpsertSnapshotBatchAsync(
        [
            new BondUniverseEntry { Secid = secid, Isin = isin, ShortName = secid, FaceValue = 1000m, UpdatedAt = DateTime.UtcNow },
        ]);
        await new BondUniverseRepository(Conn).UpdateEmitentsAsync([new UniverseEmitent(secid, Random.Shared.Next(1, int.MaxValue), emitentInn, "Эмитент " + tag)]);
        return (secid, isin);
    }

    [Fact]
    public async Task RefreshQueue_PortfolioAndWatchlistIssuersOnly_ValidInnOnly_ClosedPositionsAndBankExcluded()
    {
        var repo = new IssuerFinancialsRepository(Conn);
        var m = Marker();
        var portfolioInn = NewInn();
        var watchlistInn = NewInn();
        var closedInn = NewInn();
        var bankOnlyInn = NewInn();
        var sharedInn = NewInn(); // у эмитента два выпуска в портфеле — ИНН один раз
        var ipInn = Random.Shared.NextInt64(100_000_000_000, 999_999_999_999).ToString(); // 12 цифр (ИП)
        const string garbageInn = "ABC-123";

        var userId = await new UserRepository(Conn).CreateAsync(new User { TelegramId = Random.Shared.NextInt64(1, long.MaxValue), Username = "if" });
        var accountId = await new AccountRepository(Conn).CreateAsync(new Account { UserId = userId, Name = "IF" });
        var instrumentRepo = new InstrumentRepository(Conn);
        var positionRepo = new PositionRepository(Conn);

        async Task<ulong> Instrument(string secid, string isin) => await instrumentRepo.UpsertAsync(new Instrument
        {
            Isin = isin, Secid = secid, FaceValue = 1000m, Currency = "RUB",
            CouponType = CouponType.Fixed, MaturityDate = new DateOnly(2031, 1, 1),
        });

        foreach (var (inn, tag, qty) in new[]
                 {
                     (portfolioInn, "PA", 5m), (sharedInn, "PB", 2m), (sharedInn, "PC", 3m), (closedInn, "PD", 0m),
                     (ipInn, "PE", 1m), (garbageInn, "PF", 1m),
                 })
        {
            var (secid, isin) = await SeedBond(inn, m, tag);
            var id = await Instrument(secid, isin);
            await positionRepo.UpsertAsync(new Position { AccountId = accountId, InstrumentId = id, Quantity = qty, AvgPurchasePrice = 900m });
        }

        var (watchSecid, watchIsin) = await SeedBond(watchlistInn, m, "WA");
        await Instrument(watchSecid, watchIsin);
        await new WatchlistItemRepository(Conn).CreateAsync(new WatchlistItem { UserId = userId, Isin = watchIsin });
        await SeedBond(bankOnlyInn, m, "BA");

        var queue = (await repo.GetRefreshQueueAsync(1_000_000, DateTime.UtcNow.AddDays(-30), DateTime.UtcNow.AddHours(-20))).ToList();

        queue.Should().Contain([portfolioInn, watchlistInn, sharedInn, ipInn]);
        queue.Count(i => i == sharedInn).Should().Be(1, "ИНН эмитента с несколькими выпусками не дублируется");
        queue.Should().NotContain(closedInn, "закрытая позиция (quantity = 0) — не эмитент портфеля");
        queue.Should().NotContain(bankOnlyInn, "эмитенты остального банка не скачиваем — только портфель/watchlist");
        queue.Should().NotContain(garbageInn, "ИНН не из 10/12 цифр во внешний сервис не уходит");

        (await repo.GetRefreshQueueAsync(1, DateTime.UtcNow.AddDays(-30), DateTime.UtcNow.AddHours(-20))).Should().HaveCount(1);
        (await repo.GetRefreshQueueAsync(0, DateTime.UtcNow.AddDays(-30), DateTime.UtcNow.AddHours(-20))).Should().BeEmpty();
    }

    [Fact]
    public async Task RefreshQueue_RespectsThirtyDayCache_ErrorRetriesEarlier_NewInnsFirst()
    {
        var repo = new IssuerFinancialsRepository(Conn);
        var m = Marker();
        var freshOk = NewInn();
        var staleOk = NewInn();
        var freshNotFound = NewInn();
        var staleHidden = NewInn();
        var freshError = NewInn();
        var staleError = NewInn();
        var neverChecked = NewInn();

        var userId = await new UserRepository(Conn).CreateAsync(new User { TelegramId = Random.Shared.NextInt64(1, long.MaxValue), Username = "ic" });
        var accountId = await new AccountRepository(Conn).CreateAsync(new Account { UserId = userId, Name = "IC" });
        var tag = 0;
        foreach (var inn in new[] { freshOk, staleOk, freshNotFound, staleHidden, freshError, staleError, neverChecked })
        {
            var (secid, isin) = await SeedBond(inn, m, $"C{tag++}");
            var id = await new InstrumentRepository(Conn).UpsertAsync(new Instrument
            {
                Isin = isin, Secid = secid, FaceValue = 1000m, Currency = "RUB",
                CouponType = CouponType.Fixed, MaturityDate = new DateOnly(2031, 1, 1),
            });
            await new PositionRepository(Conn).UpsertAsync(new Position { AccountId = accountId, InstrumentId = id, Quantity = 1m });
        }

        var now = DateTime.UtcNow;
        await repo.UpsertStatusAsync(new IssuerFinancialsStatus(freshOk, IssuerFinancialsStatuses.Ok, now.AddDays(-5)));
        await repo.UpsertStatusAsync(new IssuerFinancialsStatus(staleOk, IssuerFinancialsStatuses.Ok, now.AddDays(-31)));
        await repo.UpsertStatusAsync(new IssuerFinancialsStatus(freshNotFound, IssuerFinancialsStatuses.NotFound, now.AddDays(-29)));
        await repo.UpsertStatusAsync(new IssuerFinancialsStatus(staleHidden, IssuerFinancialsStatuses.Hidden, now.AddDays(-40)));
        await repo.UpsertStatusAsync(new IssuerFinancialsStatus(freshError, IssuerFinancialsStatuses.Error, now.AddHours(-2)));
        await repo.UpsertStatusAsync(new IssuerFinancialsStatus(staleError, IssuerFinancialsStatuses.Error, now.AddDays(-2)));

        var queue = (await repo.GetRefreshQueueAsync(1_000_000, now.AddDays(-30), now.AddHours(-20))).ToList();

        queue.Should().Contain([neverChecked, staleOk, staleHidden, staleError]);
        queue.Should().NotContain(freshOk, "проверен 5 дней назад — 30-дневный кэш");
        queue.Should().NotContain(freshNotFound, "not_found 29 дней назад — ещё свежий");
        queue.Should().NotContain(freshError, "ошибка 2 часа назад — повтор не раньше следующих суток");

        queue.IndexOf(neverChecked).Should().BeLessThan(queue.IndexOf(staleOk), "ИНН без статуса — раньше протухших");
        queue.IndexOf(staleHidden).Should().BeLessThan(queue.IndexOf(staleOk), "среди протухших — самые давно проверенные первыми");
    }
    // ─── сквозной сценарий: тик → записи в рублях → повторный тик не перекачивает ───────────────

    [Fact]
    public async Task Enricher_FirstTickWritesRubleRows_SecondTickDoesNotRefetchFreshInns()
    {
        var repo = new IssuerFinancialsRepository(Conn);
        var m = Marker();
        var openInn = NewInn();
        var bankInn = NewInn();
        var hiddenInn = NewInn();

        var userId = await new UserRepository(Conn).CreateAsync(new User { TelegramId = Random.Shared.NextInt64(1, long.MaxValue), Username = "ie" });
        var accountId = await new AccountRepository(Conn).CreateAsync(new Account { UserId = userId, Name = "IE" });
        var tag = 0;
        foreach (var inn in new[] { openInn, bankInn, hiddenInn })
        {
            var (secid, isin) = await SeedBond(inn, m, $"E{tag++}");
            var id = await new InstrumentRepository(Conn).UpsertAsync(new Instrument
            {
                Isin = isin, Secid = secid, FaceValue = 1000m, Currency = "RUB",
                CouponType = CouponType.Fixed, MaturityDate = new DateOnly(2031, 1, 1),
            });
            await new PositionRepository(Conn).UpsertAsync(new Position { AccountId = accountId, InstrumentId = id, Quantity = 1m });
        }

        var now = DateTime.UtcNow;
        var girBo = new Mock<IGirBoClient>();
        girBo.Setup(g => g.FindOrganizationAsync(openInn, It.IsAny<CancellationToken>())).ReturnsAsync(new GirBoOrganization(1, openInn, "Открытый", false));
        girBo.Setup(g => g.GetFilingsAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync([new GirBoFiling(11, 2024, false)]);
        girBo.Setup(g => g.GetAnnualFinancialsAsync(11, openInn, 2024, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([Fin(openInn, 2024), Fin(openInn, 2023), Fin(openInn, 2022)]);
        girBo.Setup(g => g.FindOrganizationAsync(bankInn, It.IsAny<CancellationToken>())).ReturnsAsync(new GirBoOrganization(2, bankInn, "Банк", true));
        girBo.Setup(g => g.FindOrganizationAsync(hiddenInn, It.IsAny<CancellationToken>())).ReturnsAsync(new GirBoOrganization(3, hiddenInn, "Скрытый", false));
        girBo.Setup(g => g.GetFilingsAsync(3, It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var enricher = new IssuerFinancialsEnricher(girBo.Object, repo, NullLogger<IssuerFinancialsEnricher>.Instance);
        var queueBefore = await repo.GetRefreshQueueAsync(1_000_000, now.AddDays(-30), now.AddHours(-20));
        queueBefore.Should().Contain([openInn, bankInn, hiddenInn]);

        var first = await enricher.EnrichBatchAsync(1_000_000, TimeSpan.FromDays(30), TimeSpan.FromHours(20), TimeSpan.Zero, now);

        first.Completed.Should().BeTrue();
        var financials = await repo.GetLatestByInnsAsync([openInn, bankInn, hiddenInn]);
        financials[openInn].Select(r => r.Period).Should().Equal(2024, 2023, 2022);
        financials.Should().NotContainKey(bankInn).And.NotContainKey(hiddenInn);
        var statuses = await repo.GetStatusesAsync([openInn, bankInn, hiddenInn]);
        statuses[openInn].Status.Should().Be(IssuerFinancialsStatuses.Ok);
        statuses[bankInn].Status.Should().Be(IssuerFinancialsStatuses.Cb);
        statuses[hiddenInn].Status.Should().Be(IssuerFinancialsStatuses.Hidden);

        // Второй тик: свежепроверенные ИНН (30-дневный кэш) в очередь не попадают и во внешний сервис не ходят.
        var queueAfter = await repo.GetRefreshQueueAsync(1_000_000, now.AddDays(-30), now.AddHours(-20));
        queueAfter.Should().NotContain([openInn, bankInn, hiddenInn]);
        girBo.Invocations.Clear();
        await enricher.EnrichBatchAsync(1_000_000, TimeSpan.FromDays(30), TimeSpan.FromHours(20), TimeSpan.Zero, now.AddMinutes(5));
        girBo.Verify(g => g.FindOrganizationAsync(openInn, It.IsAny<CancellationToken>()), Times.Never);
        girBo.Verify(g => g.FindOrganizationAsync(bankInn, It.IsAny<CancellationToken>()), Times.Never);
        girBo.Verify(g => g.FindOrganizationAsync(hiddenInn, It.IsAny<CancellationToken>()), Times.Never);
    }
}

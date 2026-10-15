using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Bonds.Infrastructure.Connectors.Moex;
using Bonds.Infrastructure.Universe;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Bonds.Tests.Universe;

/// <summary>
/// Задача 43: суточные шаги обогащения банка — эмитенты (<see cref="UniverseEmitentEnricher"/>), карточки
/// бумаг (<see cref="SecurityDetailsEnricher"/>) и условие суточного запуска (<see cref="DailyEnrichmentGate"/>).
/// Без сети и БД — Moq на клиент ISS и репозитории; даты относительно «сейчас».
/// </summary>
public class UniverseEnrichmentTests
{
    // ─── DailyEnrichmentGate ──────────────────────────────────────────────────────────────────

    private static readonly TimeOnly Start = new(9, 50);

    [Fact]
    public void Gate_BeforeStartTime_NotDue()
    {
        DailyEnrichmentGate.IsDue(new DateTime(2026, 10, 15, 9, 49, 0), null, Start).Should().BeFalse();
    }

    [Fact]
    public void Gate_AfterStartTime_NoRunToday_Due()
    {
        var now = new DateTime(2026, 10, 15, 9, 50, 0);
        DailyEnrichmentGate.IsDue(now, null, Start).Should().BeTrue();
        DailyEnrichmentGate.IsDue(now, DateOnly.FromDateTime(now).AddDays(-1), Start).Should().BeTrue("вчерашний запуск не мешает сегодняшнему");
    }

    [Fact]
    public void Gate_AlreadyRanToday_NotDue_EvenLaterInDay()
    {
        var now = new DateTime(2026, 10, 15, 18, 30, 0);
        DailyEnrichmentGate.IsDue(now, DateOnly.FromDateTime(now), Start).Should().BeFalse();
    }

    // ─── UniverseEmitentEnricher ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task EmitentEnricher_MapsBulkRowsBySecid_NullsKept()
    {
        var moex = new Mock<IMoexIssClient>();
        moex.Setup(m => m.GetSecurityEmitentsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
            new MoexSecurityEmitentsResult(
            [
                new MoexSecurityEmitent("RU000A10GCU9", 11355, "АО Селектел", "7810962785"),
                new MoexSecurityEmitent("RU0001245474", null, null, null),
            ], Complete: true));
        IReadOnlyList<UniverseEmitent>? captured = null;
        var repo = new Mock<IBondUniverseRepository>();
        repo.Setup(r => r.UpdateEmitentsAsync(It.IsAny<IReadOnlyList<UniverseEmitent>>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<UniverseEmitent>, CancellationToken>((l, _) => captured = l)
            .ReturnsAsync(1);

        var result = await new UniverseEmitentEnricher(moex.Object, repo.Object, NullLogger<UniverseEmitentEnricher>.Instance).EnrichAsync();

        result.Should().Be(1);
        captured.Should().BeEquivalentTo(new[]
        {
            new UniverseEmitent("RU000A10GCU9", 11355, "7810962785", "АО Селектел"),
            new UniverseEmitent("RU0001245474", null, null, null),
        });
    }

    [Fact]
    public async Task EmitentEnricher_EmptyDirectory_ReturnsNull_DoesNotTouchBank()
    {
        var moex = new Mock<IMoexIssClient>();
        moex.Setup(m => m.GetSecurityEmitentsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MoexSecurityEmitentsResult([], Complete: false));
        var repo = new Mock<IBondUniverseRepository>(MockBehavior.Strict);

        var result = await new UniverseEmitentEnricher(moex.Object, repo.Object, NullLogger<UniverseEmitentEnricher>.Instance).EnrichAsync();

        result.Should().BeNull("пустой справочник = источник недоступен, шаг не считается выполненным");
    }

    [Fact]
    public async Task EmitentEnricher_IncompleteDirectory_AppliesRows_ButReturnsNull()
    {
        // Ревью В1 MINOR-3: обрыв обхода на середине — собранное применяем, но шаг не засчитан (повторится).
        var moex = new Mock<IMoexIssClient>();
        moex.Setup(m => m.GetSecurityEmitentsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
            new MoexSecurityEmitentsResult([new MoexSecurityEmitent("S1", 1, "Э1", "0000000001")], Complete: false));
        var repo = new Mock<IBondUniverseRepository>();
        repo.Setup(r => r.UpdateEmitentsAsync(It.IsAny<IReadOnlyList<UniverseEmitent>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var result = await new UniverseEmitentEnricher(moex.Object, repo.Object, NullLogger<UniverseEmitentEnricher>.Instance).EnrichAsync();

        result.Should().BeNull("неполный справочник не засчитывается выполненным шагом");
        repo.Verify(r => r.UpdateEmitentsAsync(It.IsAny<IReadOnlyList<UniverseEmitent>>(), It.IsAny<CancellationToken>()), Times.Once,
            "частично собранные строки всё равно применяются — это идемпотентно");
    }

    // ─── SecurityDetailsEnricher ──────────────────────────────────────────────────────────────

    private sealed class RepoFake : ISecurityDetailsRepository
    {
        public List<string> Queue { get; set; } = [];
        public List<SecurityDetails> Saved { get; } = [];
        public int? RequestedLimit { get; private set; }
        public DateTime? RequestedStaleBefore { get; private set; }

        public Task UpsertBatchAsync(IReadOnlyList<SecurityDetails> items, CancellationToken ct = default)
        {
            Saved.AddRange(items);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SecurityDetails>> GetBySecidsAsync(IReadOnlyCollection<string> secids, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SecurityDetails>>([]);

        public Task<IReadOnlyList<string>> GetEnrichmentQueueAsync(int limit, DateTime staleBeforeUtc, CancellationToken ct = default)
        {
            RequestedLimit = limit;
            RequestedStaleBefore = staleBeforeUtc;
            return Task.FromResult<IReadOnlyList<string>>(Queue);
        }

        public Task<IReadOnlyDictionary<int, EmitentDefaultHistory>> GetEmitentDefaultHistoryAsync(IReadOnlyCollection<int> emitentIds, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<int, EmitentDefaultHistory>>(new Dictionary<int, EmitentDefaultHistory>());
    }

    private static MoexSecurityDescription Card(string secid, bool? hasDefault = false) => new()
    {
        Secid = secid,
        Isin = secid,
        EmitterId = 712,
        HasDefault = hasDefault,
        HasTechnicalDefault = false,
        IsQualifiedInvestors = false,
        CouponBenchmark = "RUONIA",
        CouponBenchmarkSpreadFraction = 0.019m,
        EarlyRepayment = null,
    };

    [Fact]
    public async Task DetailsEnricher_PassesBatchSizeAndStaleThreshold_MapsCardsToRows()
    {
        var now = DateTime.UtcNow;
        var repo = new RepoFake { Queue = ["A", "B"] };
        var moex = new Mock<IMoexIssClient>();
        moex.Setup(m => m.GetSecurityDescriptionAsync("A", It.IsAny<CancellationToken>())).ReturnsAsync(Card("A", hasDefault: true));
        moex.Setup(m => m.GetSecurityDescriptionAsync("B", It.IsAny<CancellationToken>())).ReturnsAsync(Card("B"));

        var saved = await new SecurityDetailsEnricher(moex.Object, repo, NullLogger<SecurityDetailsEnricher>.Instance)
            .EnrichBatchAsync(200, TimeSpan.FromDays(30), TimeSpan.Zero, now);

        saved.Should().Be(2);
        repo.RequestedLimit.Should().Be(200);
        repo.RequestedStaleBefore.Should().Be(now.AddDays(-30));
        var a = repo.Saved.Single(s => s.Secid == "A");
        a.HasDefault.Should().BeTrue();
        a.EmitentId.Should().Be(712);
        a.CouponBenchmarkSpreadFraction.Should().Be(0.019m);
        a.FetchedAt.Should().Be(now);
    }

    [Fact]
    public async Task DetailsEnricher_MissingCard_WritesNegativeCache_TransportError_SkippedWithoutRecord()
    {
        // Ревью В1 MINOR-1: null-карточка (404/пустая) — негативный кэш со всеми null-флагами;
        // транспортная ошибка — пропуск без записи (бумага вернётся в очередь).
        var now = DateTime.UtcNow;
        var repo = new RepoFake { Queue = ["OK1", "NULL", "BOOM", "OK2"] };
        var moex = new Mock<IMoexIssClient>();
        moex.Setup(m => m.GetSecurityDescriptionAsync("OK1", It.IsAny<CancellationToken>())).ReturnsAsync(Card("OK1"));
        moex.Setup(m => m.GetSecurityDescriptionAsync("NULL", It.IsAny<CancellationToken>())).ReturnsAsync((MoexSecurityDescription?)null);
        moex.Setup(m => m.GetSecurityDescriptionAsync("BOOM", It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException("503"));
        moex.Setup(m => m.GetSecurityDescriptionAsync("OK2", It.IsAny<CancellationToken>())).ReturnsAsync(Card("OK2"));

        var saved = await new SecurityDetailsEnricher(moex.Object, repo, NullLogger<SecurityDetailsEnricher>.Instance)
            .EnrichBatchAsync(200, TimeSpan.FromDays(30), TimeSpan.Zero, now);

        saved.Should().Be(3);
        repo.Saved.Select(s => s.Secid).Should().BeEquivalentTo("OK1", "NULL", "OK2");
        var tombstone = repo.Saved.Single(s => s.Secid == "NULL");
        tombstone.HasDefault.Should().BeNull("null-флаги = «неизвестно», не «нет дефолта»");
        tombstone.EmitentId.Should().BeNull();
        tombstone.FetchedAt.Should().Be(now, "негативный кэш уводит бумагу из головы очереди до протухания");
    }

    [Fact]
    public async Task DetailsEnricher_ManyConsecutiveFailures_AbortsTick_KeepsEarlierProgress()
    {
        var queue = new List<string> { "GOOD" };
        queue.AddRange(Enumerable.Range(0, 30).Select(i => $"DEAD{i}"));
        var repo = new RepoFake { Queue = queue };
        var moex = new Mock<IMoexIssClient>();
        moex.Setup(m => m.GetSecurityDescriptionAsync("GOOD", It.IsAny<CancellationToken>())).ReturnsAsync(Card("GOOD"));
        moex.Setup(m => m.GetSecurityDescriptionAsync(It.Is<string>(s => s.StartsWith("DEAD")), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("timeout"));

        var saved = await new SecurityDetailsEnricher(moex.Object, repo, NullLogger<SecurityDetailsEnricher>.Instance)
            .EnrichBatchAsync(200, TimeSpan.FromDays(30), TimeSpan.Zero, DateTime.UtcNow);

        saved.Should().Be(1);
        moex.Verify(m => m.GetSecurityDescriptionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Exactly(1 + SecurityDetailsEnricher.MaxConsecutiveFailures),
            "после серии неудач подряд тик прерывается, а не гоняет всю очередь");
    }

    [Fact]
    public async Task DetailsEnricher_EmptyQueue_DoesNothing()
    {
        var repo = new RepoFake();
        var moex = new Mock<IMoexIssClient>(MockBehavior.Strict);

        var saved = await new SecurityDetailsEnricher(moex.Object, repo, NullLogger<SecurityDetailsEnricher>.Instance)
            .EnrichBatchAsync(200, TimeSpan.FromDays(30), TimeSpan.Zero, DateTime.UtcNow);

        saved.Should().Be(0);
        repo.Saved.Should().BeEmpty();
    }

    [Fact]
    public async Task DetailsEnricher_FlushesInChunks_AllSaved()
    {
        var repo = new RepoFake { Queue = Enumerable.Range(0, 120).Select(i => $"S{i}").ToList() };
        var moex = new Mock<IMoexIssClient>();
        moex.Setup(m => m.GetSecurityDescriptionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string s, CancellationToken _) => Card(s));

        var saved = await new SecurityDetailsEnricher(moex.Object, repo, NullLogger<SecurityDetailsEnricher>.Instance)
            .EnrichBatchAsync(200, TimeSpan.FromDays(30), TimeSpan.Zero, DateTime.UtcNow);

        saved.Should().Be(120);
        repo.Saved.Should().HaveCount(120);
    }
}

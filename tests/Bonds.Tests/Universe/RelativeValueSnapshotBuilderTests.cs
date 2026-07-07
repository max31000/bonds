using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Bonds.Core.Universe;
using Bonds.Infrastructure.Universe;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Bonds.Tests.Universe;

/// <summary>
/// Задача 30 часть B — тесты оркестратора сглаженной статистики (медиана дневных медиан за 5
/// торговых дней) и деградации на молодую историю (basedOnDays честный). Мокаем
/// <see cref="IBondUniverseRepository"/> (тот же паттерн, что SyncCycleServiceTests — singleton-
/// сервис резолвит Scoped-репозиторий через IServiceScopeFactory, поэтому мок регистрируется как
/// singleton в тестовом DI-контейнере, а не подставляется напрямую в конструктор).
/// </summary>
public class RelativeValueSnapshotBuilderTests
{
    private readonly Mock<IBondUniverseRepository> _repo = new();

    private static BondUniverseEntry Entry(string secid, string sector = "Корпоративные", decimal duration = 2m, int listLevel = 1) => new()
    {
        Secid = secid,
        Isin = $"ISIN{secid}",
        ShortName = secid,
        Sector = sector,
        YieldFraction = 0.15m,
        DurationYears = duration,
        PricePercent = 99m,
        TurnoverRub = 1_000_000m,
        ListLevel = listLevel,
        MaturityDate = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(3)),
        UpdatedAt = DateTime.UtcNow,
    };

    private static BondUniverseHistoryPoint HistoryPoint(DateOnly date, string secid, decimal gspread, decimal duration = 2m) => new()
    {
        SnapshotDate = date,
        Secid = secid,
        YieldFraction = 0.15m,
        DurationYears = duration,
        GspreadApproxFraction = gspread,
    };

    private RelativeValueSnapshotBuilder BuildSut()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_repo.Object);
        services.AddSingleton<IOptions<BondUniverseRefreshOptions>>(Options.Create(new BondUniverseRefreshOptions()));
        services.AddSingleton<RelativeValueSnapshotBuilder>();
        var sp = services.BuildServiceProvider();
        return sp.GetRequiredService<RelativeValueSnapshotBuilder>();
    }

    [Fact]
    public async Task GetSnapshotAsync_NoHistoryAtAll_FallsBackToCurrentSnapshot_BasedOnDaysIsZero()
    {
        var entries = Enumerable.Range(0, 5).Select(i => Entry($"S{i}")).ToList();
        _repo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(entries);
        _repo.Setup(r => r.GetRecentHistoryAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<BondUniverseHistoryPoint>());

        var sut = BuildSut();
        var snapshot = await sut.GetSnapshotAsync();

        snapshot.BasedOnDays.Should().Be(0, "истории вообще нет — молодой банк, работаем по единственному текущему снимку");
        snapshot.AllMembers.Should().HaveCount(5);
    }

    [Fact]
    public async Task GetSnapshotAsync_FewerThanFiveDaysOfHistory_ReportsActualDayCount()
    {
        var entries = Enumerable.Range(0, 5).Select(i => Entry($"S{i}")).ToList();
        _repo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(entries);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var history = new List<BondUniverseHistoryPoint>();
        foreach (var dayOffset in new[] { 0, 1 }) // только 2 дня истории, не 5
        {
            var date = today.AddDays(-dayOffset);
            for (var i = 0; i < 5; i++)
            {
                history.Add(HistoryPoint(date, $"S{i}", 0.01m * (i + 1)));
            }
        }
        _repo.Setup(r => r.GetRecentHistoryAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(history);

        var sut = BuildSut();
        var snapshot = await sut.GetSnapshotAsync();

        snapshot.BasedOnDays.Should().Be(2, "в истории только 2 отличных дня — молодой банк, не выдумываем 5");
    }

    [Fact]
    public async Task GetSnapshotAsync_FiveDaysOfHistory_SmoothsMedianAcrossDays()
    {
        var entries = Enumerable.Range(0, 5).Select(i => Entry($"S{i}")).ToList();
        _repo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(entries);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var history = new List<BondUniverseHistoryPoint>();
        // День 0 (самый свежий) — выброс: спреды заметно выше обычного дня.
        for (var i = 0; i < 5; i++) history.Add(HistoryPoint(today, $"S{i}", 0.50m));
        // Дни 1-4 — обычные спреды 0.01..0.05.
        for (var dayOffset = 1; dayOffset <= 4; dayOffset++)
        {
            var date = today.AddDays(-dayOffset);
            for (var i = 0; i < 5; i++) history.Add(HistoryPoint(date, $"S{i}", 0.01m * (i + 1)));
        }
        _repo.Setup(r => r.GetRecentHistoryAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(history);

        var sut = BuildSut();
        var snapshot = await sut.GetSnapshotAsync();

        snapshot.BasedOnDays.Should().Be(5);
        var key = new Bonds.Core.Analytics.RelativeValueService.BasketKey { Sector = "Корпоративные", DurationBucket = "1–3 года" };
        snapshot.BasketStats.Should().ContainKey(key);
        // Медиана дневных медиан по 5 дням (0.50, 0.03, 0.03, 0.03, 0.03) = 0.03 — однодневный
        // выброс (день 0) не должен утянуть сглаженную медиану к 0.50.
        snapshot.BasketStats[key].Median.Should().Be(0.03m, "однодневный выброс не должен исказить медиану медиан");
    }

    [Fact]
    public async Task GetSnapshotAsync_CachesResult_DoesNotHitRepositoryTwiceWithinCacheWindow()
    {
        var entries = Enumerable.Range(0, 5).Select(i => Entry($"S{i}")).ToList();
        _repo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(entries);
        _repo.Setup(r => r.GetRecentHistoryAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<BondUniverseHistoryPoint>());

        var sut = BuildSut();
        await sut.GetSnapshotAsync();
        await sut.GetSnapshotAsync();

        _repo.Verify(r => r.GetAllAsync(It.IsAny<CancellationToken>()), Times.Once, "второй вызов должен обслуживаться из кэша (~1 час), не бить в БД снова");
    }

    [Fact]
    public async Task GetSnapshotAsync_HiddenBond_ExcludedFromHistoryMembers()
    {
        var visible = Entry("VISIBLE");
        var hidden = Entry("HIDDEN", listLevel: 3); // ListLevelThree — скрыт гигиеническим фильтром по дефолту
        var entries = new List<BondUniverseEntry> { visible, hidden };
        _repo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(entries);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var history = new List<BondUniverseHistoryPoint>
        {
            HistoryPoint(today, "VISIBLE", 0.01m),
            HistoryPoint(today, "HIDDEN", 5.0m), // "мусор" — если бы попал в статистику, исказил бы медиану
        };
        _repo.Setup(r => r.GetRecentHistoryAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(history);

        var sut = BuildSut();
        var snapshot = await sut.GetSnapshotAsync();

        snapshot.AllMembers.Should().ContainSingle(m => m.Secid == "VISIBLE");
        snapshot.AllMembers.Should().NotContain(m => m.Secid == "HIDDEN");
    }
}

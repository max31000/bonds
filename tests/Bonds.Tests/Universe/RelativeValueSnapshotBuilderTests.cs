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

    private static BondUniverseEntry Entry(string secid, string sector = "Корпоративные", decimal duration = 2m, int listLevel = 1, bool? isFloater = null) => new()
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
        IsFloater = isFloater,
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

    /// <summary>
    /// Задача 42: корзины строятся по ЭФФЕКТИВНОМУ спреду — биржевой побеждает приближённый (и в
    /// текущем снимке, и в истории), для старых строк истории без effective-колонки берётся approx.
    /// </summary>
    [Fact]
    public async Task GetSnapshotAsync_BasketMedian_UsesEffectiveSpread_OfficialOverApprox_LegacyHistoryFallsBack()
    {
        var a = Entry("A"); a.GspreadOfficialFraction = 0.0010m; a.GspreadApproxFraction = 0.9m;
        var b = Entry("B"); b.GspreadOfficialFraction = 0.0030m; b.GspreadApproxFraction = 0.9m;
        var c = Entry("C"); c.GspreadOfficialFraction = null; c.GspreadApproxFraction = 0.0020m;
        _repo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<BondUniverseEntry> { a, b, c });

        // Без истории — корпус строится из текущего снимка: [0.001, 0.003, 0.002] → медиана 0.002.
        _repo.Setup(r => r.GetRecentHistoryAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<BondUniverseHistoryPoint>());
        var snapshot = await BuildSut().GetSnapshotAsync();
        var key = new Bonds.Core.Analytics.RelativeValueService.BasketKey { Sector = "Корпоративные", DurationBucket = "1–3 года" };
        snapshot.BasketStats[key].Median.Should().Be(0.0020m);

        // С историей: A, B — новые строки (effective заполнен, approx — мусор 0.9), C — старая строка (только approx).
        _repo.Invocations.Clear();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var history = new List<BondUniverseHistoryPoint>
        {
            new() { SnapshotDate = today, Secid = "A", DurationYears = 2m, GspreadEffectiveFraction = 0.0010m, GspreadApproxFraction = 0.9m },
            new() { SnapshotDate = today, Secid = "B", DurationYears = 2m, GspreadEffectiveFraction = 0.0030m, GspreadApproxFraction = 0.9m },
            new() { SnapshotDate = today, Secid = "C", DurationYears = 2m, GspreadEffectiveFraction = null, GspreadApproxFraction = 0.0020m },
        };
        _repo.Setup(r => r.GetRecentHistoryAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(history);
        var withHistory = await BuildSut().GetSnapshotAsync();
        withHistory.BasketStats[key].Median.Should().Be(0.0020m);
    }

    /// <summary>
    /// Задача 44: оборот для ликвидности — медиана за 5 торговых дней из истории (одним батчевым чтением,
    /// без запроса на бумагу); для бумаги без истории — fallback на сегодняшний VALTODAY. Утренний
    /// VALTODAY (мал) не меняет оценку.
    /// </summary>
    [Fact]
    public async Task GetSnapshotAsync_MedianTurnover_FromHistoryBatch_WithFallbackToTodaysTurnover()
    {
        var withHistory = Entry("HIST"); withHistory.TurnoverRub = 40_000m; // утро: VALTODAY «на сейчас» мал
        var noHistory = Entry("NEW"); noHistory.TurnoverRub = 777_000m;
        _repo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<BondUniverseEntry> { withHistory, noHistory });

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var turnovers = new decimal?[] { 250_000_000m, 300_000_000m, null, 200_000_000m, 320_000_000m };
        var history = turnovers
            .Select((t, i) => new BondUniverseHistoryPoint
            {
                SnapshotDate = today.AddDays(-i), Secid = "HIST", DurationYears = 2m, GspreadApproxFraction = 0.01m, TurnoverRub = t,
            })
            .ToList();
        _repo.Setup(r => r.GetRecentHistoryAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(history);

        var snapshot = await BuildSut().GetSnapshotAsync();

        // Медиана 4 непустых значений (200, 250, 300, 320 млн) = (250+300)/2 = 275 млн.
        snapshot.LiquidityTurnoverRub(withHistory).Should().Be(275_000_000m);
        snapshot.LiquidityTurnoverRub(noHistory).Should().Be(777_000m, "истории по бумаге нет — сегодняшний VALTODAY");
        _repo.Verify(r => r.GetRecentHistoryAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once, "одно батчевое чтение на всех, без N+1");
    }

    [Fact]
    public async Task GetSnapshotAsync_MedianTurnover_IncludesHygieneHiddenSecids()
    {
        // Скрытая гигиеной бумага (листинг 3) всё равно получает медиану — ликвидность считается и для скрытых строк скринера.
        var hidden = Entry("HIDDEN", listLevel: 3);
        _repo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<BondUniverseEntry> { hidden });
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        _repo.Setup(r => r.GetRecentHistoryAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(new List<BondUniverseHistoryPoint>
        {
            new() { SnapshotDate = today, Secid = "HIDDEN", TurnoverRub = 9_000_000m },
        });

        var snapshot = await BuildSut().GetSnapshotAsync();

        snapshot.LiquidityTurnoverRub(hidden).Should().Be(9_000_000m);
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

    /// <summary>
    /// Задача 31 часть B.1/D.1 — эталон посчитан руками: 4 фикс-бумаги со спредами 0.01/0.02/0.03/
    /// 0.04 (медиана 0.025 — среднее двух средних при чётном count) + один флоатер со спредом 0.99
    /// (аномальный, если бы попал в статистику — утянул бы медиану далеко вверх). Флоатер должен
    /// быть исключён из корпуса ДО построения BasketMember (тот же паттерн, что hygiene-hidden
    /// бумага в тесте выше) — медиана считается только по 4 фикс-членам.
    /// </summary>
    [Fact]
    public async Task GetSnapshotAsync_FloaterBond_ExcludedFromCorpus_MedianComputedWithoutIt()
    {
        var fixedBonds = new List<BondUniverseEntry>
        {
            Entry("FIX1"), Entry("FIX2"), Entry("FIX3"), Entry("FIX4"),
        };
        var floater = Entry("FLOAT1", isFloater: true);
        var entries = fixedBonds.Concat([floater]).ToList();
        _repo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(entries);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var history = new List<BondUniverseHistoryPoint>
        {
            HistoryPoint(today, "FIX1", 0.01m),
            HistoryPoint(today, "FIX2", 0.02m),
            HistoryPoint(today, "FIX3", 0.03m),
            HistoryPoint(today, "FIX4", 0.04m),
            HistoryPoint(today, "FLOAT1", 0.99m), // аномальный спред флоатера — исказил бы медиану, если бы не был исключён
        };
        _repo.Setup(r => r.GetRecentHistoryAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(history);

        var sut = BuildSut();
        var snapshot = await sut.GetSnapshotAsync();

        snapshot.AllMembers.Should().HaveCount(4, "флоатер не должен стать членом корпуса RV-корзин");
        snapshot.AllMembers.Should().NotContain(m => m.Secid == "FLOAT1");
        snapshot.AllMembers.Should().OnlyContain(m => !m.IsFloater, "ни один выживший член корпуса не должен быть флоатером");

        var key = new Bonds.Core.Analytics.RelativeValueService.BasketKey { Sector = "Корпоративные", DurationBucket = "1–3 года" };
        snapshot.BasketStats.Should().ContainKey(key);
        snapshot.BasketStats[key].Count.Should().Be(4);
        snapshot.BasketStats[key].Median.Should().Be(0.025m, "медиана 4 фикс-членов (0.01/0.02/0.03/0.04), без флоатера 0.99");
    }

    /// <summary>
    /// Задача 31 часть D.4 — краевой кейс: IsFloater == null (BONDTYPE не пришёл от MOEX) трактуется
    /// как "не флоатер" — бумага НЕ исключается из корпуса (иначе теряли бы бумаги с неполным
    /// справочником, см. doc-comment BasketMember.IsFloater).
    /// </summary>
    [Fact]
    public async Task GetSnapshotAsync_NullIsFloater_TreatedAsNotFloater_NotExcluded()
    {
        var entries = new List<BondUniverseEntry>
        {
            Entry("UNKNOWN1", isFloater: null),
            Entry("UNKNOWN2", isFloater: null),
            Entry("UNKNOWN3", isFloater: null),
            Entry("UNKNOWN4", isFloater: null),
            Entry("UNKNOWN5", isFloater: null),
        };
        _repo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(entries);
        _repo.Setup(r => r.GetRecentHistoryAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<BondUniverseHistoryPoint>());

        var sut = BuildSut();
        var snapshot = await sut.GetSnapshotAsync();

        snapshot.AllMembers.Should().HaveCount(5, "IsFloater == null не исключается — трактуется как 'не флоатер'");
    }
}

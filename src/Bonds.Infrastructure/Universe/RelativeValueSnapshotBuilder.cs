using Bonds.Core.Analytics;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Bonds.Core.Time;
using Bonds.Core.Universe;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using static Bonds.Core.Analytics.RelativeValueService;

namespace Bonds.Infrastructure.Universe;

/// <summary>
/// Задача 30 часть B — оркестратор сглаженной статистики корзин relative value: медиана ДНЕВНЫХ
/// медиан корзины за последние <see cref="TradingDaysWindow"/> торговых дней (защита от однодневных
/// выбросов, план часть B.1), а не статистика по одному снимку. Если истории меньше — считает по
/// тому, что есть (в т.ч. по единственному текущему снимку bond_universe, если истории вообще нет),
/// честно возвращая <see cref="RelativeValueSnapshot.BasedOnDays"/>.
/// <para>
/// <b>Почему "медиана медиан", а не медиана всех точек за 5 дней слитно.</b> Слитая выборка не
/// эквивалентна взвешиванию каждого дня поровну — если в один день MOEX прислал вдвое больше
/// котировок (например, выходной укоротил предыдущую сессию), простое объединение точек придало бы
/// этому дню больший вес. Медиана дневных медиан взвешивает каждый ТОРГОВЫЙ ДЕНЬ поровну — план
/// часть B.1 говорит буквально "медиана дневных медиан", это осознанный выбор метода сглаживания,
/// не с точностью до формулы эквивалентный медиане по объединённой выборке.
/// </para>
/// <para>
/// <b>Кэш.</b> Результат держится в памяти (простое поле + таймстамп — тот же уровень простоты, что
/// <see cref="BondUniverseRefreshService"/> использует для отслеживания последнего тика; в проекте
/// нет уже используемого IMemoryCache, изобретать сложную кэш-инфраструктуру ради одного потребителя
/// не оправдано) на <see cref="BondUniverseRefreshOptions.RelativeValueCacheDuration"/> (дефолт
/// ~1 час, план часть B.3) — банк обновляется раз в час, пересчитывать корзины на каждый HTTP-
/// запрос excessive. Вынесено в опции (не константа), чтобы интеграционные тесты могли отключить
/// кэш (см. doc-comment опции).
/// </para>
/// <para>
/// <b>Lifetime: Singleton</b> (кэш должен переживать отдельные HTTP-запросы) — <see cref="IBondUniverseRepository"/>
/// (Scoped) резолвится ВНУТРИ <see cref="GetSnapshotAsync"/> через <see cref="IServiceScopeFactory"/>,
/// тот же паттерн, что <c>SyncCycleService</c> (doc-comment там же объясняет причину).
/// </para>
/// </summary>
public sealed class RelativeValueSnapshotBuilder
{
    /// <summary>Сколько последних торговых дней истории использовать для сглаживания (план часть B.1).</summary>
    public const int TradingDaysWindow = 5;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly UniverseHygieneOptions _hygieneOptions;
    private readonly TimeSpan _cacheDuration;

    private RelativeValueSnapshot? _cached;
    private DateTime _cachedAtUtc;
    private readonly SemaphoreSlim _buildGate = new(1, 1);

    public RelativeValueSnapshotBuilder(IServiceScopeFactory scopeFactory, IOptions<BondUniverseRefreshOptions> universeOptions)
    {
        _scopeFactory = scopeFactory;
        _hygieneOptions = universeOptions.Value.Hygiene;
        _cacheDuration = universeOptions.Value.RelativeValueCacheDuration;
    }

    /// <summary>Готовый снимок для API/UI — все данные, нужные для одной бумаги: полная резолюция
    /// корзины с fallback + confidence, плюс сколько дней истории легло в основу.</summary>
    public sealed record RelativeValueSnapshot
    {
        public required Dictionary<BasketKey, BasketStats> BasketStats { get; init; }
        public required IReadOnlyList<BasketMember> AllMembers { get; init; }
        public required int BasedOnDays { get; init; }

        /// <summary>ТЕКУЩИЙ (не исторический) снимок bond_universe по secid — для обогащения
        /// кандидатов именем/доходностью/ликвидностью (план часть C.2), которых нет в
        /// <see cref="BasketMember"/> (тот несёт только поля, нужные для статистики корзин).</summary>
        public required IReadOnlyDictionary<string, BondUniverseEntry> CurrentEntriesBySecid { get; init; }
    }

    /// <summary>Возвращает закэшированный снимок либо строит новый, если кэш пуст/протух.
    /// Не форсирует пересчёт — вызывающий код (эндпоинт) не должен ждать I/O чаще раза в час.
    /// Пересчёт защищён семафором (не блокирует запросы, читающие свежий кэш) — несколько
    /// одновременных запросов на протухший кэш не должны параллельно бить по БД N раз.</summary>
    public async Task<RelativeValueSnapshot> GetSnapshotAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        if (_cached is not null && now - _cachedAtUtc < _cacheDuration)
        {
            return _cached;
        }

        await _buildGate.WaitAsync(ct);
        try
        {
            // Другой запрос мог уже пересчитать снимок, пока этот ждал семафор.
            now = DateTime.UtcNow;
            if (_cached is not null && now - _cachedAtUtc < _cacheDuration)
            {
                return _cached;
            }

            var snapshot = await BuildAsync(ct);
            _cached = snapshot;
            _cachedAtUtc = now;
            return snapshot;
        }
        finally
        {
            _buildGate.Release();
        }
    }

    private async Task<RelativeValueSnapshot> BuildAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IBondUniverseRepository>();

        var currentEntries = await repo.GetAllAsync(ct);
        var currentBySecid = currentEntries.ToDictionary(e => e.Secid, StringComparer.OrdinalIgnoreCase);

        var today = BusinessClock.MoscowToday();
        // Задача 31 часть B.1: корпус RV-корзин исключает не только hygiene-hidden бумаги, но и
        // ФЛОАТЕРЫ — их биржевой YIELD — текущая доходность (не YTM), несравнима с фикс-купоном и
        // искажала бы медиану G-спреда корзины (см. doc-comment BasketMember.IsFloater про
        // null→false). Единая точка исключения — floater'ы никогда не становятся BasketMember, ни
        // в статистике корзин, ни в AllMembers (последнее важно и для "дешёвых соседей" RV —
        // BuildCheapCandidates читает AllMembers напрямую).
        var eligibleSecids = currentEntries
            .Where(e => UniverseHygieneFilter.Evaluate(e, _hygieneOptions, today) == HygieneHiddenReason.None)
            .Where(e => e.IsFloater != true)
            .Select(e => e.Secid)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var history = await repo.GetRecentHistoryAsync(TradingDaysWindow, ct);
        var historyByDate = history
            .Where(h => eligibleSecids.Contains(h.Secid))
            .GroupBy(h => h.SnapshotDate)
            .OrderByDescending(g => g.Key)
            .Take(TradingDaysWindow)
            .ToList();

        if (historyByDate.Count == 0)
        {
            // Банк молодой — истории вообще нет, работаем по единственному текущему снимку (план часть B.2).
            var members = BuildMembersFromCurrent(currentEntries, currentBySecid, eligibleSecids);
            return new RelativeValueSnapshot
            {
                BasketStats = BuildBasketStats(members),
                AllMembers = members,
                BasedOnDays = 0,
                CurrentEntriesBySecid = currentBySecid,
            };
        }

        // Для каждого торгового дня — свой набор BasketMember (сектор/hygiene статичные поля берутся
        // из ТЕКУЩЕГО снимка — bond_universe_history не хранит sector/list_level/maturity, см.
        // doc-comment BondUniverseHistoryPoint; сектор эмитента меняется крайне редко, это разумное
        // упрощение, не искажающее корзины).
        var dailyBasketMedians = new Dictionary<BasketKey, List<decimal>>();
        var dailyBasketP25 = new Dictionary<BasketKey, List<decimal>>();
        var dailyBasketP75 = new Dictionary<BasketKey, List<decimal>>();
        var latestDayCounts = new Dictionary<BasketKey, int>();

        var isFirstDay = true;
        foreach (var dayGroup in historyByDate)
        {
            var dayMembers = dayGroup
                .Where(h => currentBySecid.ContainsKey(h.Secid))
                .Select(h => new BasketMember
                {
                    Secid = h.Secid,
                    Sector = currentBySecid[h.Secid].Sector,
                    DurationYears = h.DurationYears,
                    GSpreadFraction = h.GspreadApproxFraction,
                    // Задача 31: bond_universe_history не хранит is_floater (см. doc-comment
                    // BondUniverseHistoryPoint) — берём из ТЕКУЩЕГО снимка, как и Sector выше.
                    // На практике всегда false здесь: dayGroup уже отфильтрован eligibleSecids
                    // (флоатеры исключены до этой точки) — поле проставлено для инварианта записи.
                    IsFloater = currentBySecid[h.Secid].IsFloater == true,
                })
                .ToList();

            var dayStats = BuildBasketStats(dayMembers);
            foreach (var (key, stats) in dayStats)
            {
                if (!dailyBasketMedians.TryGetValue(key, out var medians))
                {
                    medians = [];
                    dailyBasketMedians[key] = medians;
                }
                medians.Add(stats.Median);

                if (!dailyBasketP25.TryGetValue(key, out var p25s)) { p25s = []; dailyBasketP25[key] = p25s; }
                p25s.Add(stats.P25);

                if (!dailyBasketP75.TryGetValue(key, out var p75s)) { p75s = []; dailyBasketP75[key] = p75s; }
                p75s.Add(stats.P75);

                if (isFirstDay) latestDayCounts[key] = stats.Count; // самый свежий день (обход по убыванию даты) — представительный count для confidence.
            }

            isFirstDay = false;
        }

        var smoothedStats = new Dictionary<BasketKey, BasketStats>();
        foreach (var key in dailyBasketMedians.Keys)
        {
            smoothedStats[key] = new BasketStats
            {
                Median = MedianOf(dailyBasketMedians[key]),
                P25 = MedianOf(dailyBasketP25[key]),
                P75 = MedianOf(dailyBasketP75[key]),
                Count = latestDayCounts.GetValueOrDefault(key, dailyBasketMedians[key].Count),
            };
        }

        // AllMembers для fallback/percentile — берём САМЫЙ СВЕЖИЙ день истории (наиболее актуальный
        // состав "кто есть в корзине сейчас"), не объединение всех 5 дней (иначе одна бумага считалась
        // бы 5 раз в fallback-статистике "весь сектор"/"весь рынок").
        var latestDay = historyByDate[0];
        var latestMembers = latestDay
            .Where(h => currentBySecid.ContainsKey(h.Secid))
            .Select(h => new BasketMember
            {
                Secid = h.Secid,
                Sector = currentBySecid[h.Secid].Sector,
                DurationYears = h.DurationYears,
                GSpreadFraction = h.GspreadApproxFraction,
                IsFloater = currentBySecid[h.Secid].IsFloater == true,
            })
            .ToList();

        return new RelativeValueSnapshot
        {
            BasketStats = smoothedStats,
            AllMembers = latestMembers,
            BasedOnDays = historyByDate.Count,
            CurrentEntriesBySecid = currentBySecid,
        };
    }

    private static List<BasketMember> BuildMembersFromCurrent(
        IReadOnlyList<BondUniverseEntry> currentEntries,
        Dictionary<string, BondUniverseEntry> currentBySecid,
        HashSet<string> eligibleSecids)
    {
        return currentEntries
            .Where(e => eligibleSecids.Contains(e.Secid))
            .Select(e => new BasketMember
            {
                Secid = e.Secid,
                Sector = e.Sector,
                DurationYears = e.DurationYears,
                GSpreadFraction = e.GspreadApproxFraction,
                IsFloater = e.IsFloater == true,
            })
            .ToList();
    }

    private static decimal MedianOf(List<decimal> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 0 ? (sorted[mid - 1] + sorted[mid]) / 2m : sorted[mid];
    }
}

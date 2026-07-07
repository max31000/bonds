using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Bonds.Infrastructure.Connectors.Moex;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bonds.Infrastructure.Universe;

/// <summary>
/// Задача 26 часть C.1 — банк облигаций MOEX: раз в час (в торговые часы) обновляет снимок ВСЕЙ
/// рыночной вселенной (не только позиций/watchlist), плюс раз в день (первый тик после закрытия
/// торгов) пишет строку в дневную историю. Тот же паттерн устойчивости, что
/// <see cref="Bonds.Infrastructure.Quotes.LiveQuotesPollingService"/>: try/catch вокруг тика,
/// ошибка (сеть недоступна, БД недоступна) — Warning-лог и пропуск итерации, без падения процесса.
/// <para>
/// <b>Двухъярусная архитектура (plan/26, не нарушать).</b> Здесь НЕ вызывается точный движок
/// <see cref="Bonds.Core.Calculation.BondMetricsCalculator"/> — только дешёвая биржевая статистика
/// MOEX (YIELD/DURATION/обороты) + приближённый G-спред по сохранённой безрисковой кривой.
/// </para>
/// <para>
/// <b>Первый запуск.</b> Сразу при старте (не дожидаясь <see cref="BondUniverseRefreshOptions.RefreshInterval"/>),
/// если снимок пуст или старше <see cref="BondUniverseRefreshOptions.StaleThreshold"/> — план часть
/// C.1 явно требует не морозить банк на час после каждого передеплоя.
/// </para>
/// </summary>
public sealed class BondUniverseRefreshService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly BondUniverseRefreshOptions _options;
    private readonly ILogger<BondUniverseRefreshService> _logger;
    private readonly TimeZoneInfo _moscowTimeZone;

    private DateTime? _lastRefreshAttemptUtc;
    private DateOnly? _lastHistoryWriteDateMsk;

    public BondUniverseRefreshService(
        IServiceScopeFactory scopeFactory,
        IOptions<BondUniverseRefreshOptions> options,
        ILogger<BondUniverseRefreshService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
        _moscowTimeZone = ResolveMoscowTimeZone();
    }

    private static TimeZoneInfo ResolveMoscowTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Europe/Moscow");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Russian Standard Time");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unexpected error in bond universe refresh tick");
            }

            try
            {
                await Task.Delay(_options.PollingInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        var repo = sp.GetRequiredService<IBondUniverseRepository>();

        var utcNow = DateTime.UtcNow;
        var nowMsk = TimeZoneInfo.ConvertTimeFromUtc(utcNow, _moscowTimeZone);

        if (await ShouldRefreshSnapshotAsync(repo, nowMsk, utcNow, ct))
        {
            await RefreshSnapshotAsync(sp, ct);
            _lastRefreshAttemptUtc = utcNow;
        }

        await MaybeWriteHistorySnapshotAsync(repo, nowMsk, ct);
    }

    /// <summary>
    /// Часть C.1: обновляем снимок, если (а) это первый тик процесса и снимок пуст/старше
    /// <see cref="BondUniverseRefreshOptions.StaleThreshold"/> — не ждать интервала после
    /// передеплоя; либо (б) сейчас торговые часы MOEX И с последнего рефреша прошёл
    /// <see cref="BondUniverseRefreshOptions.RefreshInterval"/>.
    /// </summary>
    private async Task<bool> ShouldRefreshSnapshotAsync(
        IBondUniverseRepository repo, DateTime nowMsk, DateTime utcNow, CancellationToken ct)
    {
        if (_lastRefreshAttemptUtc is null)
        {
            var lastSnapshotUtc = await repo.GetLastRefreshUtcAsync(ct);
            if (lastSnapshotUtc is null || utcNow - lastSnapshotUtc.Value > _options.StaleThreshold)
            {
                return true; // снимок пуст/протух — обновляем сразу, не дожидаясь торговых часов/интервала.
            }
        }

        if (!IsWithinTradingHours(nowMsk))
        {
            return false;
        }

        return _lastRefreshAttemptUtc is null || utcNow - _lastRefreshAttemptUtc.Value >= _options.RefreshInterval;
    }

    private bool IsWithinTradingHours(DateTime nowMsk)
    {
        if (nowMsk.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return false;
        var timeOfDay = TimeOnly.FromDateTime(nowMsk);
        return timeOfDay >= _options.TradingWindowStartMsk && timeOfDay <= _options.TradingWindowEndMsk;
    }

    private async Task RefreshSnapshotAsync(IServiceProvider sp, CancellationToken ct)
    {
        var moex = sp.GetRequiredService<IMoexIssClient>();
        var repo = sp.GetRequiredService<IBondUniverseRepository>();
        var curveRepo = sp.GetRequiredService<IYieldCurveRepository>();

        IReadOnlyList<MoexBondMarketRow> rawRows;
        try
        {
            rawRows = await moex.GetBondMarketSnapshotAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Bond universe refresh: failed to fetch MOEX market snapshot — skipping this tick");
            return;
        }

        if (rawRows.Count == 0)
        {
            _logger.LogWarning("Bond universe refresh: MOEX returned an empty snapshot — skipping (keeping previous data)");
            return;
        }

        YieldCurveSnapshot? curve;
        try
        {
            curve = await curveRepo.GetLatestAsync();
        }
        catch (Exception ex)
        {
            // Кривая нужна только для приближённого G-спреда — её отсутствие не должно блокировать
            // обновление остальной статистики банка (§4.4 деградация, не падение).
            _logger.LogWarning(ex, "Bond universe refresh: failed to load yield curve — gspread_approx will be null this cycle");
            curve = null;
        }

        // Только рублёвые (FACEUNIT SUR/RUB) и не погашенные (MATDATE не в прошлом либо неизвестна —
        // отсутствие даты не повод исключать бумагу, гигиенический фильтр разберётся ниже по стеку).
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _moscowTimeZone));
        var entries = rawRows
            .Where(IsRubleDenominated)
            .Where(r => r.MatDate is null || r.MatDate.Value >= today)
            .Select(r => BondUniverseEntryMapper.Map(r, curve))
            .ToList();

        if (entries.Count == 0)
        {
            _logger.LogWarning("Bond universe refresh: no eligible RUB/active bonds after filtering — skipping upsert");
            return;
        }

        try
        {
            await repo.UpsertSnapshotBatchAsync(entries, ct);
            _logger.LogInformation("Bond universe refresh: upserted {Count} bonds", entries.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Bond universe refresh: failed to upsert snapshot — skipping this tick");
        }
    }

    private static bool IsRubleDenominated(MoexBondMarketRow row) =>
        row.FaceUnit is null or "SUR" or "RUB";

    /// <summary>
    /// Часть C.1: первый тик после закрытия торгов (сейчас позже <see cref="BondUniverseRefreshOptions.TradingWindowEndMsk"/>)
    /// пишет дневной срез — идемпотентно, один раз в день (отслеживается и внутренним полем
    /// процесса, и проверкой в БД — на случай перезапуска процесса в тот же день).
    /// </summary>
    private async Task MaybeWriteHistorySnapshotAsync(IBondUniverseRepository repo, DateTime nowMsk, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(nowMsk);
        var timeOfDay = TimeOnly.FromDateTime(nowMsk);

        if (timeOfDay < _options.TradingWindowEndMsk)
        {
            return; // торги ещё не закрылись — рано.
        }

        if (_lastHistoryWriteDateMsk == today)
        {
            return; // уже писали сегодня в этом процессе.
        }

        try
        {
            if (await repo.HasHistoryForDateAsync(today, ct))
            {
                _lastHistoryWriteDateMsk = today; // другой процесс/предыдущий запуск уже записал сегодня.
                return;
            }

            await repo.AppendDailyHistorySnapshotAsync(today, _options.HistoryRetentionDays, ct);
            _lastHistoryWriteDateMsk = today;
            _logger.LogInformation("Bond universe refresh: wrote daily history snapshot for {Date}", today);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Bond universe refresh: failed to write daily history snapshot for {Date} — will retry next tick", today);
        }
    }
}

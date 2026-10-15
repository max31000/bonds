using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Bonds.Infrastructure.Connectors.Moex;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bonds.Infrastructure.Universe;

/// <summary>
/// Банк облигаций MOEX: раз в час (в торговые часы) обновляет снимок ВСЕЙ
/// рыночной вселенной (не только позиций/watchlist), плюс раз в день (первый тик после закрытия
/// торгов) пишет строку в дневную историю. Тот же паттерн устойчивости, что
/// <see cref="Bonds.Infrastructure.Quotes.LiveQuotesPollingService"/>: try/catch вокруг тика,
/// ошибка (сеть недоступна, БД недоступна) — Warning-лог и пропуск итерации, без падения процесса.
/// <para>
/// <b>Двухъярусная архитектура (не нарушать).</b> Здесь НЕ вызывается точный движок
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

    // Задача 43: суточные шаги обогащения. Состояние в памяти процесса: после перезапуска шаги
    // повторяются один раз (безопасно: идемпотентный UPDATE/upsert, лишь лишние запросы к ISS).
    private DateOnly? _lastEmitentEnrichDateMsk;
    private DateTime? _lastEmitentEnrichAttemptUtc;
    private DateOnly? _lastSecurityDetailsDateMsk;

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

        // Задача 43: суточные шаги — после обновления снимка (чтобы строки банка уже существовали).
        // Каждый шаг изолирован: сбой одного не мешает другому и не валит тик.
        await MaybeEnrichEmitentsAsync(sp, repo, nowMsk, utcNow, ct);
        await MaybeEnrichSecurityDetailsAsync(sp, nowMsk, utcNow, ct);
    }

    /// <summary>
    /// Задача 43 часть A: раз в сутки (первый тик после <see cref="BondUniverseRefreshOptions.TradingWindowStartMsk"/>
    /// МСК) ИЛИ сразу, если в непустом банке ещё ни у одной бумаги нет эмитента, — записывает эмитентов
    /// из bulk-справочника ISS. Недоступный справочник не считается выполненным шагом, повтор не чаще
    /// <see cref="BondUniverseRefreshOptions.RefreshInterval"/>.
    /// </summary>
    private async Task MaybeEnrichEmitentsAsync(
        IServiceProvider sp, IBondUniverseRepository repo, DateTime nowMsk, DateTime utcNow, CancellationToken ct)
    {
        try
        {
            if (_lastEmitentEnrichAttemptUtc is { } lastAttempt && utcNow - lastAttempt < _options.RefreshInterval)
            {
                return; // недавно пробовали (справочник был недоступен) — не долбим ISS каждый тик.
            }

            var due = DailyEnrichmentGate.IsDue(nowMsk, _lastEmitentEnrichDateMsk, _options.TradingWindowStartMsk);
            if (!due)
            {
                // Вне суточного окна — только случай "банк непуст, эмитентов нет нигде" (первый запуск после миграции).
                if (_lastEmitentEnrichDateMsk is not null
                    || await repo.CountAsync(ct) == 0
                    || await repo.CountWithEmitentAsync(ct) > 0)
                {
                    return;
                }
            }

            _lastEmitentEnrichAttemptUtc = utcNow;
            var enricher = sp.GetRequiredService<UniverseEmitentEnricher>();
            if (await enricher.EnrichAsync(ct) is not null)
            {
                _lastEmitentEnrichDateMsk = DateOnly.FromDateTime(nowMsk);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Bond universe refresh: emitent enrichment failed — will retry later");
        }
    }

    /// <summary>
    /// Задача 43 часть B: раз в сутки обрабатывает до <see cref="BondUniverseRefreshOptions.SecurityDetailsBatchSize"/>
    /// карточек бумаг (приоритет: портфель/watchlist → банк без карточки → устаревшие). Дату помечаем
    /// выполненной сразу — ошибки по отдельным бумагам не повод долбить ISS каждые 5 минут.
    /// </summary>
    private async Task MaybeEnrichSecurityDetailsAsync(IServiceProvider sp, DateTime nowMsk, DateTime utcNow, CancellationToken ct)
    {
        try
        {
            if (!DailyEnrichmentGate.IsDue(nowMsk, _lastSecurityDetailsDateMsk, _options.TradingWindowStartMsk))
            {
                return;
            }

            _lastSecurityDetailsDateMsk = DateOnly.FromDateTime(nowMsk);
            var enricher = sp.GetRequiredService<SecurityDetailsEnricher>();
            await enricher.EnrichBatchAsync(
                _options.SecurityDetailsBatchSize, _options.SecurityDetailsStaleAfter,
                _options.SecurityDetailsRequestDelay, utcNow, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Bond universe refresh: security details enrichment failed — wait for the next day");
        }
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

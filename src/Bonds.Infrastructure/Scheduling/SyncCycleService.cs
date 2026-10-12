using Bonds.Core.Analytics;
using Bonds.Core.Calculation;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Bonds.Core.Signals;
using Bonds.Core.Time;
using Bonds.Infrastructure.Analytics;
using Bonds.Infrastructure.CashFlow;
using Bonds.Infrastructure.Services;
using Bonds.Infrastructure.Sync;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bonds.Infrastructure.Scheduling;

/// <summary>
/// Полный цикл "синк → пересчёт → сигналы" (plan/07 Часть B): синк T-Invest/MOEX
/// (<see cref="BondSyncService"/>) → построение holdings с метриками inline (БЕЗ отдельной
/// персистентности снимка метрик — см. plan/07 преамбула: "пересчитывай on-demand при каждом
/// тике") → проекция денежного потока (<see cref="CashFlowProjectionOrchestrator"/>) → снимок
/// NAV/XIRR (<see cref="PortfolioSnapshotService"/>) → прогон <see cref="SignalsEngine"/> и
/// синхронизация набора активных событий с БД (plan/40: insert/refresh/resolve, без дублей).
/// <para>
/// <b>Lifetime: Singleton.</b> Должен переживать отдельные HTTP-запросы/тики хостед-сервиса,
/// чтобы статус (<see cref="GetStatus"/>) и защита от параллельного запуска (семафор) были
/// общими для вызовов из <see cref="SyncSchedulerHostedService"/> и будущего
/// <c>POST /api/sync</c> (этап 08). Поэтому scoped-зависимости (репозитории, BondSyncService и
/// другие Scoped-сервисы) резолвятся ВНУТРИ <see cref="RunCycleAsync"/> через
/// <see cref="IServiceScopeFactory.CreateScope"/> — тот же паттерн, что у cashpulse
/// ExchangeRateRefreshService (plan/00 "Эталон фоновых задач").
/// </para>
/// <para>
/// <b>Защита от параллельного запуска.</b> <see cref="SemaphoreSlim"/> с неблокирующим
/// <c>WaitAsync(0, ct)</c> (НЕ ожидание занятого слота) — если цикл уже идёт, повторный вызов
/// немедленно возвращает <see cref="SyncCycleResult.AlreadyRunning"/> = true, не блокируя вызывающий
/// HTTP-запрос (plan/07: "так HTTP POST /api/sync не подвисает").
/// </para>
/// </summary>
public sealed class SyncCycleService : ISyncCycleRunner
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SignalEngineOptions _signalOptions;
    private readonly ILogger<SyncCycleService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private readonly object _statusLock = new();
    private bool _isRunning;
    private DateTime? _lastRunStartedAtUtc;
    private DateTime? _lastSuccessAtUtc;
    private DateTime? _lastFailureAtUtc;
    private List<string> _lastRunErrors = [];
    private bool _tokenMissingOrInvalid;

    public SyncCycleService(
        IServiceScopeFactory scopeFactory,
        IOptions<SignalEngineOptions> signalOptions,
        ILogger<SyncCycleService> logger)
    {
        _scopeFactory = scopeFactory;
        _signalOptions = signalOptions.Value;
        _logger = logger;
    }

    public SyncCycleStatus GetStatus()
    {
        lock (_statusLock)
        {
            return new SyncCycleStatus
            {
                IsRunning = _isRunning,
                LastRunStartedAtUtc = _lastRunStartedAtUtc,
                LastSuccessAtUtc = _lastSuccessAtUtc,
                LastFailureAtUtc = _lastFailureAtUtc,
                LastRunErrors = _lastRunErrors,
                TokenMissingOrInvalid = _tokenMissingOrInvalid,
            };
        }
    }

    public async Task<SyncCycleResult> RunCycleAsync(CancellationToken ct = default)
    {
        if (!await _gate.WaitAsync(0, ct))
        {
            _logger.LogInformation("Sync cycle requested while another cycle is already running — returning immediately");
            return new SyncCycleResult { AlreadyRunning = true };
        }

        try
        {
            lock (_statusLock)
            {
                _isRunning = true;
                _lastRunStartedAtUtc = DateTime.UtcNow;
            }

            var result = await RunCycleInternalAsync(ct);

            lock (_statusLock)
            {
                _lastRunErrors = result.Errors;
                _tokenMissingOrInvalid = result.TokenMissingOrInvalid;
                if (result.HasErrors)
                {
                    _lastFailureAtUtc = DateTime.UtcNow;
                }
                else
                {
                    _lastSuccessAtUtc = DateTime.UtcNow;
                }
            }

            return result;
        }
        finally
        {
            lock (_statusLock)
            {
                _isRunning = false;
            }

            _gate.Release();
        }
    }

    /// <summary>Меньше стольких снапшотов истории — история считается пустой и пересобирается бэкфиллом.</summary>
    internal const int BackfillSnapshotThreshold = 5;

    private async Task<SyncCycleResult> RunCycleInternalAsync(CancellationToken ct)
    {
        var result = new SyncCycleResult();
        using var scope = _scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;

        var accounts = sp.GetRequiredService<IAccountRepository>();
        var accountId = await accounts.GetPrimaryAccountIdAsync();
        if (accountId is null)
        {
            // Single-user продукт без онбординга (ни одного Account не создано) — не ошибка,
            // цикл просто нет смысла выполнять (plan/07: "используй тот же способ в Scheduler").
            _logger.LogInformation("No account configured yet — skipping sync cycle");
            result.NoAccountConfigured = true;
            return result;
        }

        var asOf = BusinessClock.MoscowToday();

        // Диагностика токена (plan/13 часть B) — вычисляется отдельно от шага синка ниже, чтобы
        // здоровье синка (GET /api/sync/status) отражало причину сбоя явным флагом, а не требовало
        // от фронта парсить текст ошибки. Заведённый Account уже найден выше — "пользователь есть".
        var tokenProvider = sp.GetRequiredService<ITInvestTokenProvider>();
        var tokenStatus = await tokenProvider.GetTokenStatusAsync(ct);
        result.TokenMissingOrInvalid = tokenStatus is TInvestTokenStatus.NotConfigured or TInvestTokenStatus.DecryptionFailed;

        // --- 1. BondSyncService ---
        var journalSynced = false;
        try
        {
            var syncService = sp.GetRequiredService<BondSyncService>();
            var syncResult = await syncService.SyncAsync(accountId.Value, ct: ct);
            result.InstrumentsSynced = syncResult.InstrumentsSynced;
            result.OperationsUpserted = syncResult.OperationsUpserted;
            result.YieldCurveUpdated = syncResult.YieldCurveUpdated;
            journalSynced = syncResult.JournalSynced;
            if (syncResult.HasErrors) result.Errors.AddRange(syncResult.Errors.Select(e => $"Sync: {e}"));
        }
        catch (InvalidOperationException ex)
        {
            // Известное восстановимое состояние (например, токен T-Invest ещё не задан в
            // настройках — см. TInvestPortfolioClient.GetClientAsync) — сообщение уже написано
            // как user-facing подсказка, прокидываем его как есть, а не маскируем типом исключения.
            _logger.LogWarning("Sync step skipped for account {AccountId}: {Reason}", accountId, ex.Message);
            result.Errors.Add($"Sync: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sync step failed for account {AccountId}", accountId);
            result.Errors.Add($"Sync: непредвиденная ошибка ({ex.GetType().Name})");
        }

        // --- 1a. Watchlist (задача 20 часть A): справочник/расписания/котировка бумаг без позиции.
        // Отдельный шаг, независимый от счёта/токена T-Invest — не влияет на позиции/сигналы ниже,
        // поэтому не должен блокировать остальной цикл при собственной ошибке.
        try
        {
            var watchlistSync = sp.GetRequiredService<WatchlistSyncService>();
            var watchlistResult = await watchlistSync.SyncAllAsync(ct);
            result.WatchlistInstrumentsSynced = watchlistResult.InstrumentsSynced;
            if (watchlistResult.HasErrors) result.Errors.AddRange(watchlistResult.Errors);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Watchlist sync step failed");
            result.Errors.Add($"Watchlist: непредвиденная ошибка ({ex.GetType().Name})");
        }

        // --- 1b. Бэкфилл истории стоимости, если она пуста/почти пуста (первый запуск, или миграция
        // пересчёта истории её очистила). Строго ПОСЛЕ успешного перечитывания журнала: история
        // строится из журнала, и бэкфилл из неполного журнала закрепился бы навсегда (бэкфилл
        // пропускает уже существующие даты).
        if (journalSynced)
        {
            try
            {
                var snapshotRepo = sp.GetRequiredService<IPortfolioValueSnapshotRepository>();
                var existing = (await snapshotRepo.GetByAccountIdAsync(accountId.Value)).Count();
                if (existing < BackfillSnapshotThreshold)
                {
                    var backfill = sp.GetRequiredService<PortfolioHistoryBackfillService>();
                    var written = await backfill.BackfillAsync(accountId.Value, asOf, ct);
                    _logger.LogInformation("History backfill after sync: {Written} points (had {Existing})", written, existing);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "History backfill failed for account {AccountId}", accountId);
                result.Errors.Add($"History: непредвиденная ошибка ({ex.GetType().Name})");
            }
        }

        // --- 2. Проекция денежного потока ---
        try
        {
            var projectionOrchestrator = sp.GetRequiredService<CashFlowProjectionOrchestrator>();
            var projectionResult = await projectionOrchestrator.ProjectAccountAsync(accountId.Value, asOf, ct: ct);
            result.PositionsProjected = projectionResult.PositionsProjected;
            result.FlowsWritten = projectionResult.FlowsWritten;
            if (projectionResult.HasErrors) result.Errors.AddRange(projectionResult.Errors.Select(e => $"Projection: {e}"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cash-flow projection step failed for account {AccountId}", accountId);
            result.Errors.Add($"Projection: непредвиденная ошибка ({ex.GetType().Name})");
        }

        // --- 3. Снимок NAV/XIRR ---
        try
        {
            var snapshotService = sp.GetRequiredService<PortfolioSnapshotService>();
            await snapshotService.ComputeAndStoreSnapshotAsync(accountId.Value, asOf, ct);
            result.SnapshotStored = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Portfolio snapshot step failed for account {AccountId}", accountId);
            result.Errors.Add($"Snapshot: непредвиденная ошибка ({ex.GetType().Name})");
        }

        // --- 4. Signals Engine (требует holdings с метриками inline + контексты позиций) ---
        try
        {
            var signalsCreated = await RunSignalsAsync(sp, accountId.Value, asOf, ct);
            result.SignalsCreated = signalsCreated;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Signals step failed for account {AccountId}", accountId);
            result.Errors.Add($"Signals: непредвиденная ошибка ({ex.GetType().Name})");
        }

        return result;
    }

    /// <summary>
    /// Шаг «События» (plan/40): собирает <see cref="SignalEvaluationInput"/> (holdings — через
    /// <see cref="PortfolioHoldingsBuilder"/>, расписания открытых позиций — из репозиториев; пороги —
    /// <c>UserSettings</c> поверх дефолтов <see cref="SignalEngineOptions"/>; свободные деньги —
    /// <c>accounts.cash_rub</c>), прогоняет <see cref="SignalsEngine.Evaluate"/> (ПОЛНЫЙ набор активных
    /// событий) и синхронизирует его с таблицей через <see cref="SignalSetSynchronizer"/>: новое —
    /// insert, прежнее — refresh, исчезнувшее — resolved, закрытые старше 90 дней — удаляются.
    /// Для НОВЫХ Critical-событий (put-оферта) шлёт одно Telegram-сообщение на цикл. Троттлинг —
    /// естественный: событие вставляется один раз по ключу, повторные синки его не «создают» заново.
    /// Возвращает число вставленных (новых) событий.
    /// </summary>
    private async Task<int> RunSignalsAsync(IServiceProvider sp, ulong accountId, DateOnly asOf, CancellationToken ct)
    {
        var holdingsBuilder = sp.GetRequiredService<PortfolioHoldingsBuilder>();
        var couponRepo = sp.GetRequiredService<ICouponScheduleRepository>();
        var amortizationRepo = sp.GetRequiredService<IAmortizationScheduleRepository>();
        var offerRepo = sp.GetRequiredService<IOfferScheduleRepository>();
        var targetAllocationRepo = sp.GetRequiredService<ITargetAllocationRepository>();
        var signalRepo = sp.GetRequiredService<ISignalRepository>();
        var accountRepo = sp.GetRequiredService<IAccountRepository>();
        var settingsRepo = sp.GetRequiredService<IUserSettingsRepository>();

        var account = await accountRepo.GetByIdAsync(accountId);
        var userSettings = account is null ? null : await settingsRepo.GetByUserIdAsync(account.UserId);
        var options = _signalOptions.WithOverrides(
            userSettings?.UpcomingEventDaysThreshold,
            userSettings?.UninvestedCashThresholdRub,
            userSettings?.DefaultMaxConcentrationPercent);

        var targetAllocations = (await targetAllocationRepo.GetByAccountIdAsync(accountId)).ToList();

        // Позиции уже сверены с брокером (закрытые удалены, plan/39) — Quantity > 0 страхует от остатков.
        var holdings = (await holdingsBuilder.BuildForAccountAsync(accountId, asOf, ct))
            .Where(h => h.Quantity > 0m)
            .ToList();

        var positionContexts = new List<SignalPositionContext>(holdings.Count);
        foreach (var holding in holdings)
        {
            ct.ThrowIfCancellationRequested();

            positionContexts.Add(new SignalPositionContext
            {
                PositionId = holding.PositionId,
                InstrumentId = holding.InstrumentId,
                Issuer = holding.Issuer,
                Name = holding.Name,
                MaturityDate = holding.MaturityDate,
                Quantity = holding.Quantity,
                CouponType = holding.CouponType,
                Coupons = (await couponRepo.GetByInstrumentIdAsync(holding.InstrumentId)).ToList(),
                Amortizations = (await amortizationRepo.GetByInstrumentIdAsync(holding.InstrumentId)).ToList(),
                Offers = (await offerRepo.GetByInstrumentIdAsync(holding.InstrumentId)).ToList(),
            });
        }

        var produced = SignalsEngine.Evaluate(new SignalEvaluationInput
        {
            AccountId = accountId,
            AsOf = asOf,
            Positions = positionContexts,
            Holdings = holdings,
            CashRub = account?.CashRub,
            TargetAllocations = targetAllocations,
            Options = options,
        });

        var now = DateTime.UtcNow;
        var existing = await signalRepo.GetByAccountIdAsync(accountId, includeResolved: true);
        var plan = SignalSetSynchronizer.Plan(produced, existing);

        foreach (var signal in plan.ToInsert)
        {
            ct.ThrowIfCancellationRequested();
            signal.FirstSeenAt = now;
            signal.LastSeenAt = now;
            await signalRepo.InsertAsync(signal);
        }

        foreach (var (current, fresh) in plan.ToRefresh)
        {
            ct.ThrowIfCancellationRequested();
            await signalRepo.RefreshAsync(current.Id, fresh.Severity, fresh.Subject, fresh.SuggestedAction, now);
        }

        await signalRepo.ResolveAsync(plan.ToResolve.Select(s => s.Id).ToList(), now);
        await signalRepo.PurgeResolvedBeforeAsync(accountId, now.AddDays(-ResolvedRetentionDays));

        await NotifyNewCriticalAsync(sp, plan.ToInsert, ct);

        return plan.ToInsert.Count;
    }

    /// <summary>Сколько дней хранить закрытые события (история) до удаления.</summary>
    internal const int ResolvedRetentionDays = 90;

    /// <summary>
    /// Одно Telegram-сообщение на цикл о НОВЫХ Critical-событиях (put-оферта — требует решения
    /// владельца). Купоны/Info/Warning не пушатся. Сбой отправки не роняет цикл
    /// (<see cref="ITelegramAlertSender.SendAsync"/> не бросает).
    /// </summary>
    private static async Task NotifyNewCriticalAsync(IServiceProvider sp, IReadOnlyList<Signal> inserted, CancellationToken ct)
    {
        var critical = inserted.Where(s => s.Severity == SignalSeverity.Critical).ToList();
        if (critical.Count == 0) return;

        var sender = sp.GetService<ITelegramAlertSender>();
        if (sender is null) return;

        var lines = critical.Select(s => $"• {s.Subject}: {s.SuggestedAction}");
        await sender.SendAsync("Bonds: требуется ваше решение" + Environment.NewLine + string.Join(Environment.NewLine, lines), ct);
    }
}

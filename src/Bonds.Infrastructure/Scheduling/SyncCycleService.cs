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
using Bonds.Infrastructure.Universe;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bonds.Infrastructure.Scheduling;

/// <summary>
/// Полный цикл "синк → пересчёт → сигналы": синк T-Invest/MOEX
/// (<see cref="BondSyncService"/>) → построение holdings с метриками inline (БЕЗ отдельной
/// персистентности снимка метрик) → проекция денежного потока (<see cref="CashFlowProjectionOrchestrator"/>) → снимок
/// NAV/XIRR (<see cref="PortfolioSnapshotService"/>) → прогон <see cref="SignalsEngine"/> и
/// синхронизация набора активных событий с БД (insert/refresh/resolve, без дублей).
/// <para>
/// <b>Lifetime: Singleton.</b> Должен переживать отдельные HTTP-запросы/тики хостед-сервиса,
/// чтобы статус (<see cref="GetStatus"/>) и защита от параллельного запуска (семафор) были
/// общими для вызовов из <see cref="SyncSchedulerHostedService"/> и эндпоинта
/// <c>POST /api/sync</c>. Поэтому scoped-зависимости (репозитории, BondSyncService и
/// другие Scoped-сервисы) резолвятся ВНУТРИ <see cref="RunCycleAsync"/> через
/// <see cref="IServiceScopeFactory.CreateScope"/> — стандартный паттерн фоновых сервисов.
/// </para>
/// <para>
/// <b>Защита от параллельного запуска.</b> <see cref="SemaphoreSlim"/> с неблокирующим
/// <c>WaitAsync(0, ct)</c> (НЕ ожидание занятого слота) — если цикл уже идёт, повторный вызов
/// немедленно возвращает <see cref="SyncCycleResult.AlreadyRunning"/> = true, не блокируя вызывающий
/// HTTP-запрос ("так HTTP POST /api/sync не подвисает").
/// </para>
/// </summary>
public sealed class SyncCycleService : ISyncCycleRunner
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SignalEngineOptions _signalOptions;
    private readonly ILogger<SyncCycleService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// T-67E (minor 4): «хвост» ворот синка. Держится ядром цикла (пока оно пишет данные) и передаётся фоновому прогону правил — тот отпускает его, дочитав
    /// снимок. Следующий цикл ждёт его ПЕРЕД первой записью: правила никогда не читают наполовину переписанный снимок, а <c>/api/sync/status</c> после
    /// ядра показывает «не идёт» (основные ворота <see cref="_gate"/> отпущены).
    /// </summary>
    private readonly SemaphoreSlim _policiesGate = new(1, 1);

    private readonly object _statusLock = new();
    private bool _isRunning;
    private DateTime? _lastRunStartedAtUtc;
    private DateTime? _lastSuccessAtUtc;
    private DateTime? _lastFailureAtUtc;
    private List<string> _lastRunErrors = [];
    private bool _tokenMissingOrInvalid;
    private int _skippedForeignOperations;

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
                SkippedForeignOperations = _skippedForeignOperations,
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

        SyncCycleResult result;
        var policiesHeld = false;
        var handOverToPolicies = false;
        try
        {
            lock (_statusLock)
            {
                _isRunning = true;
                _lastRunStartedAtUtc = DateTime.UtcNow;
            }

            // T-67E (minor 4): прогон правил прошлого цикла ещё читает снимок — ждём его, прежде чем переписывать данные.
            await _policiesGate.WaitAsync(ct);
            policiesHeld = true;

            result = await RunCycleInternalAsync(ct);
            handOverToPolicies = !result.NoAccountConfigured;

            lock (_statusLock)
            {
                _lastRunErrors = result.Errors;
                _tokenMissingOrInvalid = result.TokenMissingOrInvalid;
                _skippedForeignOperations = result.SkippedForeignOperations;
                if (result.HasErrors)
                {
                    _lastFailureAtUtc = DateTime.UtcNow;
                }
                else
                {
                    _lastSuccessAtUtc = DateTime.UtcNow;
                }
            }

        }
        finally
        {
            lock (_statusLock)
            {
                _isRunning = false;
            }

            if (policiesHeld && !handOverToPolicies) _policiesGate.Release();
            _gate.Release();
        }

        // T-67D: правила и планы (задача 63) — ВНЕ критического пути синка. Ворота уже отпущены (/api/sync/status показывает «не идёт»), прогон идёт в фоне
        // в своём DI-скоупе; цикл (ручной POST /api/sync и плановые суточные) возвращает результат, не дожидаясь правил. Параллельные прогоны правил одного
        // пользователя сериализует его GET_LOCK (IPolicyUserLock). Нет счёта — правил тоже нет (как раньше: цикл пропускался целиком).
        // T-67E (minor 4): «хвост» ворот (_policiesGate) передан прогону правил — следующий цикл не начнёт писать данные, пока правила не дочитают снимок.
        if (handOverToPolicies) StartBackgroundPolicies();
        return result;
    }

    private Task _backgroundPolicies = Task.CompletedTask;

    /// <summary>
    /// Последний фоновый прогон правил (T-67D). Никогда не завершается с ошибкой (исключения ловятся и пишутся в лог) — для тестов и диагностики.
    /// </summary>
    internal Task BackgroundPolicies => Volatile.Read(ref _backgroundPolicies);

    private void StartBackgroundPolicies()
    {
        // CancellationToken.None: токен HTTP-запроса ручного синка отменится сразу после ответа — фоновый прогон не должен от него зависеть.
        var task = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                await Timed("policies (background)", () => RunPoliciesAsync(scope.ServiceProvider, CancellationToken.None));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Background policy run failed — plans keep the previous state");
            }
            finally
            {
                _policiesGate.Release(); // хвост ворот синка: следующий цикл может писать данные
            }
        });
        Volatile.Write(ref _backgroundPolicies, task);
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
            // цикл просто нет смысла выполнять ("используй тот же способ в Scheduler").
            _logger.LogInformation("No account configured yet — skipping sync cycle");
            result.NoAccountConfigured = true;
            return result;
        }

        var asOf = BusinessClock.MoscowToday();

        // Диагностика токена — вычисляется отдельно от шага синка ниже, чтобы
        // здоровье синка (GET /api/sync/status) отражало причину сбоя явным флагом, а не требовало
        // от фронта парсить текст ошибки. Заведённый Account уже найден выше — "пользователь есть".
        var tokenProvider = sp.GetRequiredService<ITInvestTokenProvider>();

        // T-67E (minor 1): токен и счёт — ОДНОГО пользователя. Без привязки провайдер берёт «основного пользователя» (первый users.id), а основной счёт —
        // первый счёт T-Invest: у разных пользователей это разные записи, и счёт владельца синкался бы чужим токеном. Привязка — ДО первого чтения токена в скоупе.
        if ((await accounts.GetByIdAsync(accountId.Value))?.UserId is { } ownerUserId) tokenProvider.UseUser(ownerUserId);
        var tokenStatus = await tokenProvider.GetTokenStatusAsync(ct);
        result.TokenMissingOrInvalid = tokenStatus is TInvestTokenStatus.NotConfigured or TInvestTokenStatus.DecryptionFailed;

        // --- 1. BondSyncService ---
        var journalSynced = false;
        try
        {
            var syncService = sp.GetRequiredService<BondSyncService>();
            var syncResult = await Timed("tinvest-sync", () => syncService.SyncAsync(accountId.Value, ct: ct));
            result.InstrumentsSynced = syncResult.InstrumentsSynced;
            result.OperationsUpserted = syncResult.OperationsUpserted;
            result.YieldCurveUpdated = syncResult.YieldCurveUpdated;
            journalSynced = syncResult.JournalSynced;
            result.SkippedForeignOperations = syncResult.SkippedForeignOperations;
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

        // --- 1-bis. T-Invest остальных пользователей (задача 67, #5): каждый пользователь с валидным tinvest_read синкается СВОИМ токеном в свой счёт.
        // Основной путь выше не меняется. Сбой чужого синка — только лог (цикл владельца неуспешным не помечается, как у Freedom).
        await Timed("tinvest-other-users", () => SyncOtherTInvestUsersAsync(sp, accountId.Value, asOf, ct));

        // --- 1a. Watchlist: справочник/расписания/котировка бумаг без позиции.
        // Отдельный шаг, независимый от счёта/токена T-Invest — не влияет на позиции/сигналы ниже,
        // поэтому не должен блокировать остальной цикл при собственной ошибке.
        try
        {
            var watchlistSync = sp.GetRequiredService<WatchlistSyncService>();
            var watchlistResult = await Timed("watchlist", () => watchlistSync.SyncAllAsync(ct));
            result.WatchlistInstrumentsSynced = watchlistResult.InstrumentsSynced;
            if (watchlistResult.HasErrors) result.Errors.AddRange(watchlistResult.Errors);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Watchlist sync step failed");
            result.Errors.Add($"Watchlist: непредвиденная ошибка ({ex.GetType().Name})");
        }

        // --- 1a'. risk_level облигаций T-Invest (задача 46 часть C): ОДИН bulk-вызов Bonds() раз в сутки
        // (ворота по БД внутри рефрешера). Необязательный вход скоркарты: сбой или отсутствие токена —
        // только лог, цикл НЕ помечается неуспешным (иначе Telegram-алерт синка из-за второстепенного шага).
        if (!result.TokenMissingOrInvalid)
        {
            await Timed("risk-levels", () => RefreshRiskLevelsAsync(sp, ct));
        }

        // --- 1a''. Рыночные данные фондов (задача 51): история цен паёв, выплаты, индекс RGBI — раз в сутки (ворота
        // по БД внутри). Необязательный шаг: сбой — только лог, цикл НЕ помечается неуспешным. MOEX-часть работает и без
        // токена T-Invest (выплаты в этом случае пропускаются).
        await Timed("fund-market-data", () => RefreshFundMarketDataAsync(sp, tInvestAvailable: !result.TokenMissingOrInvalid, ct));

        // --- 1a-bis. Торговые метаданные инструментов (задача 61): лот, шаг цены, флаги, режим торгов, признак выплат фондов - раз в сутки
        // (ворота по записям внутри). Необязательный шаг: сбой - только лог, цикл НЕ помечается неуспешным. Freedom-часть работает по ключам
        // пользователей и от токена T-Invest не зависит.
        await Timed("trading-meta", () => RefreshTradingMetaAsync(sp, tInvestAvailable: !result.TokenMissingOrInvalid, ct));

        // --- 1a-ter. История курса валют (задача 55, MOEX CETS) - тот же суточный шаг и те же правила (необязательный, сбой - лог).
        await Timed("currency-rates", () => RefreshCurrencyRatesAsync(sp, ct));

        // --- 1a-quater. Счёт Freedom (задача 60): позиции, деньги, журнал по всем пользователям с ключами. СТРОГО после T-Invest и отдельным
        // шагом: сбой Freedom (сеть, ключи) пишется в лог и НЕ добавляется в result.Errors - цикл T-Invest неуспешным не помечается.
        await Timed("freedom", () => SyncFreedomAsync(sp, ct));

        // --- 1a-quinquies. Правила и планы сделок (задача 63) — T-67D: вынесены из цикла, запускаются в фоне ПОСЛЕ его завершения (StartBackgroundPolicies).

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
            var projectionResult = await Timed("projection", () => projectionOrchestrator.ProjectAccountAsync(accountId.Value, asOf, ct: ct));
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
            await Timed("snapshot", () => snapshotService.ComputeAndStoreSnapshotAsync(accountId.Value, asOf, ct));
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
            var signalsCreated = await Timed("signals", () => RunSignalsAsync(sp, accountId.Value, asOf, ct));
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
    /// Задача 67 (#5): синк T-Invest для пользователей с валидным <c>tinvest_read</c>, кроме владельца основного счёта: отдельный DI-скоуп на пользователя
    /// (scoped <see cref="ITInvestTokenProvider"/> привязывается к нему — клиент T-Invest строится по ЕГО токену), счёт T-Invest пользователя (нет —
    /// создаётся, брокерский счёт привяжет первый синк), затем журнал/позиции, проекция денежного потока и снимок стоимости. Сигналы (уведомления
    /// владельцу) для чужих счетов не запускаются.
    /// </summary>
    private async Task SyncOtherTInvestUsersAsync(IServiceProvider sp, ulong primaryAccountId, DateOnly asOf, CancellationToken ct)
    {
        var credentials = sp.GetService<IUserCredentialRepository>();
        if (credentials is null) return; // шаг не зарегистрирован (юнит-тесты цикла)

        IReadOnlyList<ulong> users;
        ulong? owner;
        try
        {
            owner = (await sp.GetRequiredService<IAccountRepository>().GetByIdAsync(primaryAccountId))?.UserId;
            users = await credentials.ListUserIdsAsync(Bonds.Core.Credentials.CredentialProviders.TInvestRead, Bonds.Core.Credentials.CredentialStatus.Ok) ?? [];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "T-Invest users lookup failed — other users are not synced this cycle");
            return;
        }

        foreach (var userId in users.Where(u => u != owner).Distinct())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var usp = scope.ServiceProvider;
                usp.GetRequiredService<ITInvestTokenProvider>().UseUser(userId);

                var accounts = usp.GetRequiredService<IAccountRepository>();
                var account = (await accounts.GetByUserIdAndBrokerAsync(userId, Brokers.TInvest) ?? []).OrderBy(a => a.Id).FirstOrDefault();
                var accountId = account?.Id ?? await accounts.CreateAsync(new Account { UserId = userId, Broker = Brokers.TInvest });

                var sync = await usp.GetRequiredService<BondSyncService>().SyncAsync(accountId, ct: ct);
                if (sync.HasErrors) _logger.LogWarning("T-Invest sync of user {UserId} finished with problems: {Errors}", userId, string.Join("; ", sync.Errors));

                await usp.GetRequiredService<CashFlowProjectionOrchestrator>().ProjectAccountAsync(accountId, asOf, ct: ct);
                await usp.GetRequiredService<PortfolioSnapshotService>().ComputeAndStoreSnapshotAsync(accountId, asOf, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "T-Invest sync of user {UserId} failed — their data keeps the previous values", userId);
            }
        }
    }

    /// <summary>Длительность шага цикла в лог (Information) — диагностика медленного синка (2026-10-24: ~60 с против ~18 с до цикла 58).</summary>
    private async Task<T> Timed<T>(string step, Func<Task<T>> run)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try { return await run(); }
        finally { _logger.LogInformation("Sync step {Step} took {ElapsedMs} ms", step, sw.ElapsedMilliseconds); }
    }

    private async Task Timed(string step, Func<Task> run)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try { await run(); }
        finally { _logger.LogInformation("Sync step {Step} took {ElapsedMs} ms", step, sw.ElapsedMilliseconds); }
    }

    private async Task RefreshFundMarketDataAsync(IServiceProvider sp, bool tInvestAvailable, CancellationToken ct)
    {
        try
        {
            // GetService (не Required): цикл не должен падать, если шаг не зарегистрирован (юнит-тесты цикла).
            var refresher = sp.GetService<FundMarketDataRefresher>();
            if (refresher is null) return;

            await refresher.RefreshIfDueAsync(BusinessClock.MoscowToday(), tInvestAvailable, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fund market data refresh failed — fund prices/distributions keep the previous values");
        }
    }

    private async Task RunPoliciesAsync(IServiceProvider sp, CancellationToken ct)
    {
        try
        {
            // GetService (не Required): цикл не должен падать, если шаг не зарегистрирован (юнит-тесты цикла).
            var runner = sp.GetService<Bonds.Infrastructure.Policy.IPolicyCycleRunner>();
            if (runner is null) return;

            var run = await runner.RunAllAsync(DateTime.UtcNow, ct);
            if (run.CreatedPlanIds.Count > 0 || run.Expired > 0)
            {
                _logger.LogInformation("Policy run: {Created} plan(s) created, {Expired} expired (dry-run, no orders are sent)", run.CreatedPlanIds.Count, run.Expired);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Policy run failed — plans keep the previous state");
        }
    }

    private async Task SyncFreedomAsync(IServiceProvider sp, CancellationToken ct)
    {
        try
        {
            // GetService (не Required): цикл не должен падать, если шаг не зарегистрирован (юнит-тесты цикла).
            var freedom = sp.GetService<FreedomSyncService>();
            if (freedom is null) return;

            var freedomResult = await freedom.SyncAllAsync(ct);
            if (freedomResult.Errors.Count > 0)
            {
                _logger.LogWarning("Freedom sync finished with {Count} problem(s): {Errors}", freedomResult.Errors.Count, string.Join("; ", freedomResult.Errors));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Freedom sync step failed — Freedom data keeps the previous values");
        }
    }

    private async Task RefreshTradingMetaAsync(IServiceProvider sp, bool tInvestAvailable, CancellationToken ct)
    {
        try
        {
            // GetService (не Required): цикл не должен падать, если шаг не зарегистрирован (юнит-тесты цикла).
            var refresher = sp.GetService<TradingMetaRefresher>();
            if (refresher is null) return;

            var refreshed = await refresher.RefreshIfDueAsync(DateTime.UtcNow, tInvestAvailable, ct);
            if (refreshed.Errors.Count > 0)
            {
                _logger.LogWarning("Trading meta refresh finished with {Count} problem(s): {Errors}", refreshed.Errors.Count, string.Join("; ", refreshed.Errors));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Trading meta refresh failed — trading metadata keeps the previous values");
        }
    }

    private async Task RefreshCurrencyRatesAsync(IServiceProvider sp, CancellationToken ct)
    {
        try
        {
            var refresher = sp.GetService<CurrencyRateRefresher>();
            if (refresher is null) return;

            await refresher.RefreshIfDueAsync(BusinessClock.MoscowToday(), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Currency rate refresh failed — currency rates keep the previous values");
        }
    }

    private async Task RefreshRiskLevelsAsync(IServiceProvider sp, CancellationToken ct)
    {
        try
        {
            // GetService (не Required): цикл не должен падать, если шаг не зарегистрирован (юнит-тесты цикла).
            var refresher = sp.GetService<InstrumentRiskLevelRefresher>();
            if (refresher is null) return;

            await refresher.RefreshIfDueAsync(DateTime.UtcNow, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "T-Invest risk level refresh failed — scorecard keeps the previous values");
        }
    }

    /// <summary>
    /// Шаг «События»: собирает <see cref="SignalEvaluationInput"/> (holdings — через
    /// <see cref="PortfolioHoldingsBuilder"/>, расписания открытых позиций — из репозиториев; пороги —
    /// <c>UserSettings</c> поверх дефолтов <see cref="SignalEngineOptions"/>; свободные деньги —
    /// <c>accounts.cash_rub</c>), прогоняет <see cref="SignalsEngine.Evaluate"/> (ПОЛНЫЙ набор активных
    /// событий) и синхронизирует его с таблицей через <see cref="SignalSetSynchronizer"/>: новое —
    /// insert, прежнее — refresh, исчезнувшее — resolved, закрытые старше 90 дней — удаляются.
    /// Для НОВЫХ Critical-событий (put-оферта, дефолт/техдефолт эмитента) шлёт одно Telegram-сообщение на цикл. Троттлинг —
    /// естественный: событие вставляется один раз по ключу, повторные синки его не «создают» заново.
    /// Возвращает число вставленных (новых) событий.
    /// </summary>
    private async Task<int> RunSignalsAsync(IServiceProvider sp, ulong accountId, DateOnly asOf, CancellationToken ct)
    {
        var holdingsBuilder = sp.GetRequiredService<PortfolioHoldingsBuilder>();
        var couponRepo = sp.GetRequiredService<ICouponScheduleRepository>();
        var amortizationRepo = sp.GetRequiredService<IAmortizationScheduleRepository>();
        var offerRepo = sp.GetRequiredService<IOfferScheduleRepository>();
        var signalRepo = sp.GetRequiredService<ISignalRepository>();
        var accountRepo = sp.GetRequiredService<IAccountRepository>();
        var settingsRepo = sp.GetRequiredService<IUserSettingsRepository>();

        var account = await accountRepo.GetByIdAsync(accountId);
        var userSettings = account is null ? null : await settingsRepo.GetByUserIdAsync(account.UserId);
        var options = _signalOptions.WithOverrides(
            userSettings?.UpcomingEventDaysThreshold,
            userSettings?.UninvestedCashThresholdRub,
            userSettings?.DefaultMaxConcentrationPercent,
            userSettings?.SpreadWideningThresholdFraction,
            userSettings?.PriceDrawdownThresholdFraction);

        // Позиции уже сверены с брокером (закрытые удалены) — Quantity > 0 страхует от остатков.
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

        // Задача 49: рыночные (SpreadWidening/PriceDrawdown) и кредитное (IssuerDefaultEvent) входы — батчево
        // (банк по ISIN, история 20 дней, карточки/дефолт-агрегаты эмитентов). Сбой чтения НЕ глотаем: пустой набор
        // был бы принят за «события исчезли» — закрыл бы активные и повторно разослал Critical.
        // GetService (не Required): юнит-тесты цикла без банка/карточек работают с пустым входом.
        var marketInputs = PositionMarketInputs.Empty;
        var marketBuilder = sp.GetService<PositionMarketContextBuilder>();
        if (marketBuilder is not null)
        {
            marketInputs = await marketBuilder.BuildAsync(holdings, asOf, ct);
        }

        var produced = SignalsEngine.Evaluate(new SignalEvaluationInput
        {
            AccountId = accountId,
            AsOf = asOf,
            Positions = positionContexts,
            Holdings = holdings,
            MarketContexts = marketInputs.Market,
            IssuerDefaults = marketInputs.IssuerDefaults,
            CashRub = account?.CashRub,
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

        // Задача 68: новое Critical-событие → разбор ИИ-агентом (только постановка в очередь; ворота — agent_enabled, подключение, ≤ 3 разборов в сутки — при запуске).
        var newCritical = plan.ToInsert.Where(s => s.Severity == SignalSeverity.Critical).ToList();
        if (newCritical.Count > 0 && account is not null)
            sp.GetService<Bonds.Infrastructure.Agents.IAgentEventTrigger>()?.CriticalSignalsDetected(account.UserId, newCritical.Select(s => $"{s.Subject}: {s.SuggestedAction}").ToList());

        return plan.ToInsert.Count;
    }

    /// <summary>Сколько дней хранить закрытые события (история) до удаления.</summary>
    internal const int ResolvedRetentionDays = 90;

    /// <summary>
    /// Одно Telegram-сообщение на цикл о НОВЫХ Critical-событиях (put-оферта и дефолт/техдефолт эмитента
    /// — требуют решения владельца; ключ дефолта без даты — алерт один раз, пока флаг есть). Купоны/Info/Warning не пушатся. Сбой отправки не роняет цикл
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

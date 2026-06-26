using Bonds.Core.Analytics;
using Bonds.Core.Calculation;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Bonds.Core.Signals;
using Bonds.Core.Time;
using Bonds.Infrastructure.Analytics;
using Bonds.Infrastructure.CashFlow;
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
/// NAV/XIRR (<see cref="PortfolioSnapshotService"/>) → прогон <see cref="SignalsEngine"/> с
/// дедупликацией против непрочитанных сигналов в БД → запись новых сигналов.
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

        // --- 1. BondSyncService ---
        try
        {
            var syncService = sp.GetRequiredService<BondSyncService>();
            var syncResult = await syncService.SyncAsync(accountId.Value, ct: ct);
            result.InstrumentsSynced = syncResult.InstrumentsSynced;
            result.OperationsUpserted = syncResult.OperationsUpserted;
            result.YieldCurveUpdated = syncResult.YieldCurveUpdated;
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
    /// Собирает <see cref="SignalEvaluationInput"/> из репозиториев (позиции, инструменты,
    /// расписания, котировки, операции, TargetAllocation, существующие непрочитанные сигналы),
    /// считает <see cref="BondMetrics"/> inline для каждой позиции (plan/07: "BondMetricsCalculator
    /// нигде не вызывается из Infrastructure — вызывай inline, без отдельного сохранения снимка"),
    /// прогоняет <see cref="SignalsEngine.Evaluate"/> и сохраняет только новые (уже
    /// дедуплицированные движком) сигналы.
    /// </summary>
    private async Task<int> RunSignalsAsync(IServiceProvider sp, ulong accountId, DateOnly asOf, CancellationToken ct)
    {
        var positionRepo = sp.GetRequiredService<IPositionRepository>();
        var instrumentRepo = sp.GetRequiredService<IInstrumentRepository>();
        var couponRepo = sp.GetRequiredService<ICouponScheduleRepository>();
        var amortizationRepo = sp.GetRequiredService<IAmortizationScheduleRepository>();
        var offerRepo = sp.GetRequiredService<IOfferScheduleRepository>();
        var quoteRepo = sp.GetRequiredService<IMarketQuoteRepository>();
        var yieldCurveRepo = sp.GetRequiredService<IYieldCurveRepository>();
        var operationRepo = sp.GetRequiredService<IOperationRepository>();
        var targetAllocationRepo = sp.GetRequiredService<ITargetAllocationRepository>();
        var signalRepo = sp.GetRequiredService<ISignalRepository>();

        var positions = (await positionRepo.GetByAccountIdAsync(accountId)).ToList();
        var operations = (await operationRepo.GetByAccountIdAsync(accountId)).ToList();
        var targetAllocations = (await targetAllocationRepo.GetByAccountIdAsync(accountId)).ToList();
        var existingUnread = (await signalRepo.GetByAccountIdAsync(accountId, isRead: false)).ToList();
        var curve = await yieldCurveRepo.GetLatestAsync();

        var positionContexts = new List<SignalPositionContext>();
        var holdings = new List<PortfolioHolding>();

        foreach (var position in positions)
        {
            ct.ThrowIfCancellationRequested();

            var instrument = await instrumentRepo.GetByIdAsync(position.InstrumentId);
            if (instrument is null) continue; // ссылочная целостность нарушена — пропускаем, не падаем (та же устойчивость, что у других оркестраторов)

            var coupons = (await couponRepo.GetByInstrumentIdAsync(position.InstrumentId)).ToList();
            var amortizations = (await amortizationRepo.GetByInstrumentIdAsync(position.InstrumentId)).ToList();
            var offers = (await offerRepo.GetByInstrumentIdAsync(position.InstrumentId)).ToList();

            positionContexts.Add(new SignalPositionContext
            {
                PositionId = position.Id,
                InstrumentId = instrument.Id,
                Issuer = instrument.Issuer,
                Name = instrument.Name,
                MaturityDate = instrument.MaturityDate,
                Coupons = coupons,
                Amortizations = amortizations,
                Offers = offers,
            });

            var quote = await quoteRepo.GetLatestAsync(position.InstrumentId);

            var metricsInput = new BondMetricsCalculatorInput
            {
                InstrumentId = instrument.Id,
                AsOf = asOf,
                FaceValue = instrument.FaceValue,
                MaturityDate = instrument.MaturityDate,
                CouponType = instrument.CouponType,
                HasAmortization = instrument.HasAmortization,
                HasOffers = instrument.HasOffers,
                DataIncomplete = instrument.DataIncomplete || position.DataIncomplete,
                CleanPrice = quote?.CleanPrice,
                AccruedInterestFromSource = quote?.Accrued ?? position.Accrued,
                Coupons = coupons,
                Amortizations = amortizations,
                Offers = offers,
                CurveSnapshot = curve,
            };

            var metrics = BondMetricsCalculator.Calculate(metricsInput);

            // Если котировка недоступна, BondMetrics.DirtyPrice деградирует к 0 (см. doc-comment
            // BondMetricsCalculator) — holding попадает в список с нулевой рыночной стоимостью
            // вместо исключения из него: для правил композиции/концентрации (5/6/7) это безопасная
            // деградация (вклад в Σ MarketValue = 0, не искажает доли остальных эмитентов), в
            // отличие от PortfolioSnapshotService (NAV), где такая позиция вместо этого полностью
            // исключается из суммы — разная семантика разных потребителей одних и тех же данных.
            var marketValue = (quote?.DirtyPrice ?? metrics.DirtyPrice) * position.Quantity;

            holdings.Add(new PortfolioHolding
            {
                PositionId = position.Id,
                InstrumentId = instrument.Id,
                Quantity = position.Quantity,
                MarketValueRub = marketValue,
                Issuer = instrument.Issuer,
                Sector = instrument.Sector,
                CouponType = instrument.CouponType,
                MaturityDate = instrument.MaturityDate,
                HorizonDate = metrics.HorizonDate,
                IsCalculatedToOffer = metrics.CalculatedToOffer,
                ModifiedDuration = metrics.ModifiedDuration,
                YtmEffective = metrics.YtmEffective,
                CurrentYield = metrics.CurrentYield,
                GSpread = metrics.GSpread,
                IsFloater = metrics.IsFloater,
                IsIndexed = metrics.IsIndexed,
                IsEstimated = metrics.IsEstimated,
                DataIncomplete = metrics.DataIncomplete,
            });
        }

        var evaluationInput = new SignalEvaluationInput
        {
            AccountId = accountId,
            AsOf = asOf,
            Positions = positionContexts,
            Holdings = holdings,
            Operations = operations,
            TargetAllocations = targetAllocations,
            ExistingUnreadSignals = existingUnread,
            Options = _signalOptions,
        };

        var newSignals = SignalsEngine.Evaluate(evaluationInput);

        foreach (var signal in newSignals)
        {
            ct.ThrowIfCancellationRequested();
            await signalRepo.CreateAsync(signal);
        }

        return newSignals.Count;
    }
}

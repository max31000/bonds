namespace Bonds.Infrastructure.Scheduling;

/// <summary>
/// Контракт полного цикла синка (plan/07 Часть B) — реализуется <see cref="SyncCycleService"/>.
/// Интерфейс выделен отдельно (а не просто конкретный класс, как у BondSyncService/
/// CashFlowProjectionOrchestrator/PortfolioSnapshotService) потому что: (1) этот сервис должен
/// быть Singleton (держит статус и семафор между тиками — см. doc-comment SyncCycleService), тогда
/// как остальные оркестраторы Scoped; (2) этап 08 будет резолвить его и из HostedService, и из
/// HTTP-контроллера force-refresh — интерфейс упрощает мокирование в тестах эндпоинта на этапе 08.
/// </summary>
public interface ISyncCycleRunner
{
    /// <summary>
    /// Запускает полный цикл (синк → метрики/holdings inline → проекция потока → снимок NAV/XIRR →
    /// сигналы) для единственного счёта продукта. Защищён от параллельного запуска — если цикл уже
    /// идёт, возвращает результат немедленно со статусом "already running", не дожидаясь
    /// завершения (см. doc-comment <see cref="SyncCycleService.RunCycleAsync"/>).
    /// </summary>
    Task<SyncCycleResult> RunCycleAsync(CancellationToken ct = default);

    /// <summary>Текущий статус последнего/текущего цикла — для будущего GET /api/sync/status (этап 08).</summary>
    SyncCycleStatus GetStatus();
}

/// <summary>Итог одного вызова <see cref="ISyncCycleRunner.RunCycleAsync"/>.</summary>
public sealed class SyncCycleResult
{
    /// <summary>true — цикл не выполнялся, т.к. другой вызов уже выполняется (см. doc-comment SyncCycleService).</summary>
    public bool AlreadyRunning { get; set; }

    /// <summary>true — не найдено ни одного Account (БД без онбординга) — цикл пропущен, это не ошибка.</summary>
    public bool NoAccountConfigured { get; set; }

    /// <summary>
    /// Plan/13 часть B: true, если у заведённого пользователя токен T-Invest не задан или не
    /// расшифровался (см. <see cref="Bonds.Infrastructure.Services.TInvestTokenStatus"/>).
    /// </summary>
    public bool TokenMissingOrInvalid { get; set; }

    public int InstrumentsSynced { get; set; }

    /// <summary>Задача 20 часть A: сколько уникальных watchlist ISIN обновлено (справочник + котировка) за цикл.</summary>
    public int WatchlistInstrumentsSynced { get; set; }
    public int OperationsUpserted { get; set; }
    public bool YieldCurveUpdated { get; set; }
    public int PositionsProjected { get; set; }
    public int FlowsWritten { get; set; }
    public bool SnapshotStored { get; set; }
    public int SignalsCreated { get; set; }

    public List<string> Errors { get; } = [];

    public bool HasErrors => Errors.Count > 0;
}

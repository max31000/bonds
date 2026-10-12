namespace Bonds.Infrastructure.Scheduling;

/// <summary>
/// Контракт полного цикла синка — реализуется <see cref="SyncCycleService"/>.
/// Интерфейс выделен отдельно (а не просто конкретный класс, как у BondSyncService/
/// CashFlowProjectionOrchestrator/PortfolioSnapshotService) потому что: (1) этот сервис должен
/// быть Singleton (держит статус и семафор между тиками — см. doc-comment SyncCycleService), тогда
/// как остальные оркестраторы Scoped; (2) его резолвят и HostedService, и
/// эндпоинт force-refresh — интерфейс упрощает мокирование в тестах эндпоинта.
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

    /// <summary>Текущий статус последнего/текущего цикла — для будущего GET /api/sync/status.</summary>
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
    /// true, если у заведённого пользователя токен T-Invest не задан или не
    /// расшифровался (см. <see cref="Bonds.Infrastructure.Services.TInvestTokenStatus"/>).
    /// </summary>
    public bool TokenMissingOrInvalid { get; set; }

    public int InstrumentsSynced { get; set; }

    /// <summary>Сколько уникальных watchlist ISIN обновлено (справочник + котировка) за цикл.</summary>
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

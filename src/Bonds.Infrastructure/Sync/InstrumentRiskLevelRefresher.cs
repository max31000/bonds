using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Bonds.Infrastructure.Connectors.TInvest;
using Microsoft.Extensions.Logging;

namespace Bonds.Infrastructure.Sync;

/// <summary>
/// Задача 46 часть C: суточное обновление <c>instrument_risk_levels</c> одним bulk-вызовом T-Invest
/// <c>Instruments.Bonds()</c>. Живёт в контуре <see cref="Bonds.Infrastructure.Scheduling.SyncCycleService"/>, а не в
/// <c>BondUniverseRefreshService</c>: токен T-Invest резолвится ровно в этом контуре (клиент строится из токена
/// пользователя в БД), а универсум-тик токена не знает и MOEX-only. «Раз в сутки» обеспечивает БД-ворота
/// (<c>MAX(fetched_at)</c> моложе <see cref="RefreshInterval"/> → пропуск): переживает рестарты процесса и
/// ручной «Обновить данные», не требует состояния в памяти. Исключения не гасятся — решает вызывающий шаг цикла.
/// </summary>
public sealed class InstrumentRiskLevelRefresher
{
    /// <summary>Минимальный интервал между bulk-вызовами (20 ч: суточный шаг не «уплывает» из-за разброса времени циклов).</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(20);

    private readonly ITInvestPortfolioClient _tInvest;
    private readonly IInstrumentRiskLevelRepository _repository;
    private readonly ILogger<InstrumentRiskLevelRefresher> _logger;

    public InstrumentRiskLevelRefresher(
        ITInvestPortfolioClient tInvest,
        IInstrumentRiskLevelRepository repository,
        ILogger<InstrumentRiskLevelRefresher> logger)
    {
        _tInvest = tInvest;
        _repository = repository;
        _logger = logger;
    }

    /// <summary>Обновляет кэш, если он старше <see cref="RefreshInterval"/>. Возвращает число записанных бумаг
    /// (0 — пропуск по воротам или пустой ответ брокера: в этом случае отметка времени НЕ сдвигается, повтор в следующем цикле).</summary>
    public async Task<int> RefreshIfDueAsync(DateTime utcNow, CancellationToken ct = default)
    {
        var lastFetched = await _repository.GetLastFetchedAtAsync(ct);
        if (lastFetched is { } last && utcNow - last < RefreshInterval)
        {
            return 0;
        }

        var levels = await _tInvest.GetBondRiskLevelsAsync(ct);
        if (levels.Count == 0)
        {
            _logger.LogWarning("T-Invest Bonds() returned no instruments — risk levels not updated");
            return 0;
        }

        var items = levels.Select(l => new InstrumentRiskLevel(l.Isin, l.RiskLevel, utcNow)).ToList();
        await _repository.UpsertBatchAsync(items, ct);
        _logger.LogInformation("T-Invest risk levels refreshed: {Count} bonds", items.Count);
        return items.Count;
    }
}

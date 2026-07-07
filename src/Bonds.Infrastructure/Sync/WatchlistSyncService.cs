using Bonds.Core.Interfaces.Repositories;
using Microsoft.Extensions.Logging;

namespace Bonds.Infrastructure.Sync;

/// <summary>
/// Задача 20 (часть A): синк watchlist-бумаг (ISIN без позиции). Переиспользует
/// <see cref="InstrumentEnrichmentService.EnrichByIsinAsync"/> — единый путь заведения в
/// <c>instruments</c> + обогащения расписаниями/котировкой, вынесенный в задаче 27 (общий с
/// материализацией бумаги из банка облигаций, <c>POST /api/universe/{secid}/materialize</c>), чтобы
/// watchlist и materialize не дублировали код.
/// </summary>
public sealed class WatchlistSyncService
{
    private readonly IWatchlistItemRepository _watchlistItems;
    private readonly InstrumentEnrichmentService _enrichment;
    private readonly ILogger<WatchlistSyncService> _logger;

    public WatchlistSyncService(
        IWatchlistItemRepository watchlistItems,
        InstrumentEnrichmentService enrichment,
        ILogger<WatchlistSyncService> logger)
    {
        _watchlistItems = watchlistItems;
        _enrichment = enrichment;
        _logger = logger;
    }

    /// <summary>Обновляет справочник/расписания/котировку для всех watchlist-записей всех пользователей (шаг цикла синка).</summary>
    public async Task<WatchlistSyncResult> SyncAllAsync(CancellationToken ct = default)
    {
        var result = new WatchlistSyncResult();
        var items = (await _watchlistItems.GetAllAsync()).ToList();

        // Один ISIN может быть у нескольких пользователей (или совпадать с позицией другого счёта) —
        // не тянем справочник/котировку повторно для дублей за один цикл.
        var distinctIsins = items.Select(i => i.Isin).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        foreach (var isin in distinctIsins)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var instrumentId = await SyncOneAsync(isin, ct);
                if (instrumentId is null)
                {
                    result.Errors.Add($"Watchlist: ISIN {isin} не найден на MOEX — пропущен");
                    continue;
                }

                result.InstrumentsSynced++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to sync watchlist ISIN {Isin} — skipping, continuing with the rest", isin);
                result.Errors.Add($"Watchlist: ISIN {isin} — ошибка синка ({ex.GetType().Name})");
            }
        }

        return result;
    }

    /// <summary>
    /// Обогащает справочник + обновляет котировку для ОДНОГО ISIN — переиспользуется и циклом синка
    /// (<see cref="SyncAllAsync"/>), и синхронным добавлением из <c>POST /api/watchlist</c>
    /// (WatchlistEndpoints), чтобы бумага появлялась с метриками сразу после добавления, не
    /// дожидаясь следующего тика планировщика (plan/20 §A.4). Тонкая обёртка над
    /// <see cref="InstrumentEnrichmentService.EnrichByIsinAsync"/> (задача 27) — сохранена ради
    /// обратной совместимости вызовов (WatchlistEndpoints, SyncCycleService).
    /// </summary>
    public Task<ulong?> SyncOneAsync(string isin, CancellationToken ct = default) =>
        _enrichment.EnrichByIsinAsync(isin, ct);
}

/// <summary>Итог одного вызова синка watchlist — для логирования/отображения (без секретов).</summary>
public sealed class WatchlistSyncResult
{
    public int InstrumentsSynced { get; set; }
    public List<string> Errors { get; } = [];
    public bool HasErrors => Errors.Count > 0;
}

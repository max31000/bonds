using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Bonds.Infrastructure.Connectors.Moex;
using Microsoft.Extensions.Logging;

namespace Bonds.Infrastructure.Sync;

/// <summary>
/// Задача 20 (часть A): синк watchlist-бумаг (ISIN без позиции). Переиспользует
/// <see cref="BondSyncService.ResolveOrCreateInstrumentByIsinAsync"/> — тот же путь заведения в
/// <c>instruments</c> и обогащения расписаниями, что и у позиций (никакого параллельного пайплайна).
/// Дополнительно (то, чего нет у позиций — у watchlist-бумаги нет брокерского счёта/T-Invest) —
/// пишет котировку MOEX как <see cref="MarketQuote"/> с <see cref="MarketQuoteSource.Moex"/>:
/// чистая цена — из PREVPRICE/PREVWAPRICE securities.json (% от номинала → рубли, тот же перевод,
/// что <see cref="Analytics.PortfolioHistoryBackfillService"/>). НКД сознательно не пишется здесь
/// (Accrued=null) — <see cref="Bonds.Core.Calculation.BondMetricsCalculator"/> сам считает его
/// пропорционально по графику купонов (<see cref="Bonds.Core.Calculation.AccruedInterestCalculator"/>)
/// как fallback, когда источник не дал НКД явно (см. plan/20 §A.3).
/// </summary>
public sealed class WatchlistSyncService
{
    private readonly IWatchlistItemRepository _watchlistItems;
    private readonly IInstrumentRepository _instruments;
    private readonly IMoexIssClient _moex;
    private readonly IMarketQuoteRepository _quotes;
    private readonly BondSyncService _bondSync;
    private readonly ILogger<WatchlistSyncService> _logger;

    public WatchlistSyncService(
        IWatchlistItemRepository watchlistItems,
        IInstrumentRepository instruments,
        IMoexIssClient moex,
        IMarketQuoteRepository quotes,
        BondSyncService bondSync,
        ILogger<WatchlistSyncService> logger)
    {
        _watchlistItems = watchlistItems;
        _instruments = instruments;
        _moex = moex;
        _quotes = quotes;
        _bondSync = bondSync;
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
    /// дожидаясь следующего тика планировщика (plan/20 §A.4).
    /// </summary>
    public async Task<ulong?> SyncOneAsync(string isin, CancellationToken ct = default)
    {
        var instrumentId = await _bondSync.ResolveOrCreateInstrumentByIsinAsync(isin, ct);
        if (instrumentId is null) return null;

        await RefreshQuoteAsync(instrumentId.Value, ct);
        return instrumentId;
    }

    /// <summary>Котировка вне позиции — из MOEX (последняя цена marketdata securities.json). НКД не пишется (см. doc-comment класса) — движок считает fallback'ом по графику купонов.</summary>
    private async Task RefreshQuoteAsync(ulong instrumentId, CancellationToken ct)
    {
        var instrument = await _instruments.GetByIdAsync(instrumentId);
        if (instrument is null || string.IsNullOrEmpty(instrument.Secid)) return;

        var info = await _moex.GetSecurityInfoAsync(instrument.Secid, ct);
        var pricePercent = info?.PrevPrice ?? info?.PrevWaPrice;
        if (pricePercent is null) return; // нет свежей котировки — не подставляем ноль молча (spec §4.4)

        var cleanPriceRub = pricePercent.Value / 100m * instrument.FaceValue;

        await _quotes.UpsertAsync(new MarketQuote
        {
            InstrumentId = instrumentId,
            AsOf = DateOnly.FromDateTime(DateTime.UtcNow),
            CleanPrice = cleanPriceRub,
            Accrued = null,
            Source = MarketQuoteSource.Moex,
        });
    }
}

/// <summary>Итог одного вызова синка watchlist — для логирования/отображения (без секретов).</summary>
public sealed class WatchlistSyncResult
{
    public int InstrumentsSynced { get; set; }
    public List<string> Errors { get; } = [];
    public bool HasErrors => Errors.Count > 0;
}

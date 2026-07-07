using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Bonds.Infrastructure.Connectors.Moex;

namespace Bonds.Infrastructure.Sync;

/// <summary>
/// Задача 27 часть A: общий путь «обогатить одну бумагу вне позиции по ISIN» — вынесен из
/// <see cref="WatchlistSyncService"/> (где раньше жил как приватный <c>SyncOneAsync</c>/
/// <c>RefreshQuoteAsync</c>), чтобы watchlist (задача 20) и материализация из банка облигаций
/// (<c>POST /api/universe/{secid}/materialize</c>, задача 27) переиспользовали ОДИН путь, а не
/// дублировали резолв/котировку каждый по-своему.
/// <para>
/// Заводит/находит <see cref="Instrument"/> через
/// <see cref="Bonds.Infrastructure.Sync.BondSyncService.ResolveOrCreateInstrumentByIsinAsync"/> (тот же
/// путь, что и у позиций) и пишет котировку из MOEX (последняя цена marketdata securities.json) как
/// <see cref="MarketQuote"/> с <see cref="MarketQuoteSource.Moex"/>. НКД сознательно не пишется здесь
/// (Accrued=null) — <see cref="Bonds.Core.Calculation.BondMetricsCalculator"/> сам считает его
/// пропорционально по графику купонов как fallback (см. исходный doc-comment WatchlistSyncService,
/// сохранён смысл при переносе).
/// </para>
/// Не создаёт watchlist-запись и не знает о ней — вызывающий код (watchlist/materialize) сам решает,
/// нужна ли запись в <c>watchlist_items</c>.
/// </summary>
public sealed class InstrumentEnrichmentService
{
    private readonly IInstrumentRepository _instruments;
    private readonly IMoexIssClient _moex;
    private readonly IMarketQuoteRepository _quotes;
    private readonly BondSyncService _bondSync;

    public InstrumentEnrichmentService(
        IInstrumentRepository instruments,
        IMoexIssClient moex,
        IMarketQuoteRepository quotes,
        BondSyncService bondSync)
    {
        _instruments = instruments;
        _moex = moex;
        _quotes = quotes;
        _bondSync = bondSync;
    }

    /// <summary>
    /// Обогащает справочник + обновляет котировку для ОДНОГО ISIN. Идемпотентно: повторный вызов
    /// с тем же ISIN не заводит новый Instrument (ResolveOrCreateInstrumentByIsinAsync находит
    /// существующий по ISIN), только обновляет котировку/расписания.
    /// </summary>
    /// <returns>Id заведённого/найденного инструмента, null — ISIN не найден на MOEX.</returns>
    public async Task<ulong?> EnrichByIsinAsync(string isin, CancellationToken ct = default)
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

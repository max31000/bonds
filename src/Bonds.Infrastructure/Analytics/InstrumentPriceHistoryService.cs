using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Bonds.Infrastructure.Connectors.Moex;
using Microsoft.Extensions.Logging;

namespace Bonds.Infrastructure.Analytics;

/// <summary>
/// Кэш-обёртка над <see cref="IMoexIssClient.GetHistoryPricesAsync"/> (plan/19 §A) — карточка
/// позиции запрашивает дневную историю цены за произвольный диапазон (1м/6м/1г/всё), но не должна
/// каждый раз ходить в MOEX ISS за одни и те же прошлые дни. Стратегия — "дозагрузить только
/// недостающий хвост": если в <c>instrument_price_history</c> уже есть точки вплоть до даты X,
/// у MOEX запрашивается диапазон [X+1; to], а не весь [from; to] заново (тот же паттерн экономии
/// сетевых вызовов, что <see cref="PortfolioHistoryBackfillService"/> применяет для бэкфилла XIRR,
/// но здесь per-instrument и с персистентным кэшем, а не разовым построением карты в памяти).
/// <para>
/// Устойчивость к неполноте источника (spec §4.4): если у инструмента нет SECID и его не удаётся
/// резолвить по ISIN, либо MOEX вернул пустой ответ — метод не бросает исключение, а возвращает то,
/// что уже есть в кэше (возможно, пустой список).
/// </para>
/// </summary>
public sealed class InstrumentPriceHistoryService
{
    private readonly IInstrumentPriceHistoryRepository _repo;
    private readonly IMoexIssClient _moex;
    private readonly ILogger<InstrumentPriceHistoryService> _logger;

    public InstrumentPriceHistoryService(
        IInstrumentPriceHistoryRepository repo,
        IMoexIssClient moex,
        ILogger<InstrumentPriceHistoryService> logger)
    {
        _repo = repo;
        _moex = moex;
        _logger = logger;
    }

    /// <summary>
    /// Возвращает дневную историю цены за [<paramref name="from"/>; <paramref name="to"/>],
    /// предварительно дозагрузив недостающий хвост из MOEX ISS в кэш.
    /// </summary>
    public async Task<IReadOnlyList<InstrumentPriceHistory>> GetOrRefreshAsync(
        ulong instrumentId, string isin, string? secid, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var latestCached = await _repo.GetLatestDateAsync(instrumentId);

        // Хвост, который нужно дозагрузить: от (последняя закэшированная дата + 1 день) до `to`.
        // Если кэша ещё нет вообще — грузим весь запрошенный диапазон.
        var refreshFrom = latestCached.HasValue && latestCached.Value >= from
            ? latestCached.Value.AddDays(1)
            : from;

        if (refreshFrom <= to)
        {
            var resolvedSecid = secid;
            if (string.IsNullOrEmpty(resolvedSecid))
            {
                resolvedSecid = await _moex.ResolveSecidByIsinAsync(isin, ct);
            }

            if (string.IsNullOrEmpty(resolvedSecid))
            {
                _logger.LogWarning(
                    "PriceHistory: no MOEX secid for instrument {InstrumentId} (ISIN {Isin}) — using cache only",
                    instrumentId, isin);
            }
            else
            {
                var fresh = await _moex.GetHistoryPricesAsync(resolvedSecid, refreshFrom, to, ct);
                if (fresh.Count > 0)
                {
                    var toUpsert = fresh.Select(p => new InstrumentPriceHistory
                    {
                        InstrumentId = instrumentId,
                        Date = p.Date,
                        ClosePricePercent = p.ClosePricePercent,
                        AccruedInterestRub = p.AccruedInterestRub,
                    });
                    await _repo.UpsertManyAsync(instrumentId, toUpsert);
                }
            }
        }

        return await _repo.GetRangeAsync(instrumentId, from, to);
    }
}

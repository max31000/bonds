using Bonds.Core.Analytics;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Bonds.Infrastructure.Connectors.Moex;
using Microsoft.Extensions.Logging;

namespace Bonds.Infrastructure.Analytics;

/// <summary>
/// Оркестратор ретроспективного бэкфилла истории XIRR (plan/15 §B.3-4). Собирает входы для чистого
/// <see cref="PortfolioHistoryRebuildService"/> (журнал операций счёта + карта дневных грязных цен
/// в рублях за каждую бумагу, встречавшуюся в журнале) и upsert'ит результат в
/// <see cref="IPortfolioValueSnapshotRepository"/>.
/// <para>
/// <b>Идемпотентность / "живой снапшот побеждает".</b> Таблица <c>portfolio_value_snapshots</c> не
/// различает источник записи (живой автосинк vs бэкфилл) — уникальный ключ (AccountId, AsOf) просто
/// перезаписывается последним writer'ом. Чтобы бэкфилл никогда не затирал уже существующий снапшот
/// (живой ИЛИ ранее записанный бэкфиллом — предсказуемость повторного запуска), сервис ЗАРАНЕЕ читает
/// уже существующие даты снапшотов и пропускает upsert для дат, которые уже присутствуют — вместо
/// добавления столбца "источник" в схему (минимальное обратимое решение, не меняющее миграции).
/// Повторный вызов бэкфилла — идемпотентен: он лишь дозаполняет дыры в истории, никогда не
/// перезаписывает то, что уже есть.
/// </para>
/// </summary>
public sealed class PortfolioHistoryBackfillService
{
    private readonly IOperationRepository _operations;
    private readonly IInstrumentRepository _instruments;
    private readonly IPortfolioValueSnapshotRepository _snapshots;
    private readonly IMoexIssClient _moex;
    private readonly ILogger<PortfolioHistoryBackfillService> _logger;

    public PortfolioHistoryBackfillService(
        IOperationRepository operations,
        IInstrumentRepository instruments,
        IPortfolioValueSnapshotRepository snapshots,
        IMoexIssClient moex,
        ILogger<PortfolioHistoryBackfillService> logger)
    {
        _operations = operations;
        _instruments = instruments;
        _snapshots = snapshots;
        _moex = moex;
        _logger = logger;
    }

    /// <summary>
    /// Восстанавливает историю портфеля с даты первой операции по <paramref name="asOf"/> и
    /// upsert'ит недостающие точки (даты, для которых снапшота ещё нет — см. doc-comment класса).
    /// Возвращает количество фактически записанных точек (для эндпоинта/логов).
    /// </summary>
    public async Task<int> BackfillAsync(ulong accountId, DateOnly asOf, CancellationToken ct = default)
    {
        var operations = (await _operations.GetByAccountIdAsync(accountId)).ToList();
        if (operations.Count == 0)
        {
            _logger.LogInformation("Backfill skipped for account {AccountId}: no operations", accountId);
            return 0;
        }

        var instrumentIds = operations
            .Where(o => o.InstrumentId is not null)
            .Select(o => o.InstrumentId!.Value)
            .Distinct()
            .ToList();

        var firstDate = DateOnly.FromDateTime(operations.Min(o => o.Date));
        var priceHistory = await BuildPriceHistoryAsync(instrumentIds, firstDate, asOf, ct);

        var points = PortfolioHistoryRebuildService.Rebuild(operations, priceHistory, asOf);
        if (points.Count == 0)
        {
            return 0;
        }

        var existingDates = (await _snapshots.GetByAccountIdAsync(accountId))
            .Select(s => s.AsOf)
            .ToHashSet();

        var written = 0;
        foreach (var point in points)
        {
            ct.ThrowIfCancellationRequested();
            if (existingDates.Contains(point.Date))
            {
                // Уже есть снапшот на эту дату (живой автосинк или предыдущий запуск бэкфилла) —
                // не перезаписываем (plan/15 §B.3 "живой снапшот побеждает").
                continue;
            }

            await _snapshots.UpsertAsync(new PortfolioValueSnapshot
            {
                AccountId = accountId,
                AsOf = point.Date,
                MarketValueRub = point.MarketValueRub,
                XirrToDate = point.Xirr,
                // InvestedRub для бэкфилльных точек не считаем отдельно — эндпоинт GET /api/analytics/xirr
                // использует MarketValueRub/Xirr; 0 здесь не искажает эти два поля (см. plan/15 §C —
                // виджет строит график по XIRR + стоимости, InvestedRub не отображается на нём).
                InvestedRub = 0m,
            });
            written++;
        }

        _logger.LogInformation(
            "Backfill for account {AccountId}: {Written} of {Total} points written (rest already had snapshots)",
            accountId, written, points.Count);

        return written;
    }

    /// <summary>
    /// Собирает разреженную карту instrumentId → (дата → грязная цена в рублях) из MOEX ISS history
    /// для каждого инструмента, встречавшегося в журнале операций. Цена ISS отдаётся в % от номинала
    /// (<see cref="MoexHistoryPricePoint.ClosePricePercent"/>) — переводится в рубли через
    /// <see cref="Instrument.FaceValue"/> текущего справочника (упрощение: не учитывает изменение
    /// номинала во времени при амортизации — приемлемо для приближённой ретроспективы, см. disclaimer
    /// виджета plan/15 §C.2). НКД (<see cref="MoexHistoryPricePoint.AccruedInterestRub"/>) прибавляется
    /// как есть (уже в рублях). Дни без сделки (ClosePricePercent = null) не попадают в карту —
    /// forward fill делает <see cref="PortfolioHistoryRebuildService"/>.
    /// </summary>
    private async Task<IReadOnlyDictionary<ulong, IReadOnlyDictionary<DateOnly, decimal>>> BuildPriceHistoryAsync(
        IReadOnlyList<ulong> instrumentIds, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var result = new Dictionary<ulong, IReadOnlyDictionary<DateOnly, decimal>>();

        foreach (var instrumentId in instrumentIds)
        {
            ct.ThrowIfCancellationRequested();

            var instrument = await _instruments.GetByIdAsync(instrumentId);
            if (instrument is null) continue;

            var secid = instrument.Secid;
            if (string.IsNullOrEmpty(secid))
            {
                secid = await _moex.ResolveSecidByIsinAsync(instrument.Isin, ct);
            }
            if (string.IsNullOrEmpty(secid))
            {
                _logger.LogWarning(
                    "Backfill: no MOEX secid for instrument {InstrumentId} (ISIN {Isin}) — history price unavailable",
                    instrumentId, instrument.Isin);
                continue;
            }

            var history = await _moex.GetHistoryPricesAsync(secid, from, to, ct);

            var series = new Dictionary<DateOnly, decimal>();
            foreach (var point in history)
            {
                if (point.ClosePricePercent is null) continue;

                var dirtyPriceRub = (point.ClosePricePercent.Value / 100m * instrument.FaceValue)
                                     + (point.AccruedInterestRub ?? 0m);
                series[point.Date] = dirtyPriceRub;
            }

            if (series.Count > 0)
            {
                result[instrumentId] = series;
            }
        }

        return result;
    }
}

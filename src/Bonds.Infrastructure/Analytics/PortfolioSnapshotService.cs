using Bonds.Core.Analytics;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Microsoft.Extensions.Logging;

namespace Bonds.Infrastructure.Analytics;

/// <summary>
/// Снимок портфеля "на сейчас" (plan/06 B1, spec §6.9/§9) — координирует репозитории (позиции,
/// котировки, журнал операций) и считает <see cref="PortfolioValueSnapshot"/> через чистый
/// <see cref="PortfolioXirrService"/>. Этот класс НЕ планирует себя по расписанию и НЕ пишет
/// в <see cref="IPortfolioValueSnapshotRepository"/> автоматически — он просто умеет посчитать
/// и (опционально) сохранить один снимок по явному вызову; периодический вызов — забота
/// планировщика этапа 07 (см. doc-comment <see cref="Bonds.Core.Models.PortfolioValueSnapshot"/>).
/// </summary>
public sealed class PortfolioSnapshotService
{
    private readonly IPositionRepository _positions;
    private readonly IMarketQuoteRepository _quotes;
    private readonly IOperationRepository _operations;
    private readonly IPortfolioValueSnapshotRepository _snapshots;
    private readonly ILogger<PortfolioSnapshotService> _logger;

    public PortfolioSnapshotService(
        IPositionRepository positions,
        IMarketQuoteRepository quotes,
        IOperationRepository operations,
        IPortfolioValueSnapshotRepository snapshots,
        ILogger<PortfolioSnapshotService> logger)
    {
        _positions = positions;
        _quotes = quotes;
        _operations = operations;
        _snapshots = snapshots;
        _logger = logger;
    }

    /// <summary>
    /// Считает снимок "на сейчас": текущую рыночную стоимость портфеля (сумма Quantity × DirtyPrice
    /// последней известной котировки по каждой позиции; позиции без котировки исключаются из
    /// суммы стоимости, но не падают расчёт — деградация с пометкой, spec §4.4), XIRR с начала
    /// учёта (по полному журналу операций счёта) и InvestedRub (чистая вложенная сумма: покупки
    /// минус продажи минус погашения/амортизации, т.е. сколько "живого" капитала сейчас сидит
    /// в позициях — см. doc-comment <see cref="CalculateInvestedRub"/>).
    /// </summary>
    public async Task<PortfolioValueSnapshot> ComputeSnapshotAsync(ulong accountId, DateOnly asOf, CancellationToken ct = default)
    {
        var positions = (await _positions.GetByAccountIdAsync(accountId)).ToList();
        var operations = (await _operations.GetByAccountIdAsync(accountId)).ToList();

        decimal marketValue = 0m;
        foreach (var position in positions)
        {
            ct.ThrowIfCancellationRequested();
            var quote = await _quotes.GetLatestAsync(position.InstrumentId);
            if (quote is null)
            {
                _logger.LogWarning(
                    "No quote available for instrument {InstrumentId} (position {PositionId}) — excluded from market value",
                    position.InstrumentId, position.Id);
                continue;
            }

            // DirtyPrice приоритетнее (включает НКД); если его нет — чистая цена + НКД позиции
            // из T-Invest (см. doc-comment Position.Accrued), иначе только чистая цена (деградация).
            var priceWithAccrued = quote.DirtyPrice ?? (quote.CleanPrice + position.Accrued);
            if (priceWithAccrued is null) continue;

            marketValue += priceWithAccrued.Value * position.Quantity;
        }

        var investedRub = CalculateInvestedRub(operations);
        var xirr = PortfolioXirrService.Calculate(operations, marketValue, asOf);

        return new PortfolioValueSnapshot
        {
            AccountId = accountId,
            AsOf = asOf,
            MarketValueRub = marketValue,
            XirrToDate = xirr?.Rate,
            InvestedRub = investedRub,
        };
    }

    /// <summary>Считает снимок и сохраняет его (upsert по (AccountId, AsOf) — повторный вызов в тот же день не дублирует).</summary>
    public async Task<PortfolioValueSnapshot> ComputeAndStoreSnapshotAsync(ulong accountId, DateOnly asOf, CancellationToken ct = default)
    {
        var snapshot = await ComputeSnapshotAsync(accountId, asOf, ct);
        await _snapshots.UpsertAsync(snapshot);
        return snapshot;
    }

    /// <summary>Отдаёт исторический ряд снимков для "кривой доходности портфеля во времени" (spec §9). Пуст до первого запуска планировщика — это ожидаемо.</summary>
    public Task<IEnumerable<PortfolioValueSnapshot>> GetHistoryAsync(ulong accountId, DateOnly? from = null, DateOnly? to = null) =>
        _snapshots.GetByAccountIdAsync(accountId, from, to);

    /// <summary>
    /// "Вложенная" сумма (InvestedRub) — сколько собственного капитала владельца физически сидит
    /// в текущих позициях прямо сейчас, а НЕ накопленный с начала истории объём покупок (тот
    /// был бы переоценён для портфеля, где часть бумаг уже погашена и капитал реинвестирован).
    /// Конвенция (ТРЕБУЕТ СОГЛАСОВАНИЯ С ВЛАДЕЛЬЦЕМ — неочевидный момент, см. финальный отчёт
    /// этапа): InvestedRub = Buy − Sell − Amortization − Redemption (амортизация/погашение
    /// возвращают часть вложенного капитала, уменьшая базу), без учёта купонов/налогов/комиссий
    /// (те не меняют тело вложенного капитала). Может быть отрицательным при отсутствии открытых
    /// позиций (весь капитал возвращён) — это не ошибка, а сигнал "ничего не вложено сейчас".
    /// </summary>
    private static decimal CalculateInvestedRub(IEnumerable<Operation> operations)
    {
        decimal invested = 0m;
        foreach (var op in operations)
        {
            var magnitude = Math.Abs(op.AmountRub);
            invested += op.Type switch
            {
                OperationType.Buy => magnitude,
                OperationType.Sell => -magnitude,
                OperationType.Amortization => -magnitude,
                OperationType.Redemption => -magnitude,
                _ => 0m,
            };
        }

        return invested;
    }
}

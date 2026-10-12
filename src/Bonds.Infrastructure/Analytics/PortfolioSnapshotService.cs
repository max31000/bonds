using Bonds.Core.Analytics;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Microsoft.Extensions.Logging;

namespace Bonds.Infrastructure.Analytics;

/// <summary>
/// Снимок портфеля "на сейчас" (spec §6.9/§9) — координирует репозитории (позиции,
/// котировки, журнал операций) и считает <see cref="PortfolioValueSnapshot"/> через чистый
/// <see cref="PortfolioXirrService"/>. Этот класс НЕ планирует себя по расписанию и НЕ пишет
/// в <see cref="IPortfolioValueSnapshotRepository"/> автоматически — он просто умеет посчитать
/// и (опционально) сохранить один снимок по явному вызову; периодический вызов — забота
/// планировщика этапа 07 (см. doc-comment <see cref="Bonds.Core.Models.PortfolioValueSnapshot"/>).
/// </summary>
public sealed class PortfolioSnapshotService
{
    private readonly PortfolioHoldingsBuilder _holdingsBuilder;
    private readonly IInstrumentRepository _instruments;
    private readonly IOperationRepository _operations;
    private readonly IPortfolioValueSnapshotRepository _snapshots;
    private readonly ILogger<PortfolioSnapshotService> _logger;

    public PortfolioSnapshotService(
        PortfolioHoldingsBuilder holdingsBuilder,
        IInstrumentRepository instruments,
        IOperationRepository operations,
        IPortfolioValueSnapshotRepository snapshots,
        ILogger<PortfolioSnapshotService> logger)
    {
        _holdingsBuilder = holdingsBuilder;
        _instruments = instruments;
        _operations = operations;
        _snapshots = snapshots;
        _logger = logger;
    }

    /// <summary>
    /// Считает снимок "на сейчас": текущую рыночную стоимость портфеля, XIRR с начала учёта
    /// (по полному журналу операций счёта) и InvestedRub (чистая вложенная сумма: покупки
    /// минус продажи минус погашения/амортизации, т.е. сколько "живого" капитала сейчас сидит
    /// в позициях — см. doc-comment <see cref="CalculateInvestedRub"/>).
    /// <para>
    /// Стоимость позиции — <see cref="PortfolioHolding.MarketValueRub"/> из единого
    /// <see cref="PortfolioHoldingsBuilder"/> (грязная цена × количество; fallback-цепочка цены
    /// описана в docs/CODEBASE-GUIDE.md, «MarketValue»). Позиция без котировки получает 0 и
    /// не влияет на сумму (деградация, spec §4.4). Поверх этого — правило погашенных бумаг:
    /// погашенная (Redemption в журнале) не учитывается; с прошедшей датой погашения без выплаты —
    /// оценивается по номиналу остатка.
    /// </para>
    /// </summary>
    public async Task<PortfolioValueSnapshot> ComputeSnapshotAsync(ulong accountId, DateOnly asOf, CancellationToken ct = default)
    {
        var holdings = await _holdingsBuilder.BuildForAccountAsync(accountId, asOf, ct);
        var operations = (await _operations.GetByAccountIdAsync(accountId)).ToList();

        decimal marketValue = 0m;
        foreach (var holding in holdings)
        {
            ct.ThrowIfCancellationRequested();

            // Погашенная бумага не живёт в «текущей» стоимости. У брокера она может провисеть
            // день-два после даты погашения до фактической выплаты.
            if (HasBeenRedeemed(operations, holding.InstrumentId))
            {
                // Деньги уже пришли (Redemption в журнале — приток учтён в XIRR), терминальная
                // стоимость той же бумаги дала бы двойной учёт.
                continue;
            }

            if (!holding.IsOutOfScopeCurrency && holding.MaturityDate < asOf)
            {
                // Дата погашения прошла, выплаты в журнале ещё нет: оцениваем по НОМИНАЛУ остатка
                // (instrument.FaceValue — актуальный номинал с учётом амортизаций, рубли за бумагу),
                // а не по последней котировке, которая после погашения неактуальна.
                var instrument = await _instruments.GetByIdAsync(holding.InstrumentId);
                if (instrument is { FaceValue: > 0m })
                {
                    marketValue += instrument.FaceValue * holding.Quantity;
                    continue;
                }
            }

            if (holding.MarketValueRub <= 0m)
            {
                _logger.LogWarning(
                    "No price available for instrument {InstrumentId} (position {PositionId}) — excluded from market value",
                    holding.InstrumentId, holding.PositionId);
                continue;
            }

            marketValue += holding.MarketValueRub;
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

    /// <summary>
    /// True, если по инструменту в журнале есть полное погашение (<see cref="OperationType.Redemption"/>),
    /// датированное не раньше последней покупки/ввода бумаги (иначе это погашение предыдущего лота).
    /// </summary>
    internal static bool HasBeenRedeemed(IReadOnlyCollection<Operation> operations, ulong instrumentId)
    {
        var forInstrument = operations.Where(o => o.InstrumentId == instrumentId).ToList();
        var lastRedemption = forInstrument.Where(o => o.Type == OperationType.Redemption)
            .Select(o => (DateTime?)o.Date).Max();
        if (lastRedemption is null) return false;

        var lastAcquisition = forInstrument
            .Where(o => o.Type is OperationType.Buy or OperationType.SecurityIn)
            .Select(o => (DateTime?)o.Date).Max();
        return lastAcquisition is null || lastRedemption >= lastAcquisition;
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
    /// <para>
    /// <b>Знак.</b> Использует <see cref="Operation.AmountRub"/> напрямую (брокер уже отдаёт его
    /// со знаком потока — Buy отрицательный, Sell/Amortization/Redemption положительные, см.
    /// doc-comment <see cref="PortfolioXirrService"/>), а не <c>Math.Abs()</c> + переписывание по
    /// типу. Формула "Buy − Sell − Amortization − Redemption" в терминах уже знаковых величин
    /// превращается в "минус сумма знаковых amount по этим четырём типам" — т.е. покупка (которая
    /// хранится отрицательной) даёт положительный вклад в InvestedRub при отрицании, а продажа
    /// (положительная) — отрицательный, что и нужно. Единственный источник истины по знаку — тот
    /// же, что и в <see cref="PortfolioXirrService"/> (согласованность между этими двумя сервисами
    /// исправлена при ревью этапов 04-06 — раньше оба независимо переписывали знак по типу).
    /// </para>
    /// </summary>
    private static decimal CalculateInvestedRub(IEnumerable<Operation> operations)
    {
        decimal invested = 0m;
        foreach (var op in operations)
        {
            invested += op.Type switch
            {
                OperationType.Buy => -op.AmountRub,
                OperationType.Sell => -op.AmountRub,
                OperationType.Amortization => -op.AmountRub,
                OperationType.Redemption => -op.AmountRub,
                _ => 0m,
            };
        }

        return invested;
    }
}

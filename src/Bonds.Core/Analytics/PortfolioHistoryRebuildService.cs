using Bonds.Core.Models;

namespace Bonds.Core.Analytics;

/// <summary>
/// Ретроспективное восстановление истории стоимости/XIRR портфеля (plan/15 §B) — чистый Core-сервис,
/// без I/O. Вход: полный журнал <see cref="Operation"/> счёта + карта дневных грязных цен за бумагу
/// в рублях (<c>instrumentId → (date → dirtyPriceRub)</c>), собранная вызывающим слоем (Infrastructure)
/// из MOEX ISS history + номиналов инструментов. Выход — ряд <see cref="PortfolioHistoryPoint"/> с
/// шагом неделя (плюс последняя точка = <c>asOf</c>), пригодный для upsert в
/// <see cref="PortfolioValueSnapshot"/> (та же математика, что <see cref="PortfolioXirrService"/> —
/// этот сервис переиспользует его для самого расчёта XIRR на каждую контрольную дату).
/// <para>
/// <b>Почему не пересчитывать через <see cref="Bonds.Core.Interfaces.Repositories.IPositionRepository"/>.</b>
/// Текущие Position — это остаток "на сейчас", а не на историческую дату D. Количество бумаги на D
/// восстанавливается прогоном журнала операций (Buy увеличивает, Sell уменьшает) — единственный
/// источник истины о состоянии портфеля в прошлом (журнал операций хранится с датами с самого начала
/// учёта, в отличие от снапшотов, которые пишет только автосинк).
/// </para>
/// </summary>
public static class PortfolioHistoryRebuildService
{
    /// <summary>Шаг ряда — неделя (plan/15 §B.1). Обратимый дефолт: даёт ~52 точки/год — достаточно
    /// плотно для графика, не перегружая бэкфилл ежедневным пересчётом XIRR по всему журналу.</summary>
    public static readonly int StepDays = 7;

    /// <summary>
    /// Строит недельный ряд стоимости/XIRR портфеля от даты первой операции до <paramref name="asOf"/>
    /// включительно (последняя точка ряда — всегда <paramref name="asOf"/>, даже если она не попадает
    /// на недельный шаг от старта — plan/15 §B.1 "плюс последняя точка = сегодня").
    /// </summary>
    /// <param name="operations">Полный журнал операций счёта (не обязательно отсортирован).</param>
    /// <param name="priceHistory">
    /// Карта дневных грязных цен за бумагу в рублях: instrumentId → (дата → цена). Может быть
    /// разреженной — дни без цены (в т.ч. дни без сделок на бирже) не обязаны присутствовать,
    /// сервис сам переносит последнюю известную цену вперёд по каждой бумаге независимо (forward
    /// fill, plan/15 §B.1). Если для бумаги нет цены вообще ни на одну дату ≤ D — она исключается
    /// из суммы стоимости на D, а точка помечается <see cref="PortfolioHistoryPoint.IsApproximate"/>.
    /// </param>
    /// <param name="asOf">Дата последней точки ряда (обычно "сегодня" по MSK, plan/00 BusinessClock).</param>
    public static IReadOnlyList<PortfolioHistoryPoint> Rebuild(
        IEnumerable<Operation> operations,
        IReadOnlyDictionary<ulong, IReadOnlyDictionary<DateOnly, decimal>> priceHistory,
        DateOnly asOf)
    {
        var allOps = operations.OrderBy(o => o.Date).ToList();
        if (allOps.Count == 0)
        {
            return [];
        }

        var firstDate = DateOnly.FromDateTime(allOps[0].Date);
        if (firstDate > asOf)
        {
            return [];
        }

        var checkpoints = BuildCheckpointDates(firstDate, asOf);

        // Кэш "последняя известная цена на дату" за бумагу — переносится между контрольными точками
        // по возрастанию даты, чтобы не пересканировать всю историю цен на каждый чекпойнт (O(n) вместо O(n²)).
        var lastKnownPrice = new Dictionary<ulong, decimal?>();
        var priceCursor = new Dictionary<ulong, int>(); // instrumentId -> индекс следующей непросмотренной отсортированной даты
        var sortedPriceDates = priceHistory.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.OrderBy(p => p.Key).ToList());

        var points = new List<PortfolioHistoryPoint>(checkpoints.Count);
        var opIndex = 0; // указатель на первую ещё не применённую операцию (операции отсортированы)
        var quantities = new Dictionary<ulong, decimal>();

        foreach (var checkpoint in checkpoints)
        {
            // Применяем все операции по дату checkpoint включительно (однократный проход по журналу,
            // операции монотонно возрастают по дате — как и checkpoints).
            while (opIndex < allOps.Count && DateOnly.FromDateTime(allOps[opIndex].Date) <= checkpoint)
            {
                ApplyOperation(quantities, allOps[opIndex]);
                opIndex++;
            }

            // Продвигаем курсор цен каждой бумаги, встречавшейся в портфеле, до последней цены <= checkpoint.
            foreach (var instrumentId in quantities.Keys)
            {
                if (!sortedPriceDates.TryGetValue(instrumentId, out var series)) continue;
                if (!priceCursor.TryGetValue(instrumentId, out var cursor)) cursor = 0;

                while (cursor < series.Count && series[cursor].Key <= checkpoint)
                {
                    lastKnownPrice[instrumentId] = series[cursor].Value;
                    cursor++;
                }
                priceCursor[instrumentId] = cursor;
            }

            decimal marketValue = 0m;
            var approximate = false;

            foreach (var (instrumentId, quantity) in quantities)
            {
                if (quantity == 0m) continue;

                if (!lastKnownPrice.TryGetValue(instrumentId, out var price) || price is null)
                {
                    // Цены нет вообще (ни на checkpoint, ни раньше) — бумагу пропускаем из суммы,
                    // точку помечаем приближённой, но не падаем (plan/15 §B.1).
                    approximate = true;
                    continue;
                }

                marketValue += quantity * price.Value;
            }

            var opsToDate = allOps.Take(opIndex).ToList();
            var xirr = PortfolioXirrService.Calculate(opsToDate, marketValue, checkpoint);

            points.Add(new PortfolioHistoryPoint
            {
                Date = checkpoint,
                MarketValueRub = marketValue,
                Xirr = xirr?.Rate,
                IsApproximate = approximate,
            });
        }

        return points;
    }

    /// <summary>
    /// Контрольные даты: каждые <see cref="StepDays"/> дней от <paramref name="firstDate"/>,
    /// плюс гарантированно <paramref name="asOf"/> последней точкой (даже если она не совпадает
    /// с шагом сетки).
    /// </summary>
    private static List<DateOnly> BuildCheckpointDates(DateOnly firstDate, DateOnly asOf)
    {
        var checkpoints = new List<DateOnly>();
        var current = firstDate;
        while (current < asOf)
        {
            checkpoints.Add(current);
            current = current.AddDays(StepDays);
        }
        checkpoints.Add(asOf);
        return checkpoints;
    }

    private static void ApplyOperation(Dictionary<ulong, decimal> quantities, Operation op)
    {
        if (op.InstrumentId is not { } instrumentId || op.Quantity is not { } qty) return;

        switch (op.Type)
        {
            case OperationType.Buy:
                quantities[instrumentId] = quantities.GetValueOrDefault(instrumentId) + Math.Abs(qty);
                break;
            case OperationType.Sell:
                quantities[instrumentId] = quantities.GetValueOrDefault(instrumentId) - Math.Abs(qty);
                break;
            // Coupon/Amortization/Redemption/Tax/Fee не меняют количество бумаг в портфеле —
            // купон/налог/комиссия не меняют остаток; амортизация/погашение снижают номинал per-unit,
            // не количество штук (учитывается через цену, а не через это состояние).
        }
    }
}

/// <summary>Одна точка ретроспективного ряда стоимости/XIRR портфеля (plan/15 §B.1).</summary>
public sealed record PortfolioHistoryPoint
{
    public required DateOnly Date { get; init; }
    public required decimal MarketValueRub { get; init; }
    public decimal? Xirr { get; init; }

    /// <summary>True — хотя бы одна бумага в портфеле на эту дату не имела исторической цены нигде
    /// в доступном диапазоне (ни на дату, ни раньше) и была исключена из суммы стоимости —
    /// точка занижена/приближена (plan/15 §B.1, аналог DataIncomplete в остальном коде).</summary>
    public required bool IsApproximate { get; init; }
}

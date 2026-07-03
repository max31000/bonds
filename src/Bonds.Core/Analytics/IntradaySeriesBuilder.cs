using Bonds.Core.Models;

namespace Bonds.Core.Analytics;

/// <summary>Одна точка интрадей-ряда суммарной стоимости портфеля (GET /api/live/portfolio-intraday, plan/16).</summary>
public sealed record IntradaySeriesPoint
{
    public required DateTime TsUtc { get; init; }
    public required decimal TotalMarketValueRub { get; init; }
}

/// <summary>
/// Чистый сервис (plan/16 часть A) — собирает суммарный ряд стоимости портфеля из разреженных
/// тиков нескольких инструментов (<see cref="IntradayQuote"/>). Каждый инструмент опрашивается
/// независимо тем же <c>LiveQuotesPollingService</c> тиком, но не обязательно синхронно (сбой
/// сети на одном FIGI, разное время ответа) — на выходе один общий ряд по объединённым моментам
/// времени всех тиков, где для каждого инструмента берётся forward-fill (последняя известная
/// цена на момент времени или раньше). Позиции без цены строго ДО первого своего тика не
/// участвуют в сумме на этом моменте (нет данных — не нуль), а не тянут сумму к заниженному
/// значению.
/// </summary>
public static class IntradaySeriesBuilder
{
    /// <summary>
    /// Строит ряд (tsUtc, суммарная стоимость = Σ quantity_i × forward-fill(price_i, ts)) по всем
    /// моментам времени, встречающимся хотя бы в одном тике любого инструмента. Пустой вход →
    /// пустой ряд. Инструмент без единого тика в диапазоне не участвует в сумме нигде — не ошибка,
    /// его позиция просто не отражена на интрадей-графике (та же деградация, что market_quotes:
    /// нет данных — не подставляем 0 молча).
    /// </summary>
    /// <param name="quotesByInstrument">Тики по каждому инструменту, в любом порядке внутри списка.</param>
    /// <param name="quantityByInstrument">Количество облигаций на инструмент (для перевода цены за бумагу в рыночную стоимость позиции).</param>
    public static IReadOnlyList<IntradaySeriesPoint> Build(
        IReadOnlyDictionary<ulong, IReadOnlyList<IntradayQuote>> quotesByInstrument,
        IReadOnlyDictionary<ulong, decimal> quantityByInstrument)
    {
        // Отсортированные тики на инструмент — нужны для forward-fill бинарным/линейным проходом.
        var sortedByInstrument = quotesByInstrument.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.OrderBy(q => q.TsUtc).ToList());

        var allTimestamps = sortedByInstrument.Values
            .SelectMany(list => list.Select(q => q.TsUtc))
            .Distinct()
            .OrderBy(ts => ts)
            .ToList();

        if (allTimestamps.Count == 0) return [];

        var points = new List<IntradaySeriesPoint>(allTimestamps.Count);

        // Индекс текущей позиции forward-fill на инструмент — двигается монотонно вперёд по мере
        // роста ts (весь список меток времени отсортирован), O(N+M) суммарно вместо O(N×M) бинарного поиска.
        var cursor = sortedByInstrument.Keys.ToDictionary(id => id, _ => -1);

        foreach (var ts in allTimestamps)
        {
            decimal total = 0m;

            foreach (var (instrumentId, ticks) in sortedByInstrument)
            {
                var idx = cursor[instrumentId];
                // Продвигаем курсор, пока следующий тик всё ещё <= ts (forward-fill: берём
                // последний тик с TsUtc <= ts, а не только точное совпадение).
                while (idx + 1 < ticks.Count && ticks[idx + 1].TsUtc <= ts)
                {
                    idx++;
                }
                cursor[instrumentId] = idx;

                if (idx < 0) continue; // ещё не было ни одного тика этого инструмента к этому моменту — не участвует в сумме

                var quantity = quantityByInstrument.GetValueOrDefault(instrumentId, 0m);
                total += ticks[idx].DirtyPriceRub * quantity;
            }

            points.Add(new IntradaySeriesPoint { TsUtc = ts, TotalMarketValueRub = total });
        }

        return points;
    }
}

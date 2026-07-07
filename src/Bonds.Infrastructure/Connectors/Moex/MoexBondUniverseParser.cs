using System.Text.Json;

namespace Bonds.Infrastructure.Connectors.Moex;

/// <summary>
/// Разбор одной страницы ISS-ответа <c>/iss/engines/stock/markets/bonds/securities.json?iss.only=securities,marketdata</c>
/// (задача 26 часть A) — снимок всей рыночной вселенной облигаций одним-двумя запросами. Сливает
/// строки блоков <c>securities</c> и <c>marketdata</c> по ключу (SECID, BOARDID) — они возвращаются
/// как отдельные параллельные таблицы, не вложенные структуры (то же семейство ответов ISS, что
/// <see cref="MoexSecuritiesParser"/>, но без выбора "лучшего" board — то делает вызывающий код
/// после накопления всех страниц, см. <see cref="MoexIssClient.GetBondMarketSnapshotAsync"/>).
/// Устойчив к отсутствию marketdata для конкретной строки (бумага без сделок сегодня) — в этом
/// случае поля marketdata остаются null, не бросаем исключение.
/// </summary>
public static class MoexBondUniverseParser
{
    /// <summary>
    /// Разбирает одну страницу ответа. Возвращает пустой список, если блок <c>securities</c>
    /// отсутствует/пуст (не бросает исключение — устойчивость к неполноте источника, §4.4).
    /// </summary>
    public static List<MoexBondMarketRow> ParsePage(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var securitiesTable = IssTable.Parse(root, "securities");
        if (securitiesTable is null || securitiesTable.RowCount == 0)
        {
            return [];
        }

        var marketDataTable = IssTable.Parse(root, "marketdata");
        var marketDataByKey = new Dictionary<(string Secid, string BoardId), IssRow>();
        if (marketDataTable is not null)
        {
            foreach (var row in marketDataTable.Rows())
            {
                var secid = row.GetString("SECID");
                var boardId = row.GetString("BOARDID");
                if (secid is null || boardId is null) continue;
                // Дубликаты ключа в marketdata не ожидаются — берём первую встреченную строку.
                marketDataByKey.TryAdd((secid, boardId), row);
            }
        }

        // IssRow — readonly struct поверх Dictionary/JsonElement[]; default(IssRow) хранил бы null
        // внутри и уронил бы Get() с NullReferenceException при обращении к отсутствующей marketdata.
        // Nullable<IssRow> явно моделирует "строки marketdata для этого SECID/BOARDID нет".

        var result = new List<MoexBondMarketRow>(securitiesTable.RowCount);
        foreach (var row in securitiesTable.Rows())
        {
            var secid = row.GetString("SECID");
            var boardId = row.GetString("BOARDID");
            if (secid is null || boardId is null) continue; // строка без идентификатора бесполезна — пропускаем.

            var hasMd = marketDataByKey.TryGetValue((secid, boardId), out var md);

            result.Add(new MoexBondMarketRow
            {
                Secid = secid,
                BoardId = boardId,
                Isin = row.GetString("ISIN"),
                ShortName = row.GetString("SHORTNAME"),
                SecName = row.GetString("SECNAME"),
                FaceValue = row.GetDecimal("FACEVALUE"),
                LotValue = row.GetDecimal("LOTVALUE"),
                FaceUnit = row.GetString("FACEUNIT"),
                CouponPercent = row.GetDecimal("COUPONPERCENT"),
                CouponPeriod = row.GetInt("COUPONPERIOD"),
                MatDate = row.GetDateOnly("MATDATE"),
                OfferDate = row.GetDateOnly("OFFERDATE"),
                ListLevel = row.GetInt("LISTLEVEL"),
                SecType = row.GetString("SECTYPE"),
                BondType = row.GetString("BONDTYPE"),
                Status = row.GetString("STATUS"),

                YieldPercent = hasMd ? md.GetDecimal("YIELD") : null,
                DurationDays = hasMd ? md.GetInt("DURATION") : null,
                PricePercent = hasMd ? md.GetDecimal("LAST") ?? md.GetDecimal("MARKETPRICE") : null,
                TurnoverRub = hasMd ? md.GetDecimal("VALTODAY") : null,
                BidPercent = hasMd ? md.GetDecimal("BID") : null,
                OfferPricePercent = hasMd ? md.GetDecimal("OFFER") : null,
                NumTrades = hasMd ? md.GetInt("NUMTRADES") : null,
            });
        }

        return result;
    }

    /// <summary>
    /// Дедупликация по SECID: одна бумага может торговаться на нескольких режимах (board) —
    /// оставляем строку с максимальным оборотом (<see cref="MoexBondMarketRow.TurnoverRub"/>);
    /// при равенстве/отсутствии оборота — фолбэк на приоритет "торгового" режима, тот же список,
    /// что <see cref="MoexSecuritiesParser"/> (TQOB/TQCB/TQIR/TQRD), иначе первая встреченная строка.
    /// </summary>
    public static List<MoexBondMarketRow> DeduplicateByBoard(IEnumerable<MoexBondMarketRow> rows)
    {
        var byBoardOrder = new[] { "TQOB", "TQCB", "TQIR", "TQRD" };

        return rows
            .GroupBy(r => r.Secid)
            .Select(g => g
                .OrderByDescending(r => r.TurnoverRub ?? -1m)
                .ThenBy(r => PreferredBoardScore(r.BoardId, byBoardOrder))
                .First())
            .ToList();
    }

    private static int PreferredBoardScore(string boardId, string[] order)
    {
        var idx = Array.IndexOf(order, boardId);
        return idx >= 0 ? idx : order.Length;
    }
}

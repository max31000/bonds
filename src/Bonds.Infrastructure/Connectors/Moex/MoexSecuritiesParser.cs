using System.Text.Json;

namespace Bonds.Infrastructure.Connectors.Moex;

/// <summary>
/// Парсер ответа MOEX ISS <c>/iss/engines/stock/markets/bonds/securities/{SECID}.json</c>
/// (параметры выпуска + текущие цены, plan/04 Часть A). Один SECID может вернуть несколько строк —
/// по одной на каждый BOARDID, где бумага торгуется/торговалась. Выбираем наиболее "торговый" board.
/// </summary>
public static class MoexSecuritiesParser
{
    /// <summary>Приоритет основных режимов торгов облигациями на MOEX (выше = предпочтительнее).
    /// TQOB/TQCB — основной режим Т+ для гособлигаций/корпоративных; прочие (внесистемные,
    /// режимы расчётов) — fallback.</summary>
    private static readonly string[] PreferredBoardOrder = ["TQOB", "TQCB", "TQIR", "TQRD"];

    public static MoexSecurityInfo? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var table = IssTable.Parse(root, "securities");
        if (table is null || table.RowCount == 0) return null;

        var rows = table.Rows().ToList();
        var best = rows
            .OrderBy(r => PreferredBoardScore(r.GetString("BOARDID")))
            .First();

        return new MoexSecurityInfo
        {
            Secid = best.GetString("SECID") ?? string.Empty,
            BoardId = best.GetString("BOARDID") ?? string.Empty,
            Isin = best.GetString("ISIN"),
            ShortName = best.GetString("SHORTNAME"),
            SecName = best.GetString("SECNAME"),
            FaceValue = best.GetDecimal("FACEVALUE"),
            FaceUnit = best.GetString("FACEUNIT"),
            MatDate = best.GetDateOnly("MATDATE"),
            CouponPercent = best.GetDecimal("COUPONPERCENT"),
            CouponPeriod = best.GetInt("COUPONPERIOD"),
            NextCoupon = best.GetDateOnly("NEXTCOUPON"),
            AccruedInterest = best.GetDecimal("ACCRUEDINT"),
            PrevPrice = best.GetDecimal("PREVPRICE"),
            PrevWaPrice = best.GetDecimal("PREVWAPRICE"),
            BondType = best.GetString("BONDTYPE"),
            OfferDate = best.GetDateOnly("OFFERDATE"),
            CallOptionDate = best.GetDateOnly("CALLOPTIONDATE"),
            PutOptionDate = best.GetDateOnly("PUTOPTIONDATE"),
        };
    }

    private static int PreferredBoardScore(string? boardId)
    {
        if (boardId is null) return PreferredBoardOrder.Length;
        var idx = Array.IndexOf(PreferredBoardOrder, boardId);
        return idx >= 0 ? idx : PreferredBoardOrder.Length;
    }

    /// <summary>
    /// Результат поиска <c>/iss/securities.json?q=...</c> по ISIN — резолвер ISIN→SECID
    /// (plan/04 Часть A, задача 1). Возвращает первую бумагу из группы "stock_bonds" (отфильтровывая
    /// индексы/прочие инструменты, которые ISS иногда подмешивает в полнотекстовый поиск).
    /// </summary>
    public static string? ParseSecidFromSearch(string json, string isin)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var table = IssTable.Parse(root, "securities");
        if (table is null) return null;

        foreach (var row in table.Rows())
        {
            var rowIsin = row.GetString("isin");
            var group = row.GetString("group");
            if (string.Equals(rowIsin, isin, StringComparison.OrdinalIgnoreCase)
                && string.Equals(group, "stock_bonds", StringComparison.OrdinalIgnoreCase))
            {
                return row.GetString("secid");
            }
        }

        return null;
    }

    public static MoexSecuritySearch? ParseSearchInfo(string json, string isin)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var table = IssTable.Parse(root, "securities");
        if (table is null) return null;

        foreach (var row in table.Rows())
        {
            var rowIsin = row.GetString("isin");
            var group = row.GetString("group");
            if (string.Equals(rowIsin, isin, StringComparison.OrdinalIgnoreCase)
                && string.Equals(group, "stock_bonds", StringComparison.OrdinalIgnoreCase))
            {
                return new MoexSecuritySearch
                {
                    Secid = row.GetString("secid"),
                    EmitentTitle = row.GetString("emitent_title"),
                    TypeCode = row.GetString("type"),
                };
            }
        }

        return null;
    }
}

using System.Text.Json;
using Bonds.Core.Models;

namespace Bonds.Infrastructure.Connectors.Moex;

/// <summary>
/// Парсер ответа MOEX ISS <c>/iss/engines/stock/zcyc/securities.json</c> — параметры кривой
/// Нельсона-Сигеля-Свенссона безрисковой кривой (Gcurve/КБД), блок "params" (plan/04 Часть A,
/// §4.2). Название "MOEX GCURVE" — товарный знак (spec §4.3), в коде используем нейтральное
/// "YieldCurve"/"безрисковая кривая".
/// </summary>
public static class MoexGcurveParser
{
    public static YieldCurveSnapshot? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var table = IssTable.Parse(root, "params");
        if (table is null || table.RowCount == 0) return null;

        var row = table.Rows().First();
        var asOf = row.GetDateOnly("tradedate");
        if (asOf is null) return null;

        // B1/B2/B3/T1/G1..G9 — обязательные параметры NSS-модели; если хотя бы один не пришёл,
        // снимок бесполезен для реконструкции кривой (§6 G-спред) — не сохраняем частичный снимок
        // молча, отдаём null и даём вызывающему коду решить, как это залогировать/обработать.
        decimal? b1 = row.GetDecimal("B1"), b2 = row.GetDecimal("B2"), b3 = row.GetDecimal("B3"), t1 = row.GetDecimal("T1");
        decimal?[] g = Enumerable.Range(1, 9).Select(i => row.GetDecimal($"G{i}")).ToArray();

        if (b1 is null || b2 is null || b3 is null || t1 is null || g.Any(v => v is null))
        {
            return null;
        }

        return new YieldCurveSnapshot
        {
            AsOf = asOf.Value,
            B1 = b1.Value,
            B2 = b2.Value,
            B3 = b3.Value,
            T1 = t1.Value,
            G1 = g[0]!.Value,
            G2 = g[1]!.Value,
            G3 = g[2]!.Value,
            G4 = g[3]!.Value,
            G5 = g[4]!.Value,
            G6 = g[5]!.Value,
            G7 = g[6]!.Value,
            G8 = g[7]!.Value,
            G9 = g[8]!.Value,
        };
    }
}

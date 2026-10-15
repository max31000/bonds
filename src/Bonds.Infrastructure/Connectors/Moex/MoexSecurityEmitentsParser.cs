using System.Text.Json;

namespace Bonds.Infrastructure.Connectors.Moex;

/// <summary>
/// Разбор одной страницы bulk-справочника ISS (<c>securities</c> с колонками
/// secid/emitent_id/emitent_title/emitent_inn). Маппинг по ИМЕНИ колонки (порядок колонок ISS не
/// гарантирован). Пустой блок/отсутствие блока — пустой список, а не исключение (§4.4 устойчивость).
/// </summary>
public static class MoexSecurityEmitentsParser
{
    public static List<MoexSecurityEmitent> ParsePage(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var table = IssTable.Parse(doc.RootElement, "securities");
        if (table is null || table.RowCount == 0)
        {
            return [];
        }

        var result = new List<MoexSecurityEmitent>(table.RowCount);
        foreach (var row in table.Rows())
        {
            var secid = row.GetString("secid");
            if (string.IsNullOrWhiteSpace(secid)) continue; // строка без идентификатора бесполезна.

            result.Add(new MoexSecurityEmitent(
                secid,
                row.GetInt("emitent_id"),
                NullIfBlank(row.GetString("emitent_title")),
                NullIfBlank(row.GetString("emitent_inn"))));
        }

        return result;
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

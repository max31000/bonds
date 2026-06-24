using System.Text.Json;

namespace Bonds.Infrastructure.Connectors.Moex;

/// <summary>
/// Универсальный разбор одной "таблицы" формата MOEX ISS: объект с полями
/// <c>columns</c> (массив имён колонок) и <c>data</c> (массив строк-массивов).
/// Все парсеры этого коннектора маппят значения ПО ИМЕНИ колонки, а не по индексу
/// (plan/04 Часть A, задача 2: "писать устойчивые мапперы по именам колонок, не по индексам") —
/// порядок колонок в ответах ISS не гарантирован и менялся между версиями API.
/// </summary>
public sealed class IssTable
{
    private readonly Dictionary<string, int> _columnIndex;
    private readonly List<JsonElement[]> _rows;

    private IssTable(Dictionary<string, int> columnIndex, List<JsonElement[]> rows)
    {
        _columnIndex = columnIndex;
        _rows = rows;
    }

    public int RowCount => _rows.Count;

    /// <summary>
    /// Разбирает один блок ISS-таблицы (объект с columns/data) из родительского JSON-документа.
    /// Возвращает null, если блок с данным именем отсутствует в ответе (ISS иногда не возвращает
    /// блок целиком, например, "amortizations" для бумаги без амортизации).
    /// </summary>
    public static IssTable? Parse(JsonElement root, string blockName)
    {
        if (!root.TryGetProperty(blockName, out var block))
        {
            return null;
        }

        if (!block.TryGetProperty("columns", out var columnsEl) || !block.TryGetProperty("data", out var dataEl))
        {
            return null;
        }

        var columnIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var i = 0;
        foreach (var col in columnsEl.EnumerateArray())
        {
            columnIndex[col.GetString() ?? string.Empty] = i++;
        }

        var rows = new List<JsonElement[]>();
        foreach (var rowEl in dataEl.EnumerateArray())
        {
            rows.Add(rowEl.EnumerateArray().ToArray());
        }

        return new IssTable(columnIndex, rows);
    }

    public IEnumerable<IssRow> Rows()
    {
        foreach (var row in _rows)
        {
            yield return new IssRow(_columnIndex, row);
        }
    }
}

/// <summary>Одна строка ISS-таблицы с доступом к значениям по имени колонки.</summary>
public readonly struct IssRow
{
    private readonly Dictionary<string, int> _columnIndex;
    private readonly JsonElement[] _values;

    public IssRow(Dictionary<string, int> columnIndex, JsonElement[] values)
    {
        _columnIndex = columnIndex;
        _values = values;
    }

    private JsonElement? Get(string column)
    {
        if (!_columnIndex.TryGetValue(column, out var idx) || idx >= _values.Length)
        {
            return null;
        }

        var el = _values[idx];
        return el.ValueKind == JsonValueKind.Null ? null : el;
    }

    public string? GetString(string column) => Get(column)?.GetString();

    public decimal? GetDecimal(string column)
    {
        var el = Get(column);
        if (el is null) return null;
        return el.Value.ValueKind switch
        {
            JsonValueKind.Number => el.Value.GetDecimal(),
            JsonValueKind.String when decimal.TryParse(el.Value.GetString(), System.Globalization.CultureInfo.InvariantCulture, out var d) => d,
            _ => null,
        };
    }

    public int? GetInt(string column)
    {
        var el = Get(column);
        if (el is null) return null;
        return el.Value.ValueKind switch
        {
            JsonValueKind.Number => el.Value.GetInt32(),
            JsonValueKind.String when int.TryParse(el.Value.GetString(), out var v) => v,
            _ => null,
        };
    }

    public DateOnly? GetDateOnly(string column)
    {
        var s = GetString(column);
        if (string.IsNullOrWhiteSpace(s) || s == "0000-00-00") return null;
        return DateOnly.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    public bool? GetBool(string column)
    {
        var el = Get(column);
        if (el is null) return null;
        return el.Value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => el.Value.GetInt32() != 0,
            _ => null,
        };
    }
}

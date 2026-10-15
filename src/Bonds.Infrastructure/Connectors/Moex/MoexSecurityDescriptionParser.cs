using System.Globalization;
using System.Text.Json;

namespace Bonds.Infrastructure.Connectors.Moex;

/// <summary>
/// Разбор карточки бумаги ISS (блок <c>description</c>: строки name/title/value). В отличие от
/// остальных блоков ISS здесь одна СТРОКА = одно свойство, поэтому берём словарь name → value.
/// Пустые/нераспознанные значения → null (не исключение). Отсутствие блока, пустой блок или
/// отсутствие SECID — null (ISS отдаёт пустую карточку для неизвестного SECID).
/// </summary>
public static class MoexSecurityDescriptionParser
{
    public static MoexSecurityDescription? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var table = IssTable.Parse(doc.RootElement, "description");
        if (table is null || table.RowCount == 0)
        {
            return null;
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in table.Rows())
        {
            var name = row.GetString("name");
            var value = row.GetString("value");
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(value)) continue;
            values.TryAdd(name, value.Trim());
        }

        if (!values.TryGetValue("SECID", out var secid))
        {
            return null;
        }

        return new MoexSecurityDescription
        {
            Secid = secid,
            Isin = values.GetValueOrDefault("ISIN"),
            EmitterId = ParseInt(values.GetValueOrDefault("EMITTER_ID")),
            HasDefault = ParseFlag(values.GetValueOrDefault("HASDEFAULT")),
            HasTechnicalDefault = ParseFlag(values.GetValueOrDefault("HASTECHNICALDEFAULT")),
            IsQualifiedInvestors = ParseFlag(values.GetValueOrDefault("ISQUALIFIEDINVESTORS")),
            CouponBenchmark = values.GetValueOrDefault("COUPON_BENCHMARK"),
            // COUPON_BENCHMARK_SPREAD: проценты (например "1.90") → ДОЛЯ (0.019).
            CouponBenchmarkSpreadFraction = ParseDecimal(values.GetValueOrDefault("COUPON_BENCHMARK_SPREAD")) / 100m,
            EarlyRepayment = ParseFlag(values.GetValueOrDefault("EARLYREPAYMENT")),
        };
    }

    private static bool? ParseFlag(string? raw) => raw switch
    {
        "1" => true,
        "0" => false,
        _ => null,
    };

    private static int? ParseInt(string? raw) =>
        int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static decimal? ParseDecimal(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return decimal.TryParse(raw.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : null;
    }
}

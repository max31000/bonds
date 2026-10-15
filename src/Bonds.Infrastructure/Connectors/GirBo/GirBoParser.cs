using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bonds.Core.Models;
using Microsoft.Extensions.Logging;

namespace Bonds.Infrastructure.Connectors.GirBo;

/// <summary>
/// Разбор JSON-ответов ГИР БО (bo.nalog.gov.ru). Чистые функции без сети.
/// <para>
/// <b>Единицы:</b> ГИР БО отдаёт суммы бухгалтерской отчётности в ТЫСЯЧАХ рублей (проверено на живых
/// данных: Лента 2021, 2110 = 495 877 663 ≈ 495,9 млрд ₽). В <see cref="IssuerFinancials"/> и БД — РУБЛИ:
/// конверсия <see cref="ThousandsToRubles"/> применяется здесь, один раз, ко всем денежным строкам.
/// </para>
/// </summary>
public static class GirBoParser
{
    /// <summary>Тысячи рублей (вход ГИР БО) → рубли (модель/БД): множитель ×1000.</summary>
    public const decimal ThousandsToRubles = 1000m;

    /// <summary><c>periodType</c> годовой отчётности в подаче (12 месяцев); остальные — промежуточные.</summary>
    public const int AnnualPeriodType = 12;

    /// <summary>Нижняя граница правдоподобного отчётного года (ГИР БО — российская отчётность 1990+).</summary>
    public const int MinReportingYear = 1990;

    /// <summary>Верхняя граница отчётного года (потолок <see cref="DateOnly"/>); мусор вроде 20245 отсекается.</summary>
    public const int MaxReportingYear = 9999;

    /// <summary>Отчётный год в правдоподобном диапазоне [<see cref="MinReportingYear"/>, <see cref="MaxReportingYear"/>]:
    /// мусорный год из внешнего JSON иначе дошёл бы до <c>new DateOnly(year, 12, 31)</c> скоркарты и уронил расчёт.</summary>
    public static bool IsPlausibleYear(int year) => year is >= MinReportingYear and <= MaxReportingYear;

    private static readonly Regex HtmlTag = new("<[^>]+>", RegexOptions.Compiled);

    /// <summary>Префиксы колонок трёх лет подачи: смещение назад от отчётного года → префикс ключа.</summary>
    private static readonly string[] YearPrefixes = ["current", "previous", "beforePrevious"];

    /// <summary>
    /// Поиск по ИНН (<c>/advanced-search/organizations/search</c>). ИНН в выдаче обёрнут в
    /// <c>&lt;strong&gt;</c> (подсветка совпадения) — разметка срезается, сравнение строго по полному ИНН
    /// (нечёткие попадания игнорируются). При нескольких записях: действующая → с самой свежей подачей.
    /// Null — организации нет.
    /// </summary>
    public static GirBoOrganization? ParseSearch(string json, string inn)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return content.EnumerateArray()
            .Select(item => (item, inn: StripMarkup(GetString(item, "inn"))))
            .Where(x => x.inn == inn && x.item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number)
            .Select(x =>
            {
                var bfo = x.item.TryGetProperty("bfo", out var b) && b.ValueKind == JsonValueKind.Object ? (JsonElement?)b : null;
                var active = GetString(x.item, "statusCode") == "ACTIVE";
                var period = bfo is { } bb && int.TryParse(GetString(bb, "period"), out var p) ? p : int.MinValue;
                bool? isCb = bfo is { } b2 && b2.TryGetProperty("isCb", out var cb) && cb.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? cb.GetBoolean()
                    : null;
                return (Org: new GirBoOrganization(x.item.GetProperty("id").GetInt64(), x.inn, GetString(x.item, "shortName"), isCb),
                        active, period);
            })
            .OrderByDescending(x => x.active)
            .ThenByDescending(x => x.period)
            .Select(x => x.Org)
            .FirstOrDefault();
    }

    /// <summary>Список подач организации (<c>/nbo/organizations/{id}/bfo/</c>); записи без числового года или с
    /// неправдоподобным годом (<see cref="IsPlausibleYear"/>) пропускаются, второе — с предупреждением в <paramref name="logger"/>.</summary>
    public static IReadOnlyList<GirBoFiling> ParseFilings(string json, ILogger? logger = null)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];

        var result = new List<GirBoFiling>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number
                || !int.TryParse(GetString(item, "period"), NumberStyles.None, CultureInfo.InvariantCulture, out var period))
            {
                continue;
            }

            if (!IsPlausibleYear(period))
            {
                logger?.LogWarning("ГИР БО: подача {FilingId} пропущена — неправдоподобный отчётный год {Period}", id.GetInt64(), period);
                continue;
            }

            var isCb = item.TryGetProperty("isCb", out var cb) && cb.ValueKind == JsonValueKind.True;
            result.Add(new GirBoFiling(id.GetInt64(), period, isCb));
        }

        return result;
    }

    /// <summary>
    /// Годовые записи из <c>/nbo/bfo/{bfoId}/details</c> (массив корректировок подачи за год
    /// <paramref name="period"/>). Берём только годовые корректировки (<c>periodType</c> = 12) и среди них
    /// с максимальным <c>correctionVersion</c>. Из одной подачи формируется до трёх годовых записей
    /// (current → period, previous → period−1, beforePrevious → period−2): так одна свежая подача даёт тренд.
    /// Год без единой ключевой цифры (ни баланса 1600, ни выручки 2110) пропускается. Разбираются только
    /// нужные строки; форма 2 (ОФР) содержит лишь current/previous — у третьего года ОФР-поля null.
    /// Все суммы ×<see cref="ThousandsToRubles"/>. Пустой список — годовых данных нет. Год вне диапазона
    /// <see cref="IsPlausibleYear"/> (сам <paramref name="period"/> или период−1/−2) пропускается с предупреждением в <paramref name="logger"/>.
    /// </summary>
    public static IReadOnlyList<IssuerFinancials> ParseAnnualFinancials(
        string json, string inn, int period, DateTime fetchedAtUtc, ILogger? logger = null)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];

        JsonElement? best = null;
        var bestVersion = int.MinValue;
        foreach (var correction in doc.RootElement.EnumerateArray())
        {
            if (correction.ValueKind != JsonValueKind.Object
                || !correction.TryGetProperty("periodType", out var pt) || pt.ValueKind != JsonValueKind.Number
                || pt.GetInt32() != AnnualPeriodType)
            {
                continue;
            }

            var version = correction.TryGetProperty("correctionVersion", out var v) && v.ValueKind == JsonValueKind.Number
                ? v.GetInt32()
                : 0;
            if (version > bestVersion)
            {
                best = correction;
                bestVersion = version;
            }
        }

        if (best is not { } chosen) return [];

        chosen.TryGetProperty("balance", out var balance);
        chosen.TryGetProperty("financialResult", out var result);
        var hasAudit = chosen.TryGetProperty("auditReport", out var audit) && audit.ValueKind == JsonValueKind.Object;

        var rows = new List<IssuerFinancials>(YearPrefixes.Length);
        for (var offset = 0; offset < YearPrefixes.Length; offset++)
        {
            var prefix = YearPrefixes[offset];
            if (!IsPlausibleYear(period - offset))
            {
                logger?.LogWarning("ГИР БО: отчётность ИНН {Inn} за {Period} пропущена — неправдоподобный отчётный год", inn, period - offset);
                continue;
            }

            var row = new IssuerFinancials
            {
                Inn = inn,
                Period = period - offset,
                Source = IssuerFinancialsSources.GirBo,
                FetchedAt = fetchedAtUtc,
                Revenue = Line(result, prefix, "2110"),
                OperatingProfit = Line(result, prefix, "2200"),
                InterestPaid = Line(result, prefix, "2330"),
                NetProfit = Line(result, prefix, "2400"),
                Equity = Line(balance, prefix, "1300"),
                TotalAssets = Line(balance, prefix, "1600"),
                CurrentAssets = Line(balance, prefix, "1200"),
                CurrentLiabilities = Line(balance, prefix, "1500"),
                Cash = Line(balance, prefix, "1250"),
                LongDebt = Line(balance, prefix, "1410"),
                ShortDebt = Line(balance, prefix, "1510"),
                RetainedEarnings = Line(balance, prefix, "1370"),
                // Заключение аудитора относится к отчётному году подачи; для прошлых лет — неизвестно.
                HasAudit = offset == 0 ? hasAudit : null,
            };

            if (row.TotalAssets is null && row.Revenue is null) continue;
            rows.Add(row);
        }

        return rows;
    }

    /// <summary>Значение строки формы (<c>{prefix}{code}</c>) в РУБЛЯХ: тыс. ₽ × <see cref="ThousandsToRubles"/>; null — нет строки/значения.</summary>
    private static decimal? Line(JsonElement form, string prefix, string code)
    {
        if (form.ValueKind != JsonValueKind.Object
            || !form.TryGetProperty(prefix + code, out var value)
            || value.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        return value.TryGetDecimal(out var thousands) ? thousands * ThousandsToRubles : null;
    }

    private static string? GetString(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;

    private static string StripMarkup(string? value) => value is null ? string.Empty : HtmlTag.Replace(value, string.Empty).Trim();
}

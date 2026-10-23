using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace Bonds.Infrastructure.Connectors.Cbr;

/// <summary>Официальный курс ЦБ РФ на дату: <see cref="RubPerUnit"/> — РУБЛИ ЗА 1 ЕДИНИЦУ валюты (уже поделено на номинал).</summary>
public sealed record CbrRatePoint(DateOnly Date, decimal RubPerUnit);

/// <summary>Источник официальных курсов ЦБ РФ (задача 60): запасной/основной источник для USD/EUR/GBP, которые на MOEX CETS не торгуются.</summary>
public interface ICbrRatesClient
{
    /// <summary>Курсы валюты <paramref name="cbrId"/> (код справочника ЦБ, напр. <c>R01239</c> = EUR) за период. Пусто — данных нет.</summary>
    Task<IReadOnlyList<CbrRatePoint>> GetRatesAsync(string cbrId, DateOnly from, DateOnly to, CancellationToken ct = default);
}

/// <summary>
/// <c>GET https://www.cbr.ru/scripts/XML_dynamic.asp?date_req1=dd/MM/yyyy&amp;date_req2=dd/MM/yyyy&amp;VAL_NM_RQ={id}</c>. Ответ — XML в windows-1251
/// с десятичной ЗАПЯТОЙ. Кодировка не декодируется (в разметке и числах только ASCII): читаем байты как Latin-1 и снимаем XML-декларацию.
/// </summary>
public sealed class CbrRatesClient : ICbrRatesClient
{
    public const string HttpClientName = "cbr";

    private readonly HttpClient _http;

    public CbrRatesClient(IHttpClientFactory httpClientFactory) => _http = httpClientFactory.CreateClient(HttpClientName);

    public async Task<IReadOnlyList<CbrRatePoint>> GetRatesAsync(string cbrId, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var url = $"/scripts/XML_dynamic.asp?date_req1={from:dd/MM/yyyy}&date_req2={to:dd/MM/yyyy}&VAL_NM_RQ={Uri.EscapeDataString(cbrId)}";
        using var response = await _http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        return CbrRatesParser.Parse(Encoding.Latin1.GetString(bytes));
    }
}

/// <summary>Разбор XML <c>XML_dynamic.asp</c>. Чистая функция.</summary>
public static class CbrRatesParser
{
    /// <summary>
    /// <c>VunitRate</c> — рубли за 1 единицу; нет такого поля — <c>Value ÷ Nominal</c> (JPY/KZT котируются за 100). Записи с неразобранной
    /// датой/числом или нулём пропускаются. Все числа — десятичная запятая → InvariantCulture.
    /// </summary>
    public static IReadOnlyList<CbrRatePoint> Parse(string xml)
    {
        var start = xml.IndexOf("<ValCurs", StringComparison.Ordinal);
        if (start < 0) return [];
        var doc = XDocument.Parse(xml[start..]);
        var result = new List<CbrRatePoint>();
        foreach (var rec in doc.Descendants("Record"))
        {
            if (!DateOnly.TryParseExact((string?)rec.Attribute("Date"), "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) continue;

            var unitRate = Num((string?)rec.Element("VunitRate"));
            if (unitRate is null)
            {
                var value = Num((string?)rec.Element("Value"));
                var nominal = Num((string?)rec.Element("Nominal"));
                if (value is not null && nominal is > 0m) unitRate = value / nominal;
            }

            if (unitRate is > 0m) result.Add(new CbrRatePoint(date, unitRate.Value));
        }

        return result;
    }

    private static decimal? Num(string? s) =>
        decimal.TryParse(s?.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
}

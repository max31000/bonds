using System.Globalization;
using System.Text;
using Bonds.Core.Policy;

namespace Bonds.Core.Telegram;

/// <summary>
/// Форматирование для сообщений бота (задача 64). Не зависит от культуры сервера (в контейнере может быть инвариантная культура): разделитель тысяч — неразрывный
/// пробел, десятичный — запятая (русская локаль), знак валюты — после суммы. Текст идёт с <c>parse_mode=HTML</c>, поэтому всё пользовательское/внешнее экранируется
/// <see cref="Html"/>.
/// </summary>
public static class BotFormat
{
    private const char Nbsp = ' ';

    /// <summary>Экранирование для Telegram HTML: только <c>&amp; &lt; &gt;</c> (остальные символы допустимы как есть).</summary>
    public static string Html(string? text) =>
        string.IsNullOrEmpty(text) ? string.Empty : text.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);

    /// <summary>
    /// Экранирует <paramref name="text"/> и ограничивает ЭКРАНИРОВАННУЮ длину значением <paramref name="maxChars"/> (UTF-16): при превышении — обрезка по целым символам
    /// (суррогатная пара и сущность <c>&amp;amp;</c> не разрываются) и «…» в конце. Результат никогда не длиннее <paramref name="maxChars"/>.
    /// </summary>
    public static string HtmlTruncated(string? text, int maxChars)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var escaped = Html(text);
        if (escaped.Length <= maxChars) return escaped;

        var sb = new StringBuilder();
        for (var i = 0; i < text.Length;)
        {
            var width = char.IsHighSurrogate(text[i]) && i + 1 < text.Length ? 2 : 1;
            var piece = Html(text.Substring(i, width));
            if (sb.Length + piece.Length + 1 > maxChars) break;
            sb.Append(piece);
            i += width;
        }

        return sb.Append('…').ToString();
    }

    /// <summary>Фрагмент пользовательского ввода для эха в ответе об ошибке: не более <paramref name="maxChars"/> символов + «…» (ответ не должен раздуваться вводом). Без экранирования.</summary>
    public static string Echo(string? token, int maxChars = 32)
    {
        if (string.IsNullOrEmpty(token) || token.Length <= maxChars) return token ?? string.Empty;
        var cut = char.IsHighSurrogate(token[maxChars - 1]) ? maxChars - 1 : maxChars;
        return token[..cut] + "…";
    }

    /// <summary>
    /// Страховочная обрезка готового HTML до <paramref name="maxChars"/> (UTF-16): режет только между «токенами» (тег целиком, сущность целиком, символ), добавляет «…»
    /// и закрывает открытые теги — Telegram не отвергнет сообщение из-за разорванной разметки.
    /// </summary>
    public static string TruncateHtml(string html, int maxChars)
    {
        if (html.Length <= maxChars) return html;

        var open = new List<string>();
        var sb = new StringBuilder();
        var i = 0;
        while (i < html.Length)
        {
            var end = i + 1;
            string? opened = null, closed = null;
            if (html[i] == '<' && html.IndexOf('>', i) is var gt && gt > i)
            {
                end = gt + 1;
                var inner = html[(i + 1)..gt];
                var closing = inner.StartsWith('/');
                var name = new string(inner.TrimStart('/').TakeWhile(char.IsLetter).ToArray());
                if (closing) closed = name;
                else if (name.Length > 0 && !inner.EndsWith('/')) opened = name;
            }
            else if (html[i] == '&' && html.IndexOf(';', i) is var semi && semi > i && semi - i <= 10)
            {
                end = semi + 1;
            }
            else if (char.IsHighSurrogate(html[i]) && i + 1 < html.Length)
            {
                end = i + 2;
            }

            var after = open.Count > 0 && closed is not null ? open.Take(open.Count - 1).ToList() : open.ToList();
            if (opened is not null) after.Add(opened);
            var tailLength = 1 + after.Sum(n => n.Length + 3);
            if (sb.Length + (end - i) + tailLength > maxChars) break;

            sb.Append(html, i, end - i);
            if (opened is not null) open.Add(opened);
            if (closed is not null && open.Count > 0) open.RemoveAt(open.Count - 1);
            i = end;
        }

        sb.Append('…');
        for (var k = open.Count - 1; k >= 0; k--) sb.Append("</").Append(open[k]).Append('>');
        return sb.ToString();
    }

    /// <summary>
    /// Число в русской локали: <paramref name="decimals"/> знаков (математическое округление от нуля), группы по три цифры разделены неразрывным пробелом,
    /// запятая — десятичный разделитель, минус — «−» (U+2212).
    /// </summary>
    public static string Number(decimal value, int decimals = 0)
    {
        var rounded = Math.Round(value, decimals, MidpointRounding.AwayFromZero);
        var negative = rounded < 0m;
        var text = Math.Abs(rounded).ToString("F" + decimals, CultureInfo.InvariantCulture);
        var dot = text.IndexOf('.');
        var integer = dot < 0 ? text : text[..dot];
        var fraction = dot < 0 ? string.Empty : "," + text[(dot + 1)..];

        var sb = new StringBuilder();
        for (var i = 0; i < integer.Length; i++)
        {
            if (i > 0 && (integer.Length - i) % 3 == 0) sb.Append(Nbsp);
            sb.Append(integer[i]);
        }

        return (negative ? "−" : string.Empty) + sb + fraction;
    }

    /// <summary>Знак валюты для цены: ₽, $, € (прочие — ISO-код).</summary>
    public static string CurrencySign(string? currency) => (currency ?? string.Empty).Trim().ToUpperInvariant() switch
    {
        "RUB" or "RUR" or "" => "₽",
        "USD" => "$",
        "EUR" => "€",
        var code => code,
    };

    /// <summary>Сумма с валютой: рубли — без копеек («7 329 ₽»), прочие валюты — 2 знака («1 234,56 $»).</summary>
    public static string Money(decimal amount, string? currency)
    {
        var code = (currency ?? string.Empty).Trim().ToUpperInvariant();
        return code switch
        {
            "RUB" or "RUR" or "" => Number(amount) + " ₽",
            "USD" => Number(amount, 2) + " $",
            "EUR" => Number(amount, 2) + " €",
            _ => Number(amount, 2) + " " + code,
        };
    }

    /// <summary>Доля как проценты для человека (0.142 → «14,2%»). ДОЛЯ → проценты — единственная конверсия, только на границе вывода.</summary>
    public static string Percent(decimal fraction, int decimals = 1) => Number(fraction * 100m, decimals) + "%";

    /// <summary>Отклонение в процентных пунктах со знаком (−0.008 → «−0,8 п.п.»).</summary>
    public static string PercentPoints(decimal fraction, int decimals = 1)
    {
        var value = fraction * 100m;
        var text = Number(value, decimals);
        return (value > 0m && !text.StartsWith('−') ? "+" : string.Empty) + text + " п.п.";
    }

    /// <summary>Цена за штуку: до 4 знаков без хвостовых нулей, точка (как в терминале брокера и в плане: «166.57»).</summary>
    public static string Price(decimal price) =>
        Math.Round(price, 4, MidpointRounding.AwayFromZero).ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>Время МСК «ЧЧ:мм МСК» (Россия без перевода часов).</summary>
    public static string MoscowTime(DateTime utc) => TradingHours.ToMoscow(utc).ToString("HH:mm", CultureInfo.InvariantCulture) + " МСК";
}

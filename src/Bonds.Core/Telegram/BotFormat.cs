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

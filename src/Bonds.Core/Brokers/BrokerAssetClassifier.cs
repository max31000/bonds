using System.Text.RegularExpressions;
using Bonds.Core.Models;

namespace Bonds.Core.Brokers;

/// <summary>Чистые функции нормализации данных брокера Freedom (задача 60).</summary>
public static partial class BrokerAssetClassifier
{
    [GeneratedRegex(@"\b(ETF|UCITS|Fund)s?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EtfNameRegex();

    /// <summary>
    /// Класс актива по названию бумаги. ЭВРИСТИКА (брокер класс в <c>getPositionJson</c> не отдаёт): название содержит слово
    /// ETF / UCITS / Fund (без учёта регистра) — <see cref="BrokerAssetClass.Etf"/>, иначе <see cref="BrokerAssetClass.Stock"/>.
    /// Облигации и прочее так не распознаются (у Freedom сейчас их нет) — при появлении потребуется поле брокера <c>k</c>.
    /// </summary>
    public static string Classify(string? name) =>
        !string.IsNullOrWhiteSpace(name) && EtfNameRegex().IsMatch(name) ? BrokerAssetClass.Etf : BrokerAssetClass.Stock;

    /// <summary>
    /// ISO-код валюты ВЕРХНИМ регистром. Freedom отдаёт рубль легаси-кодом "RUR" — нормализуется в "RUB". Пустой/пробельный — null.
    /// </summary>
    public static string? NormalizeCurrency(string? code)
    {
        var c = code?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(c)) return null;
        return c == "RUR" ? "RUB" : c;
    }
}

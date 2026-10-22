namespace Bonds.Core.Currencies;

/// <summary>SECID валютной пары на MOEX (доска CETS рынка selt) по данным T-Invest (задача 55). Чистая функция.</summary>
public static class CurrencyMoexSecid
{
    /// <summary>
    /// Тикер T-Invest валютного инструмента совпадает с SECID MOEX (<c>CNYRUB_TOM</c>, <c>USD000UTSTOM</c>) — берём его, если он
    /// похож на расчётную пару (заканчивается на <c>_TOM</c> / <c>UTSTOM</c>, расчёты «завтра»). Иначе выводим из ISO-кода:
    /// USD → <c>USD000UTSTOM</c>, остальные → <c>{ISO}RUB_TOM</c> (CNYRUB_TOM, HKDRUB_TOM, JPYRUB_TOM…). Null — ISO пуст.
    /// </summary>
    public static string? Derive(string? ticker, string? isoCode)
    {
        var t = ticker?.Trim().ToUpperInvariant();
        if (!string.IsNullOrEmpty(t) && (t.EndsWith("_TOM", StringComparison.Ordinal) || t.EndsWith("UTSTOM", StringComparison.Ordinal)))
            return t;

        var iso = isoCode?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(iso)) return null;
        return iso == "USD" ? "USD000UTSTOM" : $"{iso}RUB_TOM";
    }
}

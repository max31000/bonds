namespace Bonds.Core.Currencies;

/// <summary>
/// Источник истории курса валюты, которую продукт ведёт ВСЕГДА (оценка брокерских позиций в рублях, задача 60), даже если у
/// владельца нет этой валюты в T-Invest. <paramref name="MoexSecid"/> — доска CETS (null — на бирже не торгуется, MOEX не опрашивается);
/// <paramref name="CbrId"/> — код валюты в справочнике ЦБ РФ (официальный курс, запасной источник либо основной).
/// </summary>
public sealed record TrackedCurrencySource(string Iso, string Name, string? MoexSecid, string? CbrId);

/// <summary>
/// Валюты, чья история курса ведётся всегда (по умолчанию USD, EUR, GBP). Идентификаторы сверены с живым MOEX ISS и cbr.ru 2026-10-23:
/// <list type="bullet">
/// <item><c>USD000UTSTOM</c> (CETS) — торгуется, CLOSE ≈ 80–86 ₽.</item>
/// <item><c>EUR_RUB__TOM</c> и <c>GBPRUB_TOM</c> на CETS существуют, но сделок НЕТ (CLOSE = 0, NUMTRADES = 0 за весь период, торги евро
/// и фунтом на бирже прекращены) — поэтому для EUR/GBP источник только ЦБ РФ (<c>R01239</c>, <c>R01035</c>), MOEX не опрашивается.</item>
/// <item>ЦБ: <c>R01235</c> USD, <c>R01239</c> EUR, <c>R01035</c> GBP (<c>XML_dynamic.asp</c>, <c>VunitRate</c> — рубли за 1 единицу).</item>
/// </list>
/// </summary>
public static class TrackedCurrencies
{
    public static IReadOnlyList<TrackedCurrencySource> All { get; } = new[]
    {
        new TrackedCurrencySource("USD", "Доллар США", "USD000UTSTOM", "R01235"),
        new TrackedCurrencySource("EUR", "Евро", null, "R01239"),
        new TrackedCurrencySource("GBP", "Фунт стерлингов", null, "R01035"),
    };

    public static TrackedCurrencySource? Find(string? iso) =>
        All.FirstOrDefault(c => string.Equals(c.Iso, iso?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Технический FIGI заглушки справочника (<c>currencies.figi</c> NOT NULL UNIQUE): заменяется настоящим при первом синке T-Invest.</summary>
    public static string PlaceholderFigi(string iso) => "FX:" + iso.ToUpperInvariant();
}

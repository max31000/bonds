namespace Bonds.Core.Trading;

/// <summary>Листинг бумаги на одном режиме торгов (один <c>classCode</c>) — вход <see cref="TradingListingSelector"/>.</summary>
public sealed record TradingListing(string ClassCode, bool ApiTradeAvailable);

/// <summary>
/// Выбор режима торгов (задача 61). У одного тикера бывает несколько <c>classCode</c> (листингов).
/// <para>
/// <b>Правило:</b> берём тот листинг, где <c>apiTradeAvailableFlag = true</c> (торговать через API можно только там); если таких нет —
/// <c>TQBR</c> (основной режим MOEX, чтобы метаданные были хотя бы для чтения); нет и TQBR — первый по алфавиту <c>classCode</c>
/// (детерминированно). Несколько с api=true — предпочитаем TQBR, затем по алфавиту.
/// </para>
/// <para>
/// <b>Пример (LQDT на живом счёте, 2026-10):</b> <c>TQBR</c> — api=false, <c>SPBRU</c> — api=true, выбирается SPBRU. Для SBMM/AKMM TQBR
/// api=false и другого листинга нет — остаётся TQBR с <c>api_trade_available = false</c> (бумага торгуется только вручную).
/// </para>
/// </summary>
public static class TradingListingSelector
{
    public const string DefaultClassCode = "TQBR";

    public static TradingListing? Pick(IReadOnlyCollection<TradingListing> listings)
    {
        if (listings.Count == 0) return null;

        var api = listings.Where(l => l.ApiTradeAvailable).ToList();
        if (api.Count > 0) return PreferDefault(api);

        return PreferDefault(listings);
    }

    private static TradingListing PreferDefault(IEnumerable<TradingListing> candidates)
    {
        var list = candidates.ToList();
        return list.FirstOrDefault(l => string.Equals(l.ClassCode, DefaultClassCode, StringComparison.OrdinalIgnoreCase))
               ?? list.OrderBy(l => l.ClassCode, StringComparer.OrdinalIgnoreCase).First();
    }
}

/// <summary>
/// Единицы шага цены облигации T-Invest (задача 61). <c>Bond.MinPriceIncrement</c> приходит в ПУНКТАХ (процент номинала, как цены
/// <c>GetLastPrices</c>, см. <c>LiveQuoteConverter</c>), а не в рублях. <b>Пункты → рубли за 1 бумагу: <c>рубли = пункты / 100 × номинал</c></b>.
/// Эталон: шаг 0.01 пункта при номинале 1000 ₽ = 0.1 ₽. Номинал — ТЕКУЩИЙ (у амортизируемых он уменьшается, метаданные обновляются раз в сутки).
/// </summary>
public static class BondPriceStep
{
    public static decimal PointsToRub(decimal stepPoints, decimal nominalRub) => stepPoints / 100m * nominalRub;
}

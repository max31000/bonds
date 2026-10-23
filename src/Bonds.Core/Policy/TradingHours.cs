using BrokerIds = Bonds.Core.Models.Brokers;
using Bonds.Core.Models;

namespace Bonds.Core.Policy;

/// <summary>
/// Часы торгов по рынкам (будни, окно в МОСКОВСКОМ времени, MSK = UTC+3 без перевода часов). Праздничные дни бирж не учитываются: в праздник
/// стакан пуст, и бумага пропускается по причине «нет стакана». Окна задаются <see cref="PolicyOptions"/>.
/// </summary>
public static class TradingHours
{
    private static readonly TimeSpan MoscowOffset = TimeSpan.FromHours(3);

    /// <summary>Рынок бумаги: Freedom — по суффиксу тикера (<c>.US</c>/<c>.EU</c>, прочее — без ограничений); T-Invest — <c>SPBRU</c> → СПБ, иначе Мосбиржа.</summary>
    public static PolicyMarket MarketOf(string broker, string ticker, string? classCode)
    {
        if (string.Equals(broker, BrokerIds.Freedom, StringComparison.OrdinalIgnoreCase))
        {
            var t = ticker.Trim().ToUpperInvariant();
            if (t.EndsWith(".US", StringComparison.Ordinal)) return PolicyMarket.FreedomUs;
            if (t.EndsWith(".EU", StringComparison.Ordinal)) return PolicyMarket.FreedomEu;
            return PolicyMarket.Unrestricted;
        }

        return string.Equals(classCode, "SPBRU", StringComparison.OrdinalIgnoreCase) ? PolicyMarket.SpbRu : PolicyMarket.Moex;
    }

    /// <summary>Московское время суток для момента UTC.</summary>
    public static DateTime ToMoscow(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).Add(MoscowOffset);

    /// <summary>Рынок открыт: будний день и время в окне [Open, Close).</summary>
    public static bool IsOpen(PolicyMarket market, DateTime nowUtc, PolicyOptions options)
    {
        var session = SessionOf(market, options);
        if (session is null) return true;

        var msk = ToMoscow(nowUtc);
        if (msk.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return false;
        return msk.TimeOfDay >= session.Open && msk.TimeOfDay < session.Close;
    }

    /// <summary>Момент закрытия сегодняшней сессии рынка (UTC); null — без ограничений или сегодня сессия не бывает (выходной).</summary>
    public static DateTime? SessionCloseUtc(PolicyMarket market, DateTime nowUtc, PolicyOptions options)
    {
        var session = SessionOf(market, options);
        if (session is null) return null;

        var msk = ToMoscow(nowUtc);
        if (msk.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return null;
        var closeMsk = msk.Date + session.Close;
        return DateTime.SpecifyKind(closeMsk - MoscowOffset, DateTimeKind.Utc);
    }

    private static TradingSessionOptions? SessionOf(PolicyMarket market, PolicyOptions options) => market switch
    {
        PolicyMarket.Moex => options.Moex,
        PolicyMarket.SpbRu => options.SpbRu,
        PolicyMarket.FreedomUs => options.FreedomUs,
        PolicyMarket.FreedomEu => options.FreedomEu,
        _ => null,
    };
}

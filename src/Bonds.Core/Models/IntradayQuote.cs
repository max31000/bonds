namespace Bonds.Core.Models;

/// <summary>
/// Один тик "лёгкого контура котировок" (plan/16 часть A) — только грязная цена инструмента на
/// момент опроса, без НКД/метрик. Пишется <c>LiveQuotesPollingService</c> раз в
/// <c>LiveQuotesOptions.PollingInterval</c> в торговые часы MOEX по открытым позициям, читается
/// GET /api/live/positions и GET /api/live/portfolio-intraday. НЕ путать с
/// <see cref="MarketQuote"/> — тот один снимок в день на инструмент из полного синка
/// (справочный/исторический), этот — плотный внутридневной ряд с retention 8 дней.
/// </summary>
public class IntradayQuote
{
    public ulong Id { get; set; }
    public ulong InstrumentId { get; set; }

    /// <summary>Момент опроса, UTC (миллисекундная точность — тики раз в ~60с, DATETIME(3) достаточно).</summary>
    public DateTime TsUtc { get; set; }

    /// <summary>Грязная цена (чистая цена + НКД) в рублях за облигацию.</summary>
    public decimal DirtyPriceRub { get; set; }
}

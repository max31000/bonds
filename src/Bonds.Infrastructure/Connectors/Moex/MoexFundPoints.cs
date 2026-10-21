namespace Bonds.Infrastructure.Connectors.Moex;

/// <summary>
/// Дневное закрытие пая (акции/паи, доска TQBR рынка shares) из истории торгов MOEX ISS.
/// <see cref="CloseRub"/> — <b>РУБЛИ за один пай</b> (колонка <c>CLOSE</c>), конверсии НЕТ — в отличие от облигаций, где
/// <c>CLOSE</c> это % от номинала (<see cref="MoexHistoryPricePoint.ClosePricePercent"/>). Null — в этот день сделок не было.
/// </summary>
public sealed record MoexSharePricePoint(DateOnly TradeDate, decimal? CloseRub);

/// <summary>
/// Дневная точка облигационного индекса MOEX (<c>/iss/history/engines/stock/markets/index/securities/{code}.json</c>).
/// <see cref="YieldFraction"/> — ДОЛЯ (ISS <c>YIELD</c> в процентах ÷ 100), <see cref="DurationYears"/> — ГОДЫ
/// (ISS <c>DURATION</c> в днях ÷ 365), <see cref="Close"/> — значение индекса в пунктах (ценовой индекс). Любое поле null,
/// если ISS его не вернул (ценовые индексы без YIELD/DURATION).
/// </summary>
public sealed record MoexIndexPoint(DateOnly TradeDate, decimal? Close, decimal? YieldFraction, decimal? DurationYears);

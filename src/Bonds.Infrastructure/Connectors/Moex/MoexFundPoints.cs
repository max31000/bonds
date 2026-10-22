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

/// <summary>
/// Дневное закрытие валютной пары MOEX (доска CETS рынка selt). <see cref="CloseRubPerFaceValue"/> — РУБЛИ ЗА <c>FACEVALUE</c> ЕДИНИЦ
/// валюты (колонка <c>CLOSE</c>; у CNY FACEVALUE = 1, у JPY/KZT = 100) — НЕ за 1 единицу: деление на FACEVALUE —
/// <see cref="Bonds.Core.Currencies.CurrencyRateUnits.RubPerUnit"/>, делает потребитель. Дни без сделок приходят с <c>CLOSE = 0</c>
/// (не null) — клиент отдаёт такие дни как null.
/// </summary>
public sealed record MoexCurrencyClosePoint(DateOnly TradeDate, decimal? CloseRubPerFaceValue);

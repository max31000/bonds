namespace Bonds.Infrastructure.Connectors.Moex;

/// <summary>
/// Одна дневная свеча из истории торгов MOEX ISS
/// (<c>/iss/history/engines/stock/markets/bonds/securities/{secid}.json</c>, plan/15 Часть A).
/// <see cref="ClosePricePercent"/> — цена закрытия в % от номинала (колонка <c>CLOSE</c>), как
/// в остальном коннекторе (см. <see cref="MoexSecurityInfo.PrevPrice"/>) — не рубли; переводить в
/// рубли (× номинал / 100) — ответственность потребителя. <see cref="AccruedInterestRub"/> — НКД
/// в рублях на дату (колонка <c>ACCINT</c>). Оба поля null, если ISS не вернул значение для дня
/// (нет сделок) — forward fill делает вызывающий код (plan/15 §A.2), не клиент.
/// </summary>
public sealed record MoexHistoryPricePoint(DateOnly Date, decimal? ClosePricePercent, decimal? AccruedInterestRub);

namespace Bonds.Infrastructure.Connectors.Moex;

/// <summary>
/// Одна дневная свеча из истории торгов MOEX ISS
/// (<c>/iss/history/engines/stock/markets/bonds/securities/{secid}.json</c>).
/// <see cref="ClosePricePercent"/> — цена закрытия в % от номинала (колонка <c>CLOSE</c>), как
/// в остальном коннекторе (см. <see cref="MoexSecurityInfo.PrevPrice"/>) — не рубли; переводить в
/// рубли (× номинал / 100) — ответственность потребителя. <see cref="AccruedInterestRub"/> — НКД
/// на дату (колонка <c>ACCINT</c>) в валюте расчётов режима <see cref="SettlementCurrency"/> (<c>CURRENCYID</c>):
/// у рублёвых режимов (<c>SUR</c>/null) — рубли; у бумаги с валютным номиналом в режиме TQOD — валюта (USD), а в
/// рублёвом TQCB — уже рубли, хотя номинал в USD. Оба поля null, если ISS не вернул значение для дня
/// (нет сделок) — forward fill делает вызывающий код, не клиент.
/// </summary>
public sealed record MoexHistoryPricePoint(
    DateOnly Date, decimal? ClosePricePercent, decimal? AccruedInterestRub, string? SettlementCurrency = null)
{
    /// <summary>НКД в рублях (расчёты режима в рублях: <c>SUR</c>/<c>RUB</c> или не указано).</summary>
    public bool AccruedIsRub => SettlementCurrency is null
        || SettlementCurrency.Equals("SUR", StringComparison.OrdinalIgnoreCase)
        || SettlementCurrency.Equals("RUB", StringComparison.OrdinalIgnoreCase);
}

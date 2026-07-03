namespace Bonds.Core.Models;

/// <summary>
/// Один дневной снимок кэша истории цены инструмента (plan/19 §A) — зеркалит поля
/// <c>Bonds.Infrastructure.Connectors.Moex.MoexHistoryPricePoint</c>, но персистентный (таблица
/// <c>instrument_price_history</c>), чтобы график цены карточки позиции не ходил в MOEX ISS при
/// каждом открытии. <see cref="ClosePricePercent"/> — null для дней без сделки (тот же смысл,
/// что у источника), <see cref="AccruedInterestRub"/> — НКД в рублях на момент даты, если источник
/// его вернул.
/// </summary>
public class InstrumentPriceHistory
{
    public ulong Id { get; set; }
    public ulong InstrumentId { get; set; }
    public DateOnly Date { get; set; }
    public decimal? ClosePricePercent { get; set; }
    public decimal? AccruedInterestRub { get; set; }
    public DateTime CreatedAt { get; set; }
}

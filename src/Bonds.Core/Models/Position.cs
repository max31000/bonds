namespace Bonds.Core.Models;

/// <summary>
/// Холдинг пользователя по инструменту — cost basis и количество, источник истины T-Invest
/// (spec §4.1, §5). Текущая НКД по открытой позиции приоритетно из T-Invest (см. plan/00 §4).
/// </summary>
public class Position
{
    public ulong Id { get; set; }
    public ulong AccountId { get; set; }
    public ulong InstrumentId { get; set; }

    public decimal Quantity { get; set; }

    /// <summary>Средневзвешенная цена покупки (cost basis), в рублях за одну облигацию (чистая цена).</summary>
    public decimal AvgPurchasePrice { get; set; }

    /// <summary>
    /// Текущий накопленный купонный доход (НКД) НА ОДНУ БУМАГУ (не на всю позицию), из T-Invest
    /// на момент синка (<c>PortfolioPosition.current_nkd</c>).
    /// <para>
    /// Задача 24 — подтверждено по коду: <see cref="Bonds.Infrastructure.Sync.BondSyncService"/>
    /// пишет сюда <c>tip.CurrentNkd</c> и ТУДА ЖЕ (не умножая на количество) кладёт его в
    /// <c>MarketQuote.Accrued</c>/<c>DirtyPrice = CurrentPrice + CurrentNkd</c> — грязная цена ОДНОЙ
    /// бумаги. Дальше <see cref="Bonds.Infrastructure.Analytics.PortfolioHoldingsBuilder.ToHolding"/>
    /// считает <c>MarketValueRub = DirtyPrice × Position.Quantity</c> — если бы Accrued уже был на
    /// всю позицию, эта формула задвоила бы НКД. Итого: чтобы получить НКД на всю позицию,
    /// нужно домножить на <see cref="Quantity"/> (см. <c>PortfolioHolding.AccruedPerBondRub</c> и
    /// DTO-поля <c>accruedTotalRub</c> на фронте).
    /// </para>
    /// </summary>
    public decimal Accrued { get; set; }

    /// <summary>§4.4: данные по позиции неполные (например, не удалось сверить с MOEX расписанием).</summary>
    public bool DataIncomplete { get; set; }

    public DateTime UpdatedAt { get; set; }
}

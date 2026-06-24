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

    /// <summary>Текущий накопленный купонный доход по позиции (из T-Invest на момент синка).</summary>
    public decimal Accrued { get; set; }

    /// <summary>§4.4: данные по позиции неполные (например, не удалось сверить с MOEX расписанием).</summary>
    public bool DataIncomplete { get; set; }

    public DateTime UpdatedAt { get; set; }
}

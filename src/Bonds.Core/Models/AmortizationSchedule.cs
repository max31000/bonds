namespace Bonds.Core.Models;

/// <summary>
/// График амортизационных выплат (частичный возврат номинала). Источник истины — MOEX ISS
/// (T-Invest по облигациям обычно отдаёт только флаг наличия амортизации, без полного
/// графика — spec §4.1). Убывающий номинал учитывается дальше движком расчётов (этап 05).
/// </summary>
public class AmortizationSchedule
{
    public ulong Id { get; set; }
    public ulong InstrumentId { get; set; }

    public DateOnly Date { get; set; }

    /// <summary>Сумма погашения номинала в рублях на одну облигацию.</summary>
    public decimal AmountRub { get; set; }

    public DateTime CreatedAt { get; set; }
}

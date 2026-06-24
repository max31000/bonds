namespace Bonds.Core.Models;

/// <summary>
/// Производная сущность — спроецированные будущие поступления по позиции (spec §5, §7).
/// Заполняется Cash-Flow Projection (этап 06); этот этап только готовит хранилище.
/// НДФЛ 13% удерживается только с купонного дохода, не с амортизации/погашения тела (spec §7.2).
/// </summary>
public class ProjectedCashFlow
{
    public ulong Id { get; set; }
    public ulong PositionId { get; set; }
    public ulong InstrumentId { get; set; }

    public DateOnly Date { get; set; }
    public CashFlowType FlowType { get; set; }

    public decimal GrossRub { get; set; }
    public decimal TaxRub { get; set; }
    public decimal NetRub { get; set; }

    /// <summary>
    /// true — поток оценочный (флоатер за горизонтом известной ставки и т.п.), не гарантированный.
    /// Отражает spec §4.4/§6 "Краевые случаи": неопределённость не скрывается, а помечается.
    /// </summary>
    public bool IsEstimated { get; set; }

    public DateTime CreatedAt { get; set; }
}

public enum CashFlowType
{
    Coupon,
    Amortization,
    Redemption,
}

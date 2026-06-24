namespace Bonds.Core.Models;

/// <summary>
/// Ежедневный снимок стоимости/доходности портфеля для "кривой доходности портфеля
/// во времени" (spec §9). Заполняется планировщиком (этап 07); здесь только хранилище.
/// </summary>
public class PortfolioValueSnapshot
{
    public ulong Id { get; set; }
    public ulong AccountId { get; set; }

    public DateOnly AsOf { get; set; }

    public decimal MarketValueRub { get; set; }
    public decimal? XirrToDate { get; set; }
    public decimal InvestedRub { get; set; }

    public DateTime CreatedAt { get; set; }
}

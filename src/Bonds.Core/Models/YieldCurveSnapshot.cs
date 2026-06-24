namespace Bonds.Core.Models;

/// <summary>
/// Снимок безрисковой кривой (MOEX Gcurve / КБД) на дату — параметры модели
/// Нельсона-Сигеля-Свенссона (spec §4.2, §6 — G-спред). Название "MOEX GCURVE" — товарный
/// знак; в публичном UI использовать нейтральное "безрисковая кривая" (spec §4.3).
/// </summary>
public class YieldCurveSnapshot
{
    public ulong Id { get; set; }
    public DateOnly AsOf { get; set; }

    public decimal B1 { get; set; }
    public decimal B2 { get; set; }
    public decimal B3 { get; set; }
    public decimal T1 { get; set; }

    public decimal G1 { get; set; }
    public decimal G2 { get; set; }
    public decimal G3 { get; set; }
    public decimal G4 { get; set; }
    public decimal G5 { get; set; }
    public decimal G6 { get; set; }
    public decimal G7 { get; set; }
    public decimal G8 { get; set; }
    public decimal G9 { get; set; }

    public DateTime CreatedAt { get; set; }
}

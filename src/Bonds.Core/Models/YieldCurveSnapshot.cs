namespace Bonds.Core.Models;

/// <summary>
/// Снимок безрисковой кривой (MOEX Gcurve / КБД) на дату — параметры модели
/// Нельсона-Сигеля-Свенссона с корректирующими членами (spec §4.2, §6 — G-спред). Название
/// "MOEX GCURVE" — товарный знак; в публичном UI использовать нейтральное "безрисковая
/// кривая" (spec §4.3).
/// <para>
/// <b>Единицы измерения:</b> B1/B2/B3 и G1..G9 приходят от MOEX ISS И ХРАНЯТСЯ здесь в
/// БАЗИСНЫХ ПУНКТАХ (как в исходном ответе API, см. tests/Bonds.Tests/Fixtures/Moex/zcyc_gcurve.json,
/// где B1≈1521) — НЕ в долях. T1 — в годах. См. <see cref="Bonds.Core.Calculation.GSpreadCalculator"/>
/// для точной формулы конвертации в годовую доходность (через G(t) в б.п. → exp(G(t)/10000)-1).
/// Не подавать сюда значения, уже переведённые в доли — калькулятор ожидает именно б.п.
/// </para>
/// </summary>
public class YieldCurveSnapshot
{
    public ulong Id { get; set; }
    public DateOnly AsOf { get; set; }

    /// <summary>В базисных пунктах (см. примечание класса о единицах измерения).</summary>
    public decimal B1 { get; set; }

    /// <summary>В базисных пунктах.</summary>
    public decimal B2 { get; set; }

    /// <summary>В базисных пунктах.</summary>
    public decimal B3 { get; set; }

    /// <summary>В годах.</summary>
    public decimal T1 { get; set; }

    /// <summary>Корректирующая поправка Gi, в базисных пунктах (методика MOEX, раздел 4.1).</summary>
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

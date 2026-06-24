using Bonds.Core.Models;

namespace Bonds.Core.Calculation;

/// <summary>
/// Реконструкция безрисковой кривой (MOEX Gcurve/КБД) по параметрам модели
/// Нельсона-Сигеля-Свенссона и расчёт G-спреда (spec §6.8). Название "MOEX GCURVE" —
/// товарный знак (spec §4.3) — в публичном UI использовать нейтральное наименование;
/// здесь это просто математическая кривая.
/// </summary>
public static class GSpreadCalculator
{
    /// <summary>
    /// Значение кривой NSS (бескупонная доходность, в долях, напр. 0.12 = 12%) на срок
    /// <paramref name="termYears"/> лет.
    /// <para>
    /// Каноническая параметризация Svensson (используемая MOEX для Gcurve):
    /// <c>y(t) = b1 + b2 * ((1 - e^(-t/t1)) / (t/t1)) + b3 * (((1 - e^(-t/t1)) / (t/t1)) - e^(-t/t1))
    /// + g_k</c>, где <c>g_k</c> — поправочный сплайн-член по сегментам G1..G9 на стандартных
    /// узлах срока MOEX (0.25, 0.5, 0.75, 1, 2, 3, 5, 7, 10, 15, 20, 30 лет — публичная
    /// методика расчёта КБД). Для MVP используем кусочно-линейную интерполяцию между
    /// G-поправками на стандартных узлах термов, что является принятым практическим упрощением
    /// полной сплайн-модели MOEX (ТРЕБУЕТ СОГЛАСОВАНИЯ С ВЛАДЕЛЬЦЕМ, если точность вне ±неск.
    /// б.п. станет критична — см. финальный отчёт этапа).
    /// </para>
    /// </summary>
    public static decimal CurveValue(YieldCurveSnapshot snapshot, decimal termYears)
    {
        if (termYears <= 0m) termYears = 0.01m;

        var t = (double)termYears;
        var t1 = (double)snapshot.T1;
        if (t1 <= 0) t1 = 1.0; // защита от вырожденных параметров

        var x = t / t1;
        var decay = Math.Exp(-x);
        var factor1 = x > 1e-8 ? (1.0 - decay) / x : 1.0;
        var nss = (double)snapshot.B1
            + (double)snapshot.B2 * factor1
            + (double)snapshot.B3 * (factor1 - decay);

        var gAdjustment = InterpolateGAdjustment(snapshot, termYears);

        return (decimal)nss + gAdjustment;
    }

    /// <summary>
    /// Узлы термов (в годах), на которых заданы поправочные коэффициенты G1..G9 в публичной
    /// методике расчёта КБД Московской биржи.
    /// </summary>
    private static readonly decimal[] GNodeYears = { 0.25m, 0.5m, 0.75m, 1m, 2m, 3m, 5m, 7m, 10m };

    private static decimal InterpolateGAdjustment(YieldCurveSnapshot snapshot, decimal termYears)
    {
        var values = new[]
        {
            snapshot.G1, snapshot.G2, snapshot.G3, snapshot.G4, snapshot.G5,
            snapshot.G6, snapshot.G7, snapshot.G8, snapshot.G9,
        };

        if (termYears <= GNodeYears[0]) return values[0];
        if (termYears >= GNodeYears[^1]) return values[^1];

        for (var i = 0; i < GNodeYears.Length - 1; i++)
        {
            var x0 = GNodeYears[i];
            var x1 = GNodeYears[i + 1];
            if (termYears >= x0 && termYears <= x1)
            {
                if (x1 == x0) return values[i];
                var weight = (termYears - x0) / (x1 - x0);
                return values[i] + weight * (values[i + 1] - values[i]);
            }
        }

        return values[^1];
    }

    /// <summary>
    /// G-спред = YTM бумаги − значение кривой, интерполированное на срок, сопоставимый с бумагой.
    /// Срок сопоставления — модифицированная дюрация бумаги в годах (устоявшаяся практика,
    /// точнее сравнивать по дюрации, чем по сроку до погашения, особенно для амортизируемых
    /// бумаг). Возвращается в долях (напр. 0.015 = 150 б.п.).
    /// </summary>
    public static decimal GSpread(decimal bondYtm, decimal durationYears, YieldCurveSnapshot curveSnapshot)
    {
        var curveValue = CurveValue(curveSnapshot, durationYears);
        return bondYtm - curveValue;
    }
}

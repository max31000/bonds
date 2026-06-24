using Bonds.Core.Models;

namespace Bonds.Core.Calculation;

/// <summary>
/// Реконструкция безрисковой кривой (MOEX Gcurve/КБД) по параметрам модели
/// Нельсона-Сигеля-Свенссона с корректирующими членами и расчёт G-спреда (spec §6.8).
/// Название "MOEX GCURVE" — товарный знак (spec §4.3) — в публичном UI использовать
/// нейтральное наименование; здесь это просто математическая кривая.
/// <para>
/// Формула и константы взяты буквально из официальной методики Московской биржи
/// ("Методика определения кривой бескупонной доходности государственных облигаций
/// (облигаций федеральных займов)", утв. Правлением ПАО Московская Биржа 07.07.2017,
/// Протокол №40, раздел 4 "Параметрическая модель Кривой") — НЕ предположение/упрощение,
/// а точное воспроизведение опубликованной формулы:
/// <c>G(t) = β0 + (β1+β2)·(τ/t)·[1-exp(-t/τ)] - β2·exp(-t/τ) + Σ(i=1..9) gi·exp(-(t-ai)²/bi²)</c>,
/// где <c>G(t)</c> — непрерывно начисляемая ставка **в базисных пунктах**, срок <c>t</c> — в
/// годах. <c>ai</c>/<c>bi</c> — ФИКСИРОВАННЫЕ константы методики (НЕ годовые term-узлы 0.25/
/// 0.5/.../10 лет и НЕ интерполяция!): <c>a1=0, a2=0.6, a(i+1)=ai+a2·k^(i-1)</c>,
/// <c>b1=a2, b(i+1)=bi·k</c>, <c>k=1.6</c> (раздел 4.1). Каждый член ряда — гауссов "бугор"
/// (не сплайн-сегмент), центрированный в фиксированной точке ai с шириной bi; точки ai растут
/// от 0 до ~26.4 года и НЕ совпадают с публично цитируемыми "стандартными сроками КБД".
/// </para>
/// <para>
/// Бескупонная доходность (годовая капитализация, в долях) получается из <c>G(t)</c>
/// (которое в б.п.) через раздел 4.2 методики: <c>Y(t) = exp(G(t)/10000) - 1</c>.
/// **Предыдущая реализация этого калькулятора (до этого ревью) складывала NSS-часть в долях
/// и G-поправки в долях напрямую, без перевода через эту экспоненту и без верных ai/bi — на
/// реальных параметрах MOEX (масштаб B1~1500 б.п., не ~0.10) это давало абсурдные значения
/// кривой (тысячи "процентов"). Юнит-тесты предыдущей реализации не поймали баг, потому что
/// использовали синтетические B1=0.10 в долевом масштабе, а не реальный масштаб б.п. Это
/// исправлено здесь; см. также tests/Bonds.Tests/Fixtures/Moex/zcyc_gcurve.json (реальный
/// снимок) и обновлённые тесты с реалистичными по масштабу параметрами.</c>**
/// </para>
/// </summary>
public static class GSpreadCalculator
{
    /// <summary>
    /// Фиксированные параметры центров (ai, годы) и ширин (bi, годы) гауссовых корректирующих
    /// членов — раздел 4.1 методики: a1=0, a2=0.6, a(i+1)=ai+a2*k^(i-1); b1=a2, b(i+1)=bi*k; k=1.6.
    /// Вычислены один раз статически (детерминированные константы методики, не настраиваемые).
    /// </summary>
    private static readonly (double A, double B)[] GTerms = BuildGTerms();

    private static (double A, double B)[] BuildGTerms()
    {
        const double k = 1.6;
        const double a2 = 0.6;
        var a = new double[9];
        var b = new double[9];
        a[0] = 0.0;
        a[1] = a2;
        for (var i = 2; i < 9; i++)
        {
            // a(i+1) = ai + a2*k^(i-1), индексация раздела 4.1 — 1-based (i=2..8); здесь 0-based.
            a[i] = a[i - 1] + a2 * Math.Pow(k, i - 1);
        }

        b[0] = a2;
        for (var i = 1; i < 9; i++)
        {
            b[i] = b[i - 1] * k;
        }

        var result = new (double A, double B)[9];
        for (var i = 0; i < 9; i++)
        {
            result[i] = (a[i], b[i]);
        }

        return result;
    }

    /// <summary>
    /// Значение непрерывно начисляемой ставки G(t) в базисных пунктах (методика, раздел 4.1) —
    /// промежуточный результат, до перевода в годовую доходность.
    /// </summary>
    private static double GBasisPoints(YieldCurveSnapshot snapshot, double t)
    {
        var tau = (double)snapshot.T1;
        if (tau <= 0) tau = 1.0; // защита от вырожденных параметров

        var b0 = (double)snapshot.B1;
        var b1 = (double)snapshot.B2;
        var b2 = (double)snapshot.B3;

        var decay = Math.Exp(-t / tau);
        var nss = b0 + (b1 + b2) * (tau / t) * (1.0 - decay) - b2 * decay;

        var g = new[]
        {
            (double)snapshot.G1, (double)snapshot.G2, (double)snapshot.G3,
            (double)snapshot.G4, (double)snapshot.G5, (double)snapshot.G6,
            (double)snapshot.G7, (double)snapshot.G8, (double)snapshot.G9,
        };

        var correction = 0.0;
        for (var i = 0; i < 9; i++)
        {
            var (a, bWidth) = GTerms[i];
            var diff = t - a;
            correction += g[i] * Math.Exp(-(diff * diff) / (bWidth * bWidth));
        }

        return nss + correction;
    }

    /// <summary>
    /// Значение кривой (бескупонная доходность с годовой капитализацией, в долях, напр.
    /// 0.12 = 12%) на срок <paramref name="termYears"/> лет. Методика, разделы 4.1-4.2:
    /// G(t) в б.п. → Y(t) = exp(G(t)/10000) - 1 (годовая капитализация).
    /// </summary>
    public static decimal CurveValue(YieldCurveSnapshot snapshot, decimal termYears)
    {
        if (termYears <= 0m) termYears = 0.01m;

        var t = (double)termYears;
        var gBps = GBasisPoints(snapshot, t);
        var yearlyYield = Math.Exp(gBps / 10000.0) - 1.0;

        return (decimal)yearlyYield;
    }

    /// <summary>
    /// G-спред = YTM бумаги − значение кривой на срок, сопоставимый с бумагой. Срок
    /// сопоставления — модифицированная дюрация бумаги в годах (устоявшаяся практика,
    /// точнее сравнивать по дюрации, чем по сроку до погашения, особенно для амортизируемых
    /// бумаг). Возвращается в долях (напр. 0.015 = 150 б.п.).
    /// </summary>
    public static decimal GSpread(decimal bondYtm, decimal durationYears, YieldCurveSnapshot curveSnapshot)
    {
        var curveValue = CurveValue(curveSnapshot, durationYears);
        return bondYtm - curveValue;
    }
}

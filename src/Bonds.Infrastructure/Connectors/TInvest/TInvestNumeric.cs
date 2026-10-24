using Tinkoff.InvestApi.V1;

namespace Bonds.Infrastructure.Connectors.TInvest;

/// <summary>
/// Конвертация числовых типов T-Invest gRPC-контракта (<see cref="MoneyValue"/>, <see cref="Quotation"/>)
/// в <see cref="decimal"/>. Оба типа кодируют число как целая часть (Units) + дробная в нанодолях
/// (Nano, 10^-9) — это документированный формат protobuf-контракта T-Invest (verified §12.2,
/// см. README коннектора), SDK не предоставляет готовый helper, поэтому реализуем сами.
/// </summary>
public static class TInvestNumeric
{
    private const decimal NanoScale = 1_000_000_000m;

    public static decimal ToDecimal(this MoneyValue? value)
        => value is null ? 0m : value.Units + value.Nano / NanoScale;

    public static decimal ToDecimal(this Quotation? value)
        => value is null ? 0m : value.Units + value.Nano / NanoScale;

    public static decimal? ToNullableDecimal(this MoneyValue? value)
        => value is null ? null : value.ToDecimal();

    public static decimal? ToNullableDecimal(this Quotation? value)
        => value is null ? null : value.ToDecimal();

    /// <summary>
    /// <see cref="decimal"/> → <see cref="Quotation"/> (задача 65; ЕДИНСТВЕННОЕ место конверсии цены заявки в формат брокера). Units — целая часть (усечение к нулю),
    /// Nano — дробная часть × 10^9 с ТЕМ ЖЕ знаком (−1.5 → Units −1, Nano −500 000 000), как требует контракт T-Invest. Единицы НЕ меняются: облигация — пункты
    /// (процент номинала), остальное — валюта за штуку; что именно передаётся, решает вызывающий через <c>PriceType</c>. Больше 9 знаков после запятой —
    /// <see cref="ArgumentException"/> (цена заявки должна лежать на сетке шага, а шаги T-Invest не мельче 10^-9).
    /// </summary>
    public static Quotation ToQuotation(decimal value)
    {
        if (decimal.Round(value, 9) != value) throw new ArgumentException("Цена заявки точнее 10^-9 — не на сетке шага", nameof(value));
        var units = decimal.Truncate(value);
        var nano = (value - units) * NanoScale;
        return new Quotation { Units = (long)units, Nano = (int)nano };
    }

    /// <summary>Порог различения единиц TER: выше — «проценты», в диапазоне (0, порог] — уже «доля».</summary>
    private const decimal ExpenseFractionCeiling = 0.05m;

    /// <summary>Верхняя граница правдоподобного TER в процентах: выше — считаем мусором.</summary>
    private const decimal ExpensePercentCeiling = 10m;

    /// <summary>
    /// <c>AssetEtf.TotalExpense</c> (T-Invest) → ДОЛЯ годовых. Документация SDK говорит «в процентах», но на практике единицы
    /// не подтверждены, поэтому конверсия по правдоподобию: реальные TER фондов — 0.1–3% (доля 0.001–0.03), TER &lt; 0.05%
    /// (доля &lt; 0.0005) не бывает, значит значение <c>raw &gt; 0.05</c> — это проценты (<c>raw/100</c>: 0.9 → 0.009),
    /// а <c>0 &lt; raw ≤ 0.05</c> — уже доля (0.009 → 0.009). Неправдоподобное (<c>≤ 0</c> или <c>&gt; 10</c>, т.е. TER &gt; 10%) → null;
    /// вызывающий логирует предупреждение. Чистая функция — граничные случаи закреплены тестами.
    /// </summary>
    public static decimal? ExpenseRatioToFraction(decimal raw)
    {
        if (raw <= 0m || raw > ExpensePercentCeiling) return null;
        return raw > ExpenseFractionCeiling ? raw / 100m : raw;
    }
}

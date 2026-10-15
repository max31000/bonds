namespace Bonds.Infrastructure.Connectors.Moex;

/// <summary>
/// Разобранная карточка бумаги MOEX ISS (<c>/iss/securities/{secid}.json?iss.only=description</c>,
/// пары name/value). Любое поле null, если биржа его не отдала или значение не распознано — null
/// означает «неизвестно», а НЕ «нет» (например, <see cref="HasDefault"/> = null ≠ false).
/// <para>
/// <b>Единицы:</b> <see cref="CouponBenchmarkSpreadFraction"/> — ДОЛЯ (MOEX отдаёт
/// COUPON_BENCHMARK_SPREAD в процентных пунктах, "1.90" → 0.019; конверсия — в парсере).
/// Булевы флаги ISS приходят строками "0"/"1".
/// </para>
/// </summary>
public sealed class MoexSecurityDescription
{
    public required string Secid { get; init; }
    public string? Isin { get; init; }

    /// <summary>EMITTER_ID — идентификатор эмитента в справочнике MOEX (тот же, что emitent_id bulk-справочника).</summary>
    public int? EmitterId { get; init; }

    /// <summary>HASDEFAULT — по бумаге допущен дефолт.</summary>
    public bool? HasDefault { get; init; }

    /// <summary>HASTECHNICALDEFAULT — по бумаге допущен технический дефолт.</summary>
    public bool? HasTechnicalDefault { get; init; }

    /// <summary>ISQUALIFIEDINVESTORS — бумага только для квалифицированных инвесторов.</summary>
    public bool? IsQualifiedInvestors { get; init; }

    /// <summary>COUPON_BENCHMARK — базовый индикатор плавающего купона (RUONIA, RREFKEYR, CPI…).</summary>
    public string? CouponBenchmark { get; init; }

    /// <summary>ДОЛЯ (COUPON_BENCHMARK_SPREAD в процентных пунктах ÷ 100): надбавка к бенчмарку.</summary>
    public decimal? CouponBenchmarkSpreadFraction { get; init; }

    /// <summary>EARLYREPAYMENT — предусмотрено досрочное погашение.</summary>
    public bool? EarlyRepayment { get; init; }
}

namespace Bonds.Core.Models;

/// <summary>
/// Задача 26 (банк облигаций MOEX) — одна строка снимка ВСЕЙ рыночной вселенной облигаций (не
/// только бумаг из портфеля/watchlist). Источник — MOEX ISS securities+marketdata (см.
/// <see cref="Bonds.Infrastructure.Connectors.Moex.MoexBondMarketRow"/> для сырых полей до
/// конвертации единиц). Обновляется целиком раз в час хостед-сервисом (upsert по <see cref="Secid"/>),
/// НЕ прогоняется через точный движок <see cref="Bonds.Core.Calculation.BondMetricsCalculator"/> —
/// это дешёвая биржевая статистика "как есть", точный расчёт — по требованию для избранных бумаг
///, см. doc-comment plan/26 "Архитектурная рамка".
/// <para>
/// <b>Единицы измерения (см. plan/26 "ЕДИНИЦЫ"):</b>
/// <see cref="YieldFraction"/> — ДОЛЯ (MOEX YIELD приходит в ПРОЦЕНТАХ, здесь уже разделено на 100:
/// 0.12 = 12%). <see cref="DurationYears"/> — ГОДЫ (MOEX DURATION приходит в ДНЯХ, здесь уже
/// разделено на 365). <see cref="PricePercent"/>/<see cref="BidPercent"/>/<see cref="OfferPercent"/> —
/// % от номинала, КАК ПРИШЛИ от MOEX (суффикс _percent — не переведены в доли/рубли).
/// </para>
/// <para>
/// <b>Биржевые расчёты (задача 42, блок ISS <c>marketdata_yields</c>):</b>
/// <see cref="EffectiveYieldFraction"/>/<see cref="YieldToOfferFraction"/> — ДОЛИ (MOEX отдаёт %, ÷100);
/// <see cref="GspreadOfficialFraction"/>/<see cref="ZspreadOfficialFraction"/> — ДОЛИ (MOEX отдаёт
/// базисные пункты, ÷10000: 35 б.п. = 0.0035); <see cref="AccruedRub"/> — РУБЛИ на одну бумагу.
/// </para>
/// </summary>
public class BondUniverseEntry
{
    public ulong Id { get; set; }

    /// <summary>Биржевой идентификатор MOEX (SECID) — естественный ключ снимка (upsert).</summary>
    public string Secid { get; set; } = string.Empty;

    public string? Isin { get; set; }
    public string? ShortName { get; set; }
    public string? SecName { get; set; }

    public decimal? FaceValue { get; set; }
    public decimal? LotValue { get; set; }

    /// <summary>Ставка купона в % (как MOEX COUPONPERCENT) — НЕ доля.</summary>
    public decimal? CouponPercent { get; set; }

    public DateOnly? MaturityDate { get; set; }
    public DateOnly? OfferDate { get; set; }

    /// <summary>Уровень листинга MOEX (1/2/3).</summary>
    public int? ListLevel { get; set; }

    /// <summary>Простая классификация "Гособлигации"/"Муниципальные"/"Корпоративные" по коду
    /// SECTYPE (см. <c>BondUniverseSectorMapper</c>) — НЕ то же самое, что <see cref="Instrument.Sector"/>.</summary>
    public string? Sector { get; set; }

    /// <summary>ДОЛЯ (см. doc-comment класса про единицы). Null, если сделок сегодня не было.</summary>
    public decimal? YieldFraction { get; set; }

    /// <summary>ГОДЫ (см. doc-comment класса про единицы).</summary>
    public decimal? DurationYears { get; set; }

    /// <summary>Цена последней сделки, % от номинала.</summary>
    public decimal? PricePercent { get; set; }

    /// <summary>Оборот за сегодня, в рублях.</summary>
    public decimal? TurnoverRub { get; set; }

    public decimal? BidPercent { get; set; }
    public decimal? OfferPercent { get; set; }
    public int? NumTrades { get; set; }

    /// <summary>
    /// Приближённый G-спред = YieldFraction − значение безрисковой кривой на срок DurationYears
    /// (см. <see cref="Bonds.Core.Calculation.GSpreadCalculator"/>, переиспользуется интерполяция).
    /// Приближение по данным MOEX (YIELD/DURATION биржи), НЕ точный движок — см. doc-comment
    /// <c>BondUniverseRefreshService</c>. Null, если кривая/дюрация недоступны.
    /// </summary>
    public decimal? GspreadApproxFraction { get; set; }

    /// <summary>Задача 42: НКД в РУБЛЯХ на одну бумагу (MOEX ACCRUEDINT). Null — биржа не отдала
    /// (старый снимок до миграции 017 или бумага без данных); потребители трактуют как "НКД неизвестен".</summary>
    public decimal? AccruedRub { get; set; }

    /// <summary>Задача 42: объём выпуска, штук бумаг (MOEX ISSUESIZE).</summary>
    public long? IssueSize { get; set; }

    /// <summary>Задача 42: эффективная доходность по расчёту биржи, ДОЛЯ (MOEX EFFECTIVEYIELD в % ÷ 100).</summary>
    public decimal? EffectiveYieldFraction { get; set; }

    /// <summary>Задача 42: доходность к оферте по расчёту биржи, ДОЛЯ (MOEX YIELDTOOFFER в % ÷ 100).
    /// Null — оферты нет или биржа не посчитала.</summary>
    public decimal? YieldToOfferFraction { get; set; }

    /// <summary>Задача 42: к какой дате биржа считает доходность — "MATDATE" (к погашению), "OFFER"
    /// (к оферте) и т.п., строка как отдаёт MOEX (YIELDDATETYPE).</summary>
    public string? YieldDateType { get; set; }

    /// <summary>Задача 42: G-спред, посчитанный БИРЖЕЙ, ДОЛЯ (MOEX GSPREADBP в б.п. ÷ 10000:
    /// 35 б.п. = 0.0035). Null — биржа не посчитала (нет сделок/нет строки marketdata_yields).</summary>
    public decimal? GspreadOfficialFraction { get; set; }

    /// <summary>Задача 42: Z-спред, посчитанный БИРЖЕЙ, ДОЛЯ (MOEX ZSPREADBP в б.п. ÷ 10000).</summary>
    public decimal? ZspreadOfficialFraction { get; set; }

    /// <summary>
    /// Биржевой G-спред, при его отсутствии — наше приближение по сохранённой кривой
    /// (<see cref="GspreadApproxFraction"/>). ДОЛЯ. Единственное значение спреда, которое должны
    /// читать потребители банка (RV-корзины, риск-сигналы кандидатов, скринер): иначе в одной
    /// корзине смешиваются два способа расчёта. Источник значения — <see cref="GspreadSource"/>.
    /// </summary>
    public decimal? EffectiveGspreadFraction => GspreadOfficialFraction ?? GspreadApproxFraction;

    /// <summary>Откуда взято <see cref="EffectiveGspreadFraction"/>: <see cref="GspreadSourceMoex"/>,
    /// <see cref="GspreadSourceApprox"/> или null (спреда нет вовсе).</summary>
    public string? GspreadSource =>
        GspreadOfficialFraction is not null ? GspreadSourceMoex
        : GspreadApproxFraction is not null ? GspreadSourceApprox
        : null;

    /// <summary>Значение <see cref="GspreadSource"/>: спред посчитала биржа.</summary>
    public const string GspreadSourceMoex = "moex";

    /// <summary>Значение <see cref="GspreadSource"/>: наше приближение по последней сохранённой кривой.</summary>
    public const string GspreadSourceApprox = "approx";

    /// <summary>Эвристика по BONDTYPE ISS ("Флоатер") — см. <c>MoexSecurityInfo.LooksLikeFloater</c>.
    /// Null, если распознать не удалось (BONDTYPE отсутствует в ответе).</summary>
    public bool? IsFloater { get; set; }

    public DateTime UpdatedAt { get; set; }
}

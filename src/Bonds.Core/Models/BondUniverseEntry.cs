namespace Bonds.Core.Models;

/// <summary>
/// Задача 26 (банк облигаций MOEX) — одна строка снимка ВСЕЙ рыночной вселенной облигаций (не
/// только бумаг из портфеля/watchlist). Источник — MOEX ISS securities+marketdata (см.
/// <see cref="Bonds.Infrastructure.Connectors.Moex.MoexBondMarketRow"/> для сырых полей до
/// конвертации единиц). Обновляется целиком раз в час хостед-сервисом (upsert по <see cref="Secid"/>),
/// НЕ прогоняется через точный движок <see cref="Bonds.Core.Calculation.BondMetricsCalculator"/> —
/// это дешёвая биржевая статистика "как есть", точный расчёт — по требованию для избранных бумаг
/// (задача 27), см. doc-comment plan/26 "Архитектурная рамка".
/// <para>
/// <b>Единицы измерения (см. plan/26 "ЕДИНИЦЫ"):</b>
/// <see cref="YieldFraction"/> — ДОЛЯ (MOEX YIELD приходит в ПРОЦЕНТАХ, здесь уже разделено на 100:
/// 0.12 = 12%). <see cref="DurationYears"/> — ГОДЫ (MOEX DURATION приходит в ДНЯХ, здесь уже
/// разделено на 365). <see cref="PricePercent"/>/<see cref="BidPercent"/>/<see cref="OfferPercent"/> —
/// % от номинала, КАК ПРИШЛИ от MOEX (суффикс _percent — не переведены в доли/рубли).
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

    /// <summary>Эвристика по BONDTYPE ISS ("Флоатер") — см. <c>MoexSecurityInfo.LooksLikeFloater</c>.
    /// Null, если распознать не удалось (BONDTYPE отсутствует в ответе).</summary>
    public bool? IsFloater { get; set; }

    public DateTime UpdatedAt { get; set; }
}

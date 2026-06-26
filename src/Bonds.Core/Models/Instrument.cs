namespace Bonds.Core.Models;

/// <summary>
/// Справочник облигационного выпуска (общие данные, не зависящие от позиции пользователя).
/// Источник истины по справочным полям — MOEX ISS; T-Invest используется как вторичный
/// источник идентификаторов (Figi) при синке позиций (см. spec §4, plan/00 §4 "Разделение источников").
/// </summary>
public class Instrument
{
    public ulong Id { get; set; }

    /// <summary>ISIN — общий ключ маппинга между T-Invest и MOEX (spec §4.2).</summary>
    public string Isin { get; set; } = string.Empty;

    /// <summary>Биржевой идентификатор MOEX (SECID).</summary>
    public string? Secid { get; set; }

    /// <summary>Идентификатор T-Invest (FIGI).</summary>
    public string? Figi { get; set; }

    public string? Name { get; set; }

    public string? Issuer { get; set; }
    public string? Sector { get; set; }

    public decimal FaceValue { get; set; }

    /// <summary>Валюта номинала. MVP — только RUB (см. <see cref="IsOutOfScopeCurrency"/>).</summary>
    public string Currency { get; set; } = "RUB";

    public CouponType CouponType { get; set; } = CouponType.Fixed;

    public bool HasAmortization { get; set; }
    public bool HasOffers { get; set; }

    public DateOnly MaturityDate { get; set; }

    /// <summary>
    /// §4.4: данные по выпуску заведомо неполные (например, MOEX bondization не отдал часть купонов).
    /// Не падать, не подставлять нули молча — помечать и деградировать.
    /// </summary>
    public bool DataIncomplete { get; set; }

    /// <summary>
    /// §11: MVP — портфель рублёвый. Бумаги в иной валюте помечаются и исключаются
    /// из агрегатов/метрик, но не удаляются из справочника (точка расширения на будущее).
    /// </summary>
    public bool IsOutOfScopeCurrency { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>Тип купона (spec §5, §6 — обработка флоатеров отдельная).</summary>
public enum CouponType
{
    Fixed,
    Floating,
    Indexed,
}

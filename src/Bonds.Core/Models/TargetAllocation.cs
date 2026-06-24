namespace Bonds.Core.Models;

/// <summary>
/// Целевые доли/лимиты концентрации для триггеров и ребаланса (spec §5, опционально на MVP).
/// Не обязательная зависимость других модулей — используется только Signals Engine (этап 07),
/// если задана пользователем.
/// </summary>
public class TargetAllocation
{
    public ulong Id { get; set; }
    public ulong AccountId { get; set; }

    /// <summary>Эмитент, к которому относится лимит. Null — лимит/цель по портфелю в целом.</summary>
    public string? Issuer { get; set; }

    /// <summary>Целевая доля эмитента/портфеля в процентах (0-100).</summary>
    public decimal? TargetSharePercent { get; set; }

    /// <summary>Максимально допустимая доля по эмитенту — лимит концентрации (spec §8).</summary>
    public decimal? MaxConcentrationPercent { get; set; }

    /// <summary>Целевая модифицированная дюрация портфеля (для сигнала "дрейф дюрации", spec §8).</summary>
    public decimal? TargetDurationYears { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

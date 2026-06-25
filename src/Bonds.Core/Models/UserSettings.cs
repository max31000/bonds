namespace Bonds.Core.Models;

/// <summary>
/// Настройки пользователя (этап 08, GET/PUT /api/settings + PUT /api/settings/tinvest-token).
/// Single-user продукт — одна строка на пользователя (см. doc-comment <see cref="User"/>).
/// <see cref="TInvestTokenEncrypted"/> хранит токен ЗАШИФРОВАННЫМ через
/// <c>Microsoft.AspNetCore.DataProtection.IDataProtectionProvider</c> (см.
/// <c>Bonds.Infrastructure.Services.TInvestTokenProtector</c>) — никогда не отдаётся на фронт
/// в открытом виде (spec §11). Поля порогов Signals Engine — null означает "использовать
/// дефолт <see cref="Bonds.Core.Signals.SignalEngineOptions"/>" (этап 07).
/// </summary>
public class UserSettings
{
    public ulong Id { get; set; }
    public ulong UserId { get; set; }

    /// <summary>Токен T-Invest, зашифрованный IDataProtectionProvider. Null — не задан через UI (используется ENV).</summary>
    public string? TInvestTokenEncrypted { get; set; }

    /// <summary>Последние 4 символа токена в открытом виде — для отображения маски "...1234" (spec §11: не отдавать токен целиком).</summary>
    public string? TInvestTokenLast4 { get; set; }

    public int? UpcomingEventDaysThreshold { get; set; }
    public decimal? UninvestedCashThresholdRub { get; set; }
    public int? UninvestedCashLookbackDays { get; set; }
    public int? YieldBelowAlternativeBpsThreshold { get; set; }
    public int? MaturityWindowDaysForAlternativeComparison { get; set; }
    public decimal? DefaultMaxConcentrationPercent { get; set; }
    public decimal? DurationDriftToleranceYears { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

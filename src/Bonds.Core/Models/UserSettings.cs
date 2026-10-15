namespace Bonds.Core.Models;

/// <summary>
/// Настройки пользователя (GET/PUT /api/settings + PUT /api/settings/tinvest-token).
/// Single-user продукт — одна строка на пользователя (см. doc-comment <see cref="User"/>).
/// <see cref="TInvestTokenEncrypted"/> хранит токен ЗАШИФРОВАННЫМ через
/// <c>Microsoft.AspNetCore.DataProtection.IDataProtectionProvider</c> (см.
/// <c>Bonds.Infrastructure.Services.TInvestTokenProtector</c>) — никогда не отдаётся на фронт
/// в открытом виде (spec §11). Поля порогов Signals Engine — null означает "использовать
/// дефолт <see cref="Bonds.Core.Signals.SignalEngineOptions"/>".
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
    public decimal? DefaultMaxConcentrationPercent { get; set; }

    /// <summary>
    /// Ручной override ставки комиссии брокера — ДОЛЯ (0.0005 = 0.05%), не процент
    /// (конвенция бэкенда). Null — override не задан, <see cref="Bonds.Core.Interfaces.ICommissionRateProvider"/>
    /// переходит к автооценке из журнала/дефолту. Валидация диапазона (0, 0.05) — на границе API
    /// (SettingsEndpoints), не здесь — модель хранит уже провалидированное значение.
    /// </summary>
    public decimal? CommissionRateOverride { get; set; }

    /// <summary>
    /// Горизонт анализа замены, ГОДЫ (задача 48; <c>user_settings.replacement_horizon_years</c>, миграция 021).
    /// Диапазон 0.5–10, шаг 0.5 — валидация на границе API (SettingsEndpoints). Не null: у новой записи и
    /// у существующих строк = 2.0 (прежняя фронтовая константа).
    /// </summary>
    public decimal ReplacementHorizonYears { get; set; } = DefaultReplacementHorizonYears;

    /// <summary>Дефолт горизонта анализа замены, годы (совпадает с DEFAULT колонки в миграции 021).</summary>
    public const decimal DefaultReplacementHorizonYears = 2.0m;

    /// <summary>
    /// Порог события SpreadWidening, ДОЛЯ (0.0050 = 50 б.п.; задача 49). Null — дефолт
    /// <see cref="Bonds.Core.Signals.SignalEngineOptions.SpreadWideningThresholdFraction"/>. Диапазон — на границе API.
    /// </summary>
    public decimal? SpreadWideningThresholdFraction { get; set; }

    /// <summary>
    /// Порог события PriceDrawdown, ДОЛЯ (0.05 = 5%; задача 49). Null — дефолт
    /// <see cref="Bonds.Core.Signals.SignalEngineOptions.PriceDrawdownThresholdFraction"/>. Диапазон — на границе API.
    /// </summary>
    public decimal? PriceDrawdownThresholdFraction { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

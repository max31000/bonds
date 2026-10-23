namespace Bonds.Infrastructure.Sync;

/// <summary>Настройки истории курсов валют (секция конфигурации <c>CurrencyRates</c>, задача 60).</summary>
public sealed class CurrencyRateOptions
{
    public const string SectionName = "CurrencyRates";

    /// <summary>
    /// ISO-коды валют, чья история курса ведётся ВСЕГДА (даже без позиции в T-Invest): нужна для оценки брокерских позиций в рублях.
    /// Источники — <see cref="Bonds.Core.Currencies.TrackedCurrencies"/>; неизвестный код пропускается.
    /// </summary>
    public string[] AlwaysTrackedCurrencies { get; set; } = ["USD", "EUR", "GBP"];
}

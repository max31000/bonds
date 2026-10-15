namespace Bonds.Core.Models;

/// <summary>
/// Брокерская оценка риска облигации T-Invest (<c>Bond.risk_level</c>, instruments.proto:502, enum 0–3).
/// Числовые значения совпадают с протобуф-enum и хранятся в <c>instrument_risk_levels.risk_level</c>.
/// </summary>
public enum BrokerRiskLevel
{
    /// <summary>RISK_LEVEL_UNSPECIFIED: брокер оценку не дал — для скоркарты то же, что «нет данных».</summary>
    Unspecified = 0,
    Low = 1,
    Moderate = 2,
    High = 3,
}

/// <summary>
/// Задача 46: суточный кэш <c>risk_level</c> облигации из T-Invest (один bulk-вызов <c>Instruments.Bonds()</c>).
/// Ключ — ISIN; таблица отдельна от <c>instruments</c>, чтобы покрывать весь банк, а не только бумаги портфеля.
/// <see cref="FetchedAt"/> — UTC.
/// </summary>
public sealed record InstrumentRiskLevel(string Isin, BrokerRiskLevel RiskLevel, DateTime FetchedAt);

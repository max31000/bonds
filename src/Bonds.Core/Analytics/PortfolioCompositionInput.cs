using Bonds.Core.Models;

namespace Bonds.Core.Analytics;

/// <summary>
/// Один holding для расчёта композиции/сравнения портфеля (plan/06 B2/B3) — объединяет позицию,
/// справочные данные инструмента и (опционально) уже посчитанные метрики движка
/// (<see cref="Bonds.Core.Calculation.BondMetrics"/>). Сборка из репозиториев + вызов
/// <see cref="Bonds.Core.Calculation.BondMetricsCalculator"/> — ответственность вызывающего
/// слоя (Infrastructure); этот record — просто плоский вход для чистых аналитических сервисов.
/// </summary>
public sealed record PortfolioHolding
{
    public required ulong PositionId { get; init; }
    public required ulong InstrumentId { get; init; }
    public required decimal Quantity { get; init; }

    /// <summary>Рыночная стоимость holding'а в рублях (Quantity × грязная цена) — база для весов композиции.</summary>
    public required decimal MarketValueRub { get; init; }

    public string? Name { get; init; }
    public string? Isin { get; init; }
    public string? Issuer { get; init; }
    public string? Sector { get; init; }
    public required CouponType CouponType { get; init; }

    public required DateOnly MaturityDate { get; init; }

    /// <summary>Ближайший релевантный горизонт (оферта либо погашение) — для колонки "дни до погашения/оферты" (spec §9).</summary>
    public required DateOnly HorizonDate { get; init; }
    public required bool IsCalculatedToOffer { get; init; }

    /// <summary>Модифицированная дюрация (в годах) — null, если метрика не посчиталась (флоатер без дюрации/неполные данные).</summary>
    public decimal? ModifiedDuration { get; init; }

    /// <summary>Дюрация Маколея (в годах) — единый измеритель «срока» для scatter-оси и G-спреда (T-7).
    /// Null там же, где и модифицированная.</summary>
    public decimal? MacaulayDuration { get; init; }

    /// <summary>Выпуклость (convexity) в годах² — null если не посчиталась (флоатер/неполные данные).</summary>
    public decimal? Convexity { get; init; }

    /// <summary>YTM (эффективная) — null для флоатера/индексируемой бумаги или несошедшегося расчёта.</summary>
    public decimal? YtmEffective { get; init; }

    /// <summary>Текущая купонная доходность — обязательная замена YTM для флоатера/индексируемой бумаги (spec §6/§9).</summary>
    public decimal? CurrentYield { get; init; }

    public decimal? GSpread { get; init; }

    public required bool IsFloater { get; init; }
    public required bool IsIndexed { get; init; }
    public required bool IsEstimated { get; init; }
    public required bool DataIncomplete { get; init; }

    /// <summary>§11/§3: номинал в иностранной валюте — бумага вне рублёвого контура MVP. У таких
    /// YTM/дюрация/G-спред не считаются; UI помечает бейджем, агрегаты их не искажают.</summary>
    public bool IsOutOfScopeCurrency { get; init; }
}

using Bonds.Api.Auth;
using Bonds.Api.Middleware;
using Bonds.Core.Analytics;
using Bonds.Core.Calculation;
using Bonds.Core.CashFlow;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Interfaces;
using Bonds.Core.Models;
using Bonds.Core.Time;
using Bonds.Core.Universe;
using Bonds.Infrastructure.Analytics;
using Bonds.Infrastructure.Universe;
using Microsoft.Extensions.Options;
using static Bonds.Api.Endpoints.AnalyticsSupport;
using static Bonds.Core.Analytics.RelativeValueService;

namespace Bonds.Api.Endpoints;

public sealed record AllocationResponseDto
{
    public required decimal AmountRub { get; init; }

    /// <summary>Эхо запрошенного источника кандидатов — "portfolio"|"market"|"recommended".</summary>
    public required string Source { get; init; }

    public required IReadOnlyList<AllocationLineDto> Allocations { get; init; }
    public required IReadOnlyList<AllocationSkipDto> Skipped { get; init; }
    public required decimal LeftoverRub { get; init; }
    public required string Disclaimer { get; init; }

    /// <summary>Ставка комиссии покупки, применённая к цене лота — ДОЛЯ (резолвер части C).</summary>
    public required decimal CommissionRateUsed { get; init; }

    /// <summary>Источник ставки — строка <see cref="CommissionRateSource"/>.</summary>
    public required string CommissionRateSource { get; init; }

    /// <summary>Только для Source=market/recommended — сколько кандидатов
    /// реально ПОДХОДИЛО (после гигиенического + [для recommended] риск-фильтра) ДО отсечения
    /// потолком <see cref="AllocationEndpoints.MarketAllocationCandidatePoolLimit"/>. Null для
    /// Source=portfolio.</summary>
    public int? CandidatePoolAvailable { get; init; }

    /// <summary>Только для Source=market/recommended — верхняя граница числа
    /// кандидатов, реально участвовавших в жадном проходе
    /// (<see cref="AllocationEndpoints.MarketAllocationCandidatePoolLimit"/>). Null для Source=portfolio.</summary>
    public int? CandidatePoolLimit { get; init; }

    /// <summary>true — <see cref="CandidatePoolAvailable"/> &gt;
    /// <see cref="CandidatePoolLimit"/>, потолок реально обрезал часть подходящих кандидатов ДО
    /// жадного прохода (план требует "не молча" — см. также текст <see cref="Disclaimer"/>, который
    /// в этом случае дописывает фактические числа). Null для Source=portfolio.</summary>
    public bool? CandidatePoolTruncated { get; init; }
}

public sealed record AllocationLineDto
{
    /// <summary>Null для Source=market/recommended — банк-кандидат не связан с таблицей
    /// Instrument, см. doc-comment <c>CashAllocationCandidate.InstrumentId</c>. Идентификатор для
    /// таких строк — <see cref="Secid"/>.</summary>
    public required ulong? InstrumentId { get; init; }

    /// <summary>Биржевой SECID — только для Source=market/recommended. Null для Source=portfolio.</summary>
    public string? Secid { get; init; }

    public string? Name { get; init; }
    public string? Issuer { get; init; }

    /// <summary>Грубая секторная классификация банка ("Гособлигации"/"Муниципальные"/
    /// "Корпоративные") — заполнена для Source=market/recommended, null для Source=portfolio.</summary>
    public string? Sector { get; init; }

    public required decimal Quantity { get; init; }
    public required decimal EstimatedCostRub { get; init; }
    public required decimal EffectiveYield { get; init; }
    public required bool LotSizeAssumed { get; init; }

    /// <summary>Разложение EstimatedCostRub — чистая цена всей докупки (без НКД/комиссии). CleanCostRub + AccruedCostRub + CommissionCostRub = EstimatedCostRub.</summary>
    public decimal CleanCostRub { get; init; }

    /// <summary>НКД в составе EstimatedCostRub.</summary>
    public decimal AccruedCostRub { get; init; }

    /// <summary>Комиссия покупки в составе EstimatedCostRub.</summary>
    public decimal CommissionCostRub { get; init; }

    /// <summary>Риск-сигналы (см. <see cref="RiskSignalsDto"/>) —
    /// заполнены для Source=market/recommended, null для Source=portfolio (план допускает эту
    /// более простую альтернативу вместо resolve-по-ISIN на каждую строку портфеля).</summary>
    public RiskSignalsDto? RiskSignals { get; init; }
}

public sealed record AllocationSkipDto
{
    /// <summary>Null для Source=market/recommended — см. doc-comment <see cref="AllocationLineDto.InstrumentId"/>.</summary>
    public required ulong? InstrumentId { get; init; }

    /// <summary>См. <see cref="AllocationLineDto.Secid"/>.</summary>
    public string? Secid { get; init; }

    public string? Name { get; init; }
    public string? Issuer { get; init; }
    public required string Reason { get; init; }
}

// ─── POST /api/analytics/basket ───────────────────────────────────────────────

/// <summary>Тело запроса POST /api/analytics/basket — сумма + строки корзины (доля, НЕ проценты — конвенция репо).</summary>
public sealed record BasketRequestDto
{
    public required decimal AmountRub { get; init; }
    public required IReadOnlyList<BasketRequestLineDto> Lines { get; init; }
}

public sealed record BasketRequestLineDto
{
    public required ulong InstrumentId { get; init; }

    /// <summary>Целевая доля суммы корзины — доля (0, 1], НЕ проценты.</summary>
    public required decimal WeightFraction { get; init; }
}

/// <summary>Ответ POST /api/analytics/basket — собранная корзина + what-if всего портфеля.</summary>
public sealed record BasketResponseDto
{
    public required BasketDto Basket { get; init; }
    public required WhatIfDto WhatIf { get; init; }
    public required string Disclaimer { get; init; }
}

public sealed record BasketDto
{
    public required decimal AmountRub { get; init; }
    public required IReadOnlyList<BasketLineDto> Lines { get; init; }
    public required decimal LeftoverRub { get; init; }
    public required BasketMetricsDto Metrics { get; init; }
}

public sealed record BasketLineDto
{
    public required ulong InstrumentId { get; init; }
    public string? Name { get; init; }
    public string? Issuer { get; init; }
    public required decimal TargetWeightFraction { get; init; }
    public required decimal ActualWeightFraction { get; init; }
    public required decimal Quantity { get; init; }
    public required decimal ActualCostRub { get; init; }
    public decimal? EffectiveYield { get; init; }
    public decimal? ModifiedDuration { get; init; }
    public bool IsFloater { get; init; }
    public required bool LotSizeAssumed { get; init; }
    public decimal CleanCostRub { get; init; }
    public decimal AccruedCostRub { get; init; }
    public decimal CommissionCostRub { get; init; }
}

public sealed record BasketMetricsDto
{
    public required decimal TotalCostRub { get; init; }
    public decimal? WeightedYield { get; init; }
    public decimal? WeightedDuration { get; init; }
    public required bool HasExcludedFloaters { get; init; }
}

public sealed record WhatIfDto
{
    public required WhatIfSnapshotDto Before { get; init; }
    public required WhatIfSnapshotDto After { get; init; }
    public required IReadOnlyList<WhatIfConcentrationDto> Concentrations { get; init; }
    public required IReadOnlyList<WhatIfWarningDto> Warnings { get; init; }
}

public sealed record WhatIfSnapshotDto
{
    public required decimal TotalValueRub { get; init; }
    public decimal? WeightedYield { get; init; }
    public decimal? WeightedDuration { get; init; }
    public required bool HasExcludedFloaters { get; init; }
}

public sealed record WhatIfConcentrationDto
{
    public required string Issuer { get; init; }
    public required decimal SharePercentBefore { get; init; }
    public required decimal SharePercentAfter { get; init; }
}

public sealed record WhatIfWarningDto
{
    public required string Kind { get; init; }
    public required string Issuer { get; init; }
    public required decimal SharePercentAfter { get; init; }
}

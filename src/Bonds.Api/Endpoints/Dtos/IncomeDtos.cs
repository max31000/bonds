using Bonds.Core.Income;

namespace Bonds.Api.Endpoints;

/// <summary>
/// Ответ <c>GET /api/income</c> (задача 52). Деньги — РУБЛИ, доли — ДОЛИ (0.12 = 12%). Enum-значения сериализуются
/// camelCase-строками (assetKind: "bond"/"fund"; status: "open"/"closed"/"redeemed").
/// </summary>
public sealed record IncomeResponseDto
{
    public required DateOnly AsOf { get; init; }

    /// <summary>Период (включительно) или null — всё время.</summary>
    public DateOnly? From { get; init; }
    public DateOnly? To { get; init; }

    /// <summary>Применённый фильтр: "all" | "bond" | "fund".</summary>
    public required string AssetKind { get; init; }

    /// <summary>Итоги all-time по показанным позициям (+ налоги/комиссии «по счёту» в итоге).</summary>
    public required IncomeTotalsDto Totals { get; init; }

    public required IncomeAccountLevelDto AccountLevel { get; init; }

    /// <summary>Денежные события периода (без бумажной прибыли); без периода равны all-time.</summary>
    public required IncomePeriodDto Period { get; init; }

    /// <summary>Помесячный ряд по периоду (ось ровная — пустые месяцы нулевые).</summary>
    public required IReadOnlyList<IncomeMonthDto> Monthly { get; init; }

    public required IReadOnlyList<IncomePositionDto> Positions { get; init; }

    /// <summary>XIRR портфеля (облигации + фонды выбранного фильтра), ДОЛЯ годовых; деньги на счёте и пополнения не участвуют.</summary>
    public decimal? PortfolioXirrFraction { get; init; }

    /// <summary>Всегда true: бумажная прибыль (<c>unrealizedPnlRub</c>) — за всё время, периодом не режется.</summary>
    public bool UnrealizedIsAllTime { get; init; } = true;

    public required string Disclaimer { get; init; }
}

public sealed record IncomeTotalsDto
{
    public required int PositionsCount { get; init; }
    public required decimal InvestedTotalRub { get; init; }
    public required decimal MarketValueCleanRub { get; init; }
    public required decimal AccruedNowRub { get; init; }
    public required decimal UnrealizedPnlRub { get; init; }
    public required decimal RealizedPnlRub { get; init; }
    public required decimal IncomeGrossRub { get; init; }
    public required decimal AccruedPaidRub { get; init; }
    public required decimal AccruedReceivedRub { get; init; }
    public required decimal NetCouponIncomeRub { get; init; }

    /// <summary>Налоги по позициям (знак брокера, обычно −); налоги «по счёту» — в <see cref="IncomeAccountLevelDto"/>.</summary>
    public required decimal TaxesRub { get; init; }
    public required decimal FeesRub { get; init; }
    public required decimal PrincipalReturnedRub { get; init; }

    /// <summary>Итог = Σ итогов позиций + налоги/комиссии «по счёту».</summary>
    public required decimal TotalResultRub { get; init; }

    /// <summary>TotalResult / InvestedTotal — ДОЛЯ.</summary>
    public decimal? TotalReturnFraction { get; init; }
}

public sealed record IncomeAccountLevelDto
{
    public required decimal TaxesRub { get; init; }
    public required decimal FeesRub { get; init; }
}

public sealed record IncomePeriodDto
{
    public required decimal CouponsGrossRub { get; init; }
    public required decimal DistributionsGrossRub { get; init; }
    public required decimal AccruedPaidRub { get; init; }
    public required decimal AccruedReceivedRub { get; init; }
    public required decimal TaxesRub { get; init; }
    public required decimal FeesRub { get; init; }
    public required decimal RealizedPnlRub { get; init; }
    public required decimal NetRub { get; init; }
}

public sealed record IncomeMonthDto
{
    /// <summary>"yyyy-MM".</summary>
    public required string Month { get; init; }
    public required decimal CouponsGrossRub { get; init; }
    public required decimal DistributionsGrossRub { get; init; }

    /// <summary>−уплачен + получен.</summary>
    public required decimal AccruedNetRub { get; init; }
    public required decimal TaxesRub { get; init; }
    public required decimal FeesRub { get; init; }
    public required decimal RealizedPnlRub { get; init; }
    public required decimal NetRub { get; init; }
}

/// <summary>Позиция журнала доходов (включая закрытые/погашенные). Поля — <see cref="PositionIncome"/> + метаданные + периодные.</summary>
public sealed record IncomePositionDto
{
    public required AssetKind AssetKind { get; init; }
    public required ulong AssetId { get; init; }
    public string? Name { get; init; }
    public string? Ticker { get; init; }
    public string? Isin { get; init; }

    /// <summary>Id позиции облигации (для ссылки на карточку); null у закрытых и фондов.</summary>
    public ulong? PositionId { get; init; }
    public ulong? FundId { get; init; }

    /// <summary>Расчёт актива упал или нет котировки/цены — числа приблизительные (у упавшего — нули).</summary>
    public required bool DataIncomplete { get; init; }

    public required PositionIncomeStatus Status { get; init; }
    public required DateOnly FirstDate { get; init; }
    public required DateOnly LastDate { get; init; }
    public required int HoldingDays { get; init; }
    public required decimal QuantityNow { get; init; }
    public required decimal InvestedTotalRub { get; init; }
    public required decimal CostRemainingRub { get; init; }
    public required decimal MarketValueCleanRub { get; init; }
    public required decimal AccruedNowRub { get; init; }
    public decimal? UnrealizedPnlRub { get; init; }
    public required decimal RealizedPnlRub { get; init; }
    public required decimal IncomeGrossRub { get; init; }
    public required decimal AccruedPaidRub { get; init; }
    public required decimal AccruedReceivedRub { get; init; }
    public required decimal NetCouponIncomeRub { get; init; }
    public required decimal TaxesRub { get; init; }
    public required decimal FeesRub { get; init; }
    public required decimal PrincipalReturnedRub { get; init; }
    public required decimal TotalResultRub { get; init; }

    /// <summary>ДОЛЯ.</summary>
    public decimal? TotalReturnFraction { get; init; }

    /// <summary>ДОЛЯ годовых.</summary>
    public decimal? XirrFraction { get; init; }
    public required bool HasUnknownLots { get; init; }
    public required bool AccruedUnknown { get; init; }

    // Денежные события в выбранном периоде (без периода = all-time).
    public required decimal PeriodIncomeGrossRub { get; init; }
    public required decimal PeriodRealizedPnlRub { get; init; }
    public required decimal PeriodTaxesRub { get; init; }
    public required decimal PeriodFeesRub { get; init; }
}

/// <summary>Ответ <c>GET /api/income/{assetKind}/{assetId}/ledger</c> — хронология операций актива.</summary>
public sealed record IncomeLedgerResponseDto
{
    public required AssetKind AssetKind { get; init; }
    public required ulong AssetId { get; init; }
    public string? Name { get; init; }
    public string? Ticker { get; init; }
    public string? Isin { get; init; }
    public required PositionIncomeStatus Status { get; init; }
    public required IReadOnlyList<IncomeLedgerEntryDto> Entries { get; init; }
    public required string Disclaimer { get; init; }
}

public sealed record IncomeLedgerEntryDto
{
    public required DateTime Date { get; init; }

    /// <summary>"buy" | "sell" | "income" | "tax" | "fee" | "principalReturn" | "redemption" | "securityIn" | "securityOut".</summary>
    public required LedgerEntryKind Kind { get; init; }

    /// <summary>РУБЛИ, знак брокера (покупка −, поступление +).</summary>
    public required decimal AmountRub { get; init; }
    public decimal? Quantity { get; init; }

    /// <summary>НКД операции, РУБЛИ на всю операцию, величина ≥ 0 (как в БД; null — неизвестен).</summary>
    public decimal? AccruedRub { get; init; }

    /// <summary>Остаток после операции (по журналу).</summary>
    public required decimal QuantityAfter { get; init; }

    /// <summary>Средняя чистая цена остатка за штуку после операции, рубли.</summary>
    public required decimal AvgCostAfterRub { get; init; }

    /// <summary>Реализованный результат, признанный на этой операции, рубли.</summary>
    public required decimal RealizedPnlRub { get; init; }
}

using Bonds.Core.Funds;
using Bonds.Core.Models;

namespace Bonds.Api.Endpoints;

/// <summary>
/// Контракты <c>/api/funds*</c> и <c>/api/portfolio/summary</c> (задача 53). Деньги — РУБЛИ (цены — за пай), все доходности и доли —
/// ДОЛИ (0.12 = 12%; ×100 только во фронт-форматтерах). Enum сериализуются camelCase-строками.
/// P&amp;L-поля берутся из <c>PositionIncomeCalculator</c> (метод средней цены) — та же методика, что у страницы «Доходы».
/// </summary>
public sealed record FundsResponseDto
{
    public required DateOnly AsOf { get; init; }
    public required IReadOnlyList<FundRowDto> Funds { get; init; }
    public required FundsTotalsDto Totals { get; init; }
    public required string Disclaimer { get; init; }
}

/// <summary>Итоги по показанным фондам. <c>IncomeNetRub</c> = выплаты брутто + налоги (налог со знаком брокера, обычно −).</summary>
public sealed record FundsTotalsDto
{
    public required decimal MarketValueRub { get; init; }

    /// <summary>Себестоимость остатка (average cost), рубли.</summary>
    public required decimal InvestedRub { get; init; }

    public required decimal UnrealizedPnlRub { get; init; }
    public required decimal IncomeNetRub { get; init; }
    public required decimal TotalResultRub { get; init; }
}

public sealed record FundWindowReturnDto
{
    /// <summary>Доходность за окно, ДОЛЯ.</summary>
    public required decimal ReturnFraction { get; init; }

    /// <summary>Пересчёт в годовую, ДОЛЯ годовых.</summary>
    public required decimal AnnualizedFraction { get; init; }

    public required int Days { get; init; }
}

public sealed record FundPerformanceDto
{
    public FundWindowReturnDto? M1 { get; init; }
    public FundWindowReturnDto? M3 { get; init; }
    public FundWindowReturnDto? M6 { get; init; }
    public FundWindowReturnDto? M12 { get; init; }
    public FundWindowReturnDto? SinceFirstBuy { get; init; }
}

public sealed record FundOutlookDto
{
    /// <summary>Метод, выбранный пользователем (auto | distributions | indexYield | trailingReturn).</summary>
    public required FundForecastMethod RequestedMethod { get; init; }

    /// <summary>Применённый метод (при Auto — то, во что он разрешился).</summary>
    public required FundForecastMethod Method { get; init; }

    /// <summary>Ожидаемая годовая доходность, ДОЛЯ; null — оценка недоступна (см. <see cref="UnavailableReason"/>).</summary>
    public decimal? ExpectedAnnualYieldFraction { get; init; }

    /// <summary>После НДФЛ (единая ставка 13%, налог ко всей доходности — упрощение), ДОЛЯ.</summary>
    public decimal? AfterTaxYieldFraction { get; init; }

    public required string BasisLabel { get; init; }
    public decimal? ExpectedIncome1yRub { get; init; }
    public decimal? ExpectedMonthlyIncomeRub { get; init; }
    public required FundOutlookConfidence Confidence { get; init; }
    public required DateOnly AsOf { get; init; }
    public required IReadOnlyList<FundOutlookWarning> Warnings { get; init; }
    public string? UnavailableReason { get; init; }

    public static FundOutlookDto From(FundOutlook o) => new()
    {
        RequestedMethod = o.RequestedMethod,
        Method = o.Method,
        ExpectedAnnualYieldFraction = o.ExpectedAnnualYieldFraction,
        AfterTaxYieldFraction = o.AfterTaxYieldFraction,
        BasisLabel = o.BasisLabel,
        ExpectedIncome1yRub = o.ExpectedIncome1yRub,
        ExpectedMonthlyIncomeRub = o.ExpectedMonthlyIncomeRub,
        Confidence = o.Confidence,
        AsOf = o.AsOf,
        Warnings = o.Warnings,
        UnavailableReason = o.UnavailableReason,
    };
}

public sealed record FundRowDto
{
    public required ulong FundId { get; init; }
    public string? Ticker { get; init; }
    public string? Name { get; init; }
    public string? ManagementCompany { get; init; }

    /// <summary>Паёв сейчас (0 — фонд продан).</summary>
    public required decimal Quantity { get; init; }

    /// <summary>Цена пая, рубли за пай (брокер; нет позиции — последнее закрытие MOEX); null — цены нет.</summary>
    public decimal? CurrentPriceRub { get; init; }

    public DateTime? PriceAsOf { get; init; }
    public required decimal MarketValueRub { get; init; }

    /// <summary>Средняя цена остатка за пай (average cost журнала), рубли; у проданного — 0.</summary>
    public required decimal AvgCostRub { get; init; }

    /// <summary>Себестоимость остатка, рубли (average cost) — база для P&amp;L %. Σ всех покупок — в <c>/api/income</c>.</summary>
    public required decimal InvestedRub { get; init; }

    public decimal? UnrealizedPnlRub { get; init; }

    /// <summary>Нереализованный P&amp;L / себестоимость остатка, ДОЛЯ; null — себестоимости нет.</summary>
    public decimal? UnrealizedPnlFraction { get; init; }

    /// <summary>Выплаты фонда брутто (до налога), рубли.</summary>
    public required decimal DistributionsGrossRub { get; init; }

    /// <summary>Налоги по фонду со знаком брокера (удержание −), рубли.</summary>
    public required decimal TaxesRub { get; init; }

    /// <summary>Выплаты за вычетом налогов (= brutto + taxes), рубли.</summary>
    public required decimal DistributionsNetRub { get; init; }

    public required decimal TotalResultRub { get; init; }
    public decimal? TotalReturnFraction { get; init; }
    public decimal? XirrFraction { get; init; }

    /// <summary>TER фонда, ДОЛЯ годовых (0.009 = 0.9%); null — неизвестен.</summary>
    public decimal? TotalExpenseFraction { get; init; }

    public required FundPerformanceDto Performance { get; init; }
    public required FundOutlookDto Outlook { get; init; }

    /// <summary>Доля фонда в (облигации + фонды), ДОЛЯ; деньги не входят; null — стоимость портфеля 0.</summary>
    public decimal? ShareOfPortfolioFraction { get; init; }

    /// <summary>Журнал не покрывает весь остаток — P&amp;L приблизительный.</summary>
    public required bool HasUnknownLots { get; init; }

    /// <summary>Нет цены пая — стоимость принята 0.</summary>
    public required bool DataIncomplete { get; init; }
}

public sealed record FundPricePointDto
{
    public required DateOnly Date { get; init; }
    public required decimal CloseRub { get; init; }
}

public sealed record FundDistributionDto
{
    public required DateOnly RecordDate { get; init; }
    public DateOnly? PaymentDate { get; init; }
    public required decimal AmountPerShareRub { get; init; }

    /// <summary>Выплата / цена пая на дату фиксации, ДОЛЯ за период (не годовая); null — цены нет.</summary>
    public decimal? YieldOnPriceFraction { get; init; }
}

public sealed record FundOperationDto
{
    public required DateTime Date { get; init; }
    public required FundOperationType Type { get; init; }

    /// <summary>Сумма со знаком брокера (покупка −, выплата +), рубли.</summary>
    public required decimal AmountRub { get; init; }

    public decimal? Quantity { get; init; }
}

public sealed record FundSettingsDto
{
    public required FundForecastMethod ForecastMethod { get; init; }
    public string? BenchmarkIndex { get; init; }
}

public sealed record FundDetailDto
{
    public required DateOnly AsOf { get; init; }
    public required FundRowDto Fund { get; init; }

    /// <summary>Закрытия пая за ≤ 2 года, по возрастанию даты.</summary>
    public required IReadOnlyList<FundPricePointDto> PriceHistory { get; init; }

    public required IReadOnlyList<FundDistributionDto> Distributions { get; init; }
    public required IReadOnlyList<FundOperationDto> Operations { get; init; }
    public required FundSettingsDto Settings { get; init; }
    public required string Disclaimer { get; init; }
}

/// <summary>Тело <c>PUT /api/funds/{id}/settings</c>.</summary>
public sealed record UpdateFundSettingsRequest
{
    public string? ForecastMethod { get; init; }
    public string? BenchmarkIndex { get; init; }
}

/// <summary>Ответ <c>GET /api/portfolio/summary</c>. Деньги — РУБЛИ; XIRR — ДОЛЯ годовых.</summary>
public sealed record PortfolioSummaryDto
{
    public required decimal BondsValueRub { get; init; }
    public required decimal FundsValueRub { get; init; }

    /// <summary>Свободные деньги на счёте по последнему синку; null — баланс ещё не синкался.</summary>
    public decimal? CashRub { get; init; }

    /// <summary>Облигации + фонды + деньги (null-деньги = 0).</summary>
    public required decimal TotalRub { get; init; }

    /// <summary>Полная стоимость счёта по оценке брокера — для сверки.</summary>
    public decimal? BrokerTotalRub { get; init; }

    /// <summary>BrokerTotal − Total (положительная — брокер показывает больше: другие активы/валюта); null — брокерского итога нет.</summary>
    public decimal? BrokerDiffRub { get; init; }

    public DateTime? BalanceSyncedAt { get; init; }

    /// <summary>XIRR всего портфеля (облигации + фонды) — тот же расчёт, что на странице «Доходы».</summary>
    public decimal? PortfolioXirrFraction { get; init; }

    public required string Disclaimer { get; init; }
}

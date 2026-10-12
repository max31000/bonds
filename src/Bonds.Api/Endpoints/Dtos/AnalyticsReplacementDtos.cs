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

/// <summary>Verdict одной позиции (план часть C.1): Cheap — спред заметно выше медианы корзины
/// (рынок недооценивает/закладывает риск), Rich — заметно ниже (дорого относительно соседей,
/// кандидат на пересмотр), Fair — в пределах порога (см. <see cref="SpreadThresholds.DeviationFraction"/>).</summary>
public enum RelativeValueVerdict
{
    Cheap,
    Fair,
    Rich,
}

public sealed record RelativeValueResponseDto
{
    public required IReadOnlyList<RelativeValuePositionDto> Positions { get; init; }
    public required string Disclaimer { get; init; }
}

public sealed record RelativeValuePositionDto
{
    public required ulong PositionId { get; init; }
    public required RelativeValueBasketDto Basket { get; init; }

    /// <summary>ДОЛЯ (0.002 = 20 б.п., см. общая конвенция единиц репо — б.п. рисует фронт formatBp).</summary>
    public required decimal DeviationFraction { get; init; }

    /// <summary>Перцентиль внутри эффективной корзины (0-100).</summary>
    public required decimal Percentile { get; init; }

    public required string Verdict { get; init; }

    /// <summary>Сколько торговых дней истории легло в основу сглаженной медианы (0 — банк молодой,
    /// использован единственный текущий снимок, см. RelativeValueSnapshotBuilder).</summary>
    public required int BasedOnDays { get; init; }

    /// <summary>Топ-3 дешёвых кандидата из ЕЁ ЖЕ корзины — только для Rich-позиций, иначе пусто.</summary>
    public required IReadOnlyList<RelativeValueCandidateDto> CheapCandidates { get; init; }
}

public sealed record RelativeValueBasketDto
{
    public required string Sector { get; init; }
    public required string DurationBucket { get; init; }
    public required int Count { get; init; }

    /// <summary>High/Medium/Low — см. <see cref="RelativeValueService.RelativeValueConfidence"/>.</summary>
    public required string Confidence { get; init; }
}

public sealed record RelativeValueCandidateDto
{
    public required string Secid { get; init; }
    public string? Name { get; init; }
    public decimal? YieldFraction { get; init; }

    /// <summary>ДОЛЯ — положительное значение (кандидат «дешевле» медианы своей корзины).</summary>
    public required decimal DeviationFraction { get; init; }

    /// <summary>High/Medium/Low/None — см. <see cref="Bonds.Core.Universe.LiquidityScore"/>.</summary>
    public required string LiquidityScore { get; init; }
}

/// <summary>
/// Ответ GET /api/analytics/replacement-candidates. Схема — контракт для
/// фронтовой задачи 35 (зеркалит буквально), не менять без согласования.
/// </summary>
public sealed record ReplacementCandidatesResponseDto
{
    /// <summary>"market" | "rv" — эхо запрошенного режима.</summary>
    public required string Mode { get; init; }

    public required string PositionIsin { get; init; }
    public required IReadOnlyList<ReplacementCandidateDto> Candidates { get; init; }
    public required string Disclaimer { get; init; }
}

/// <summary>Один кандидат-замена — дешёвая банк-статистика (bond_universe), НЕ точный расчёт
/// движка (см. doc-comment <see cref="ReplacementEndpoints.GetReplacementCandidates"/>).</summary>
public sealed record ReplacementCandidateDto
{
    public required string Secid { get; init; }
    public string? Isin { get; init; }
    public required string Name { get; init; }

    /// <summary>Банк-слой не хранит эмитента — всегда null (issuer-поле — задача 34).</summary>
    public string? Issuer { get; init; }

    public string? Sector { get; init; }

    /// <summary>ДОЛЯ (см. общая конвенция единиц репо).</summary>
    public decimal? YieldFraction { get; init; }

    /// <summary>Годы.</summary>
    public decimal? DurationYears { get; init; }

    /// <summary>ДОЛЯ; приближённый G-спред банка (<c>BondUniverseEntry.GspreadApproxFraction</c>).</summary>
    public decimal? GSpreadFraction { get; init; }

    /// <summary>Дата оферты банк-записи (<see cref="BondUniverseEntry.OfferDate"/>),
    /// если она есть — доходность бумаги с офертой считается движком К ОФЕРТЕ, не к погашению,
    /// горизонт другой. Null — оферты нет/не известна.</summary>
    public DateOnly? OfferDate { get; init; }

    public required RiskSignalsDto RiskSignals { get; init; }
}

/// <summary>
/// Два ИНФОРМАЦИОННЫХ риск-сигнала кандидата (не рейтинг кредитного качества,
/// не «надёжность» — см. doc-comment <see cref="Bonds.Core.Analytics.SignalLevel"/>): ликвидность+
/// листинг и отклонение спреда от медианы корзины. Уровни не ранжируют кандидатов — ранжирование
/// mode=market идёт по доходности.
/// </summary>
public sealed record RiskSignalsDto
{
    /// <summary>"Good"|"Neutral"|"Caution" — см. <see cref="Bonds.Core.Analytics.SignalLevel"/>.</summary>
    public required string Liquidity { get; init; }

    /// <summary>Человекочитаемая подпись, напр. "Высокая ликвидность, листинг 1".</summary>
    public required string LiquidityLabel { get; init; }

    /// <summary>"Good"|"Neutral"|"Caution".</summary>
    public required string Spread { get; init; }

    /// <summary>ДОЛЯ; эхо G-спреда кандидата. Null, если MOEX не отдал спред.</summary>
    public decimal? GSpreadFraction { get; init; }

    /// <summary>ДОЛЯ; GSpreadFraction − медиана корзины кандидата. Знак: положительное — спред
    /// выше медианы (рынок закладывает бОльшую премию/риск). Null вместе с GSpreadFraction.</summary>
    public decimal? SpreadVsBasketMedianFraction { get; init; }

    /// <summary>
    /// Светофор надёжности: "Green"|"Yellow"|"Red" (см.
    /// <see cref="Bonds.Core.Analytics.ReliabilityLight"/>/<see cref="Bonds.Core.Analytics.CandidateRiskSignalService.Aggregate"/>
    /// для точной матрицы). Аддитивное поле — существующие Liquidity/Spread не меняются.
    /// <b>НЕ кредитный рейтинг</b> — сигнал по биржевой статистике, формулировка "рейтинг"
    /// запрещена планом задачи 38 везде в UI/API.
    /// </summary>
    public required string Reliability { get; init; }

    /// <summary>Человекочитаемое обоснование светофора (что притянуло уровень вниз) — задача 38
    /// часть A.1. Дисклеймер "не кредитный рейтинг" сюда НЕ входит — он отдельный элемент UI
    /// (тултип фронта, задача 38 часть C), не часть этой строки.</summary>
    public required string ReliabilityReason { get; init; }
}

public sealed record ComparisonResponseDto
{
    public required IReadOnlyList<ComparisonRowDto> Rows { get; init; }
    public required string Disclaimer { get; init; }
}

public sealed record ComparisonRowDto
{
    public required ulong PositionId { get; init; }
    public required ulong InstrumentId { get; init; }
    public string? Name { get; init; }
    public string? Issuer { get; init; }
    public decimal? EffectiveYield { get; init; }
    public required string YieldKind { get; init; }
    public decimal? ModifiedDuration { get; init; }
    public decimal? GSpread { get; init; }
    public required int DaysToHorizon { get; init; }
    public required DateOnly HorizonDate { get; init; }
    public required bool CalculatedToOffer { get; init; }
    public required string CouponType { get; init; }
    public required bool IsEstimated { get; init; }
    public required bool DataIncomplete { get; init; }
}

public sealed record ReplacementRequestDto
{
    public required ulong HoldPositionId { get; init; }

    /// <summary>Target — позиция портфеля (контракт не менялся). Игнорируется, если задан <see cref="TargetInstrumentId"/>.</summary>
    public ulong TargetPositionId { get; init; }

    /// <summary>target — бумага БЕЗ позиции (watchlist), ищется по InstrumentId. Взаимно исключим с TargetPositionId — если задан, он приоритетнее.</summary>
    public ulong? TargetInstrumentId { get; init; }
    public required decimal HorizonYears { get; init; }
    public decimal? SellCommissionRate { get; init; }
    public decimal? BuyCommissionRate { get; init; }
}

public sealed record ReplacementResponseDto
{
    public required ulong HoldPositionId { get; init; }

    /// <summary>0 — target был найден по TargetInstrumentId (watchlist-holding без позиции, см. doc-comment BuildForInstrumentsAsync), не по позиции портфеля.</summary>
    public required ulong TargetPositionId { get; init; }

    /// <summary>Эхо запроса — задан, только если target был watchlist-бумагой без позиции.</summary>
    public ulong? TargetInstrumentId { get; init; }
    public required decimal HorizonYears { get; init; }
    public required decimal SellCommissionRub { get; init; }
    public required decimal BuyCommissionRub { get; init; }
    public required decimal TotalSwitchCostRub { get; init; }
    public required decimal NetBenefitRub { get; init; }
    public required bool IsSwitchFavorable { get; init; }
    public decimal? BreakEvenYears { get; init; }
    public required bool YieldDataIncomplete { get; init; }
    public required string Disclaimer { get; init; }

    /// <summary>Фактически применённая ставка продажи — ДОЛЯ (явная ставка запроса, если была передана, иначе резолвер части C).</summary>
    public required decimal SellCommissionRateUsed { get; init; }

    /// <summary>Фактически применённая ставка покупки — ДОЛЯ.</summary>
    public required decimal BuyCommissionRateUsed { get; init; }

    /// <summary>Источник ставки — строка <see cref="CommissionRateSource"/> либо "ExplicitRequest" (обе ставки заданы явно в запросе — резолвер не вызывался).</summary>
    public required string CommissionRateSource { get; init; }

    /// <summary>Спред эффективных доходностей (targetYield − holdYield) — ДОЛЯ. Null — YieldDataIncomplete.</summary>
    public decimal? SpreadFraction { get; init; }

    /// <summary>Капитал, реально переходящий в target (MarketValueRub hold минус комиссия продажи), в рублях — та же величина, что MatrixPair.CapitalRub.</summary>
    public required decimal CapitalRub { get; init; }

    /// <summary>Валовая выгода (спред × капитал × горизонт) ДО вычета комиссий, в рублях. Null — YieldDataIncomplete.</summary>
    public decimal? GrossGainRub { get; init; }

    /// <summary>NetBenefitRub / CapitalRub / HorizonYears — ДОЛЯ (не процент), та же формула, что MatrixPair.AnnualizedBenefitFraction.</summary>
    public decimal? AnnualizedBenefitFraction { get; init; }

    /// <summary>Оценка НДФЛ от продажи hold-позиции (13% с прибыли к средней цене входа) — null, если cost basis недоступен/журнал неполон.</summary>
    public decimal? SellTaxEstimateRub { get; init; }

    /// <summary>NetBenefitRub − SellTaxEstimateRub — выгода после налога. Null, если SellTaxEstimateRub недоступен.</summary>
    public decimal? NetBenefitAfterTaxRub { get; init; }

    /// <summary>
    /// Информационные риск-сигналы (ликвидность+листинг, спред) для ЦЕЛИ
    /// сравнения — по её банк-записи (<c>bond_universe</c>, найдена по ISIN). Null, если целевая
    /// бумага не найдена в банке (не покрыта биржевой статистикой MOEX) — НЕ ошибка, фронт просто
    /// не показывает сигналы. См. doc-comment <see cref="RiskSignalsDto"/> — НЕ рейтинг агентств.
    /// </summary>
    public RiskSignalsDto? TargetRiskSignals { get; init; }
}

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

    /// <summary>ДОЛЯ (0.0020 = 20 б.п.): порог вердикта корзины = max(20 б.п., 0.25 × IQR корзины),
    /// см. <see cref="SpreadThresholds.ForBasket"/>. Отклонение больше порога → Cheap/Rich.</summary>
    public required decimal ThresholdFraction { get; init; }

    /// <summary>"Cheap" | "Fair" | "Rich" | "InsufficientData" (<see cref="RelativeValueVerdict"/>;
    /// InsufficientData — fallback «весь рынок», мало сопоставимых бумаг, вердикта нет).</summary>
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

    /// <summary>ДОЛЯ; G-спред банка (<c>BondUniverseEntry.EffectiveGspreadFraction</c>): биржевой, а при его отсутствии приближённый по кривой.</summary>
    public decimal? GSpreadFraction { get; init; }

    /// <summary>Дата оферты банк-записи (<see cref="BondUniverseEntry.OfferDate"/>),
    /// если она есть — доходность бумаги с офертой считается движком К ОФЕРТЕ, не к погашению,
    /// горизонт другой. Null — оферты нет/не известна.</summary>
    public DateOnly? OfferDate { get; init; }

    public required RiskSignalsDto RiskSignals { get; init; }
}

/// <summary>
/// Два ИНФОРМАЦИОННЫХ сигнала кандидата (ликвидность+листинг и отклонение спреда от медианы корзины —
/// см. <see cref="Bonds.Core.Analytics.SignalLevel"/>) + скоркарта надёжности v2 (задача 46: score, coverage,
/// компоненты, флаги, светофор) + ось Value отдельным полем. Сигналы НЕ ранжируют кандидатов — ранжирование
/// mode=market идёт по доходности; светофор считает скоркарта, а не эти два сигнала.
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
    /// Светофор надёжности v2 (задача 46): "Green"|"Yellow"|"Red" — результат скоркарты
    /// (<see cref="Bonds.Core.Analytics.ReliabilityScoreService"/>), а НЕ оси спреда: «дешёвая» бумага больше не краснеет.
    /// Контракт значений и фильтра <c>reliability=green|yellow|red</c> прежний. <b>НЕ кредитный рейтинг</b> —
    /// оценка по открытым данным, формулировка «рейтинг» для самого светофора запрещена планом задачи 38.
    /// </summary>
    public required string Reliability { get; init; }

    /// <summary>Человекочитаемое обоснование светофора (что притянуло уровень вниз/вверх). Дисклеймер
    /// «не кредитный рейтинг» сюда НЕ входит — он отдельный элемент UI.</summary>
    public required string ReliabilityReason { get; init; }

    /// <summary>Скор надёжности 0–100 = набрано / доступно × 100 (по компонентам с данными). ПОСЧИТАН НЕ из G-спреда.</summary>
    public required decimal Score { get; init; }

    /// <summary>Покрытие данными 0–1 = доступные баллы / максимум применимых компонентов (устаревшая отчётность
    /// даёт слою фундамента половину веса). Низкое покрытие = скор опирается на мало данных.</summary>
    public required decimal Coverage { get; init; }

    /// <summary>Разбивка скоркарты: каждый компонент со значением, вкладом, источником и признаком «нет данных».</summary>
    public required IReadOnlyList<ScoreComponentDto> Components { get; init; }

    /// <summary>Критические флаги (краснят светофор): IssuerDefaulted, InterestUncovered, NegativeEquity, SpreadShock.</summary>
    public required IReadOnlyList<string> CriticalFlags { get; init; }

    /// <summary>Предупреждения (не краснят): IssuerTechDefault, PriceDrawdown, JunkRating.</summary>
    public required IReadOnlyList<string> WarningFlags { get; init; }

    /// <summary>Ось Value (цена, НЕ риск): "Cheap"|"Fair"|"Rich"|"InsufficientData" — G-спред бумаги против медианы
    /// её корзины (сектор × дюрация Маколея), порог — IQR корзины (<c>RelativeValueService.ResolveVerdict</c>).
    /// Отдельное поле: «дешёвая» бумага не обязана быть рискованной, а «Rich» — надёжной.</summary>
    public required string ValueVerdict { get; init; }

    /// <summary>Задача 47: медиана G-спреда корзины бумаги — ДОЛЯ (0.003 = 30 б.п.), для тултипа Value «спред X против
    /// медианы Y». Null, если у бумаги нет спреда.</summary>
    public decimal? ValueMedianFraction { get; init; }

    /// <summary>Задача 47: порог вердикта Value — ДОЛЯ, max(20 б.п., 0.25 × IQR корзины). Null вместе с медианой.</summary>
    public decimal? ValueThresholdFraction { get; init; }
}

/// <summary>Один компонент скоркарты надёжности (зеркало <see cref="Bonds.Core.Analytics.ScoreComponent"/>).</summary>
public sealed record ScoreComponentDto
{
    /// <summary>Стабильный ключ: issuer_default_history, listing, no_offer, not_qual_only, broker_risk_level,
    /// manual_rating, revenue_size, interest_coverage, leverage, current_ratio, trend, altman_z_em, sovereign,
    /// spread_momentum, price_drawdown, liquidity.</summary>
    public required string Key { get; init; }
    public required string Title { get; init; }

    /// <summary>Числовое значение показателя (единицы по компоненту: ДОЛЯ для spread_momentum/price_drawdown,
    /// РУБЛИ для revenue_size, «раз» для коэффициентов); null, если нет или не число.</summary>
    public decimal? RawValue { get; init; }

    /// <summary>Значение/причина словами («2200/2330 = 2.4×», «нет данных: …», «[Stale: …]»).</summary>
    public string? RawDisplay { get; init; }
    public required int Points { get; init; }
    public required int MaxPoints { get; init; }
    public required string Source { get; init; }

    /// <summary>true — данных нет: компонент не входит в Score (в отличие от честного нуля).</summary>
    public required bool Missing { get; init; }

    /// <summary>"A" структурный | "B" фундаментальный | "C" рыночный.</summary>
    public required string Layer { get; init; }

    /// <summary>true — отчётность устарела (старше 20 месяцев).</summary>
    public required bool Stale { get; init; }
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

    /// <summary>Задача 44: дюрация Маколея, ГОДЫ — единая мера для сравнения с корзинами банка (банк
    /// хранит MOEX DURATION = Маколей; ModifiedDuration остаётся мерой чувствительности).</summary>
    public decimal? MacaulayDurationYears { get; init; }
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

    /// <summary>Горизонт, ГОДЫ. Null — взять из настроек пользователя (<c>user_settings.replacement_horizon_years</c>, дефолт 2.0), задача 48.</summary>
    public decimal? HorizonYears { get; init; }
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
    /// <summary>Комиссии обеих сделок (продажа + покупка), рубли; слиппедж и налог — отдельные поля.</summary>
    public required decimal TotalSwitchCostRub { get; init; }
    /// <summary>FV_switch − FV_hold БЕЗ налога продажи (но после НДФЛ на купоны, комиссий и слиппеджа), рубли.</summary>
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

    /// <summary>Капитал, реально вложенный в цель: выручка hold минус комиссия/слиппедж покупки (и минус комиссия, слиппедж, налог продажи — до этого), рубли.</summary>
    public required decimal CapitalRub { get; init; }

    /// <summary>Валовая выгода: MV × ((1+y_target_at)^h − (1+y_hold_at)^h) — разница доходностей после НДФЛ на полной стоимости hold ДО издержек, рубли. Null — YieldDataIncomplete.</summary>
    public decimal? GrossGainRub { get; init; }

    /// <summary>(FV_switch / FV_hold)^(1/EffectiveHorizonYears) − 1 — годовая выгода ДОЛЕЙ (не процент). Null — YieldDataIncomplete.</summary>
    public decimal? AnnualizedBenefitFraction { get; init; }

    /// <summary>Оценка НДФЛ от продажи hold-позиции (13% с прибыли к средней цене входа) — null, если cost basis недоступен/журнал неполон.</summary>
    public decimal? SellTaxEstimateRub { get; init; }

    /// <summary>FV_switch − FV_hold С налогом продажи (он уже вычтен из выручки). Null, если SellTaxEstimateRub недоступен.</summary>
    public decimal? NetBenefitAfterTaxRub { get; init; }

    /// <summary>
    /// Информационные риск-сигналы (ликвидность+листинг, спред) для ЦЕЛИ
    /// сравнения — по её банк-записи (<c>bond_universe</c>, найдена по ISIN). Null, если целевая
    /// бумага не найдена в банке (не покрыта биржевой статистикой MOEX) — НЕ ошибка, фронт просто
    /// не показывает сигналы. См. doc-comment <see cref="RiskSignalsDto"/> — НЕ рейтинг агентств.
    /// </summary>
    public RiskSignalsDto? TargetRiskSignals { get; init; }

    // ─── Задача 48: математика замены v2 (additive; поля выше сохраняют имена, семантика обновлена — см. doc-comment полей) ───

    /// <summary>Откуда взят <see cref="HorizonYears"/>: "Request" (явно в запросе) | "Settings" (настройка пользователя).</summary>
    public required string HorizonSource { get; init; }

    /// <summary>Горизонт, на котором сравнены ноги: min(HorizonYears, срок до оферты/погашения цели), не меньше 0.25, ГОДЫ.</summary>
    public required decimal EffectiveHorizonYears { get; init; }

    /// <summary>true — горизонт урезан сроком цели; тогда заданы <see cref="TargetHorizonDate"/>/<see cref="TargetHorizonIsOffer"/>.</summary>
    public required bool HorizonLimitedByTarget { get; init; }
    public DateOnly? TargetHorizonDate { get; init; }

    /// <summary>true — <see cref="TargetHorizonDate"/> это оферта, false — погашение.</summary>
    public bool TargetHorizonIsOffer { get; init; }

    /// <summary>Ставка НДФЛ на купоны/дисконт, применённая к доходностям обеих ног — ДОЛЯ (0.13). Упрощение: ко всей доходности, без сальдирования/ЛДВ.</summary>
    public required decimal CouponTaxRate { get; init; }

    /// <summary>Доходность hold после НДФЛ — ДОЛЯ (y × (1 − 0.13)).</summary>
    public required decimal HoldYieldAfterTaxFraction { get; init; }

    /// <summary>Доходность цели после НДФЛ — ДОЛЯ.</summary>
    public required decimal TargetYieldAfterTaxFraction { get; init; }

    /// <summary>Слиппедж продажи hold (MV × полуспред стакана), рубли; 0 при <see cref="SlippageUnknown"/>.</summary>
    public required decimal SellSlippageRub { get; init; }

    /// <summary>Слиппедж покупки цели (выручка × полуспред стакана), рубли.</summary>
    public required decimal BuySlippageRub { get; init; }

    /// <summary>true — у одной из ног стакан (bid/offer) неизвестен: слиппедж этой ноги НЕ учтён.</summary>
    public required bool SlippageUnknown { get; init; }

    /// <summary>Выручка продажи hold после комиссии, слиппеджа и налога продажи, рубли.</summary>
    public required decimal ProceedsRub { get; init; }

    /// <summary>Будущая стоимость hold на эффективном горизонте (MV × (1+y_at)^h), рубли.</summary>
    public required decimal HoldFutureValueRub { get; init; }

    /// <summary>Будущая стоимость варианта «переложиться» (вложено × (1+y_at)^h), рубли.</summary>
    public required decimal SwitchFutureValueRub { get; init; }

    /// <summary>Дюрации Маколея, ГОДЫ (null — не посчитаны).</summary>
    public decimal? HoldMacaulayDurationYears { get; init; }
    public decimal? TargetMacaulayDurationYears { get; init; }

    /// <summary>|Маколей цели − Маколей hold| &gt; 1.5 года.</summary>
    public required bool DurationMismatch { get; init; }

    /// <summary>Текст предупреждения «Процентный риск изменится: дюрация X → Y лет» при <see cref="DurationMismatch"/>.</summary>
    public string? DurationMismatchWarning { get; init; }
}

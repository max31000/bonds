namespace Bonds.Core.Analytics;

/// <summary>
/// Пороги и баллы скоркарты надёжности (<see cref="ReliabilityScoreService"/>, задача 46). Все значения —
/// <b>эвристика по литературе/статистике РФ, калибровка — BACKLOG О2</b> (исследование 962 эмитентов:
/// дефолт-история, уровень листинга, размер выручки, оферта; Giesecke et al., NBER w15848 — уровень
/// спреда слабый предиктор дефолта, поэтому спред в риск-скоринг не входит, только его динамика).
/// <para>
/// <b>Единицы.</b> Доли — ДОЛИ (0.0020 = 20 б.п. G-спреда; 0.02 = 2% просадки цены); деньги — РУБЛИ
/// (контракт issuer_financials: ГИР БО отдаёт тысячи, ×1000 на границе парсинга); коэффициенты — безразмерные.
/// </para>
/// </summary>
public static class ReliabilityScoreOptions
{
    // ─── Слой A — структурный ─────────────────────────────────────────────────────────────

    /// <summary>issuer_default_history: макс. баллов (нет дефолтов ни по одному выпуску эмитента).</summary>
    public const int DefaultHistoryMaxPoints = 12;

    /// <summary>issuer_default_history: баллов при техническом дефолте без дефолта (флаг IssuerTechDefault).</summary>
    public const int DefaultHistoryTechDefaultPoints = 4;

    /// <summary>listing: макс. баллов (уровень 1).</summary>
    public const int ListingMaxPoints = 15;

    /// <summary>listing: баллов за уровень 2.</summary>
    public const int ListingLevel2Points = 8;

    /// <summary>no_offer: макс. баллов (нет неисполненной оферты; дефолтность с офертой ×4.5 по статистике РФ).</summary>
    public const int NoOfferMaxPoints = 5;

    /// <summary>not_qual_only: макс. баллов (бумага доступна неквалифицированному инвестору).</summary>
    public const int NotQualOnlyMaxPoints = 3;

    /// <summary>broker_risk_level: макс. баллов (T-Invest risk_level = Low).</summary>
    public const int BrokerRiskMaxPoints = 10;

    /// <summary>broker_risk_level: баллов за Moderate.</summary>
    public const int BrokerRiskModeratePoints = 5;

    /// <summary>manual_rating: макс. баллов (ручной рейтинг агентства AAA/AA).</summary>
    public const int ManualRatingMaxPoints = 10;

    /// <summary>manual_rating: срок годности рейтинга, МЕСЯЦЫ; старше — компонент Missing («протух»).</summary>
    public const int ManualRatingFreshMonths = 18;

    // ─── Слой B — фундаментальный ─────────────────────────────────────────────────────────

    /// <summary>revenue_size: выручка от которой 8 баллов, РУБЛИ (4 млрд ₽ — сильнейший предиктор по РФ-статистике).</summary>
    public const decimal RevenueLargeRub = 4_000_000_000m;

    /// <summary>revenue_size: выручка от которой 4 балла, РУБЛИ (1 млрд ₽).</summary>
    public const decimal RevenueMediumRub = 1_000_000_000m;

    public const int RevenueMaxPoints = 8;
    public const int RevenueMediumPoints = 4;

    /// <summary>interest_coverage: 2200/2330 — порог 8 баллов (раз).</summary>
    public const decimal InterestCoverageStrong = 3m;

    /// <summary>interest_coverage: порог 5 баллов (раз).</summary>
    public const decimal InterestCoverageGood = 2m;

    /// <summary>interest_coverage: порог 2 баллов (раз); ниже — 0 баллов и критический флаг InterestUncovered.</summary>
    public const decimal InterestCoverageMin = 1m;

    public const int InterestCoverageMaxPoints = 8;

    /// <summary>leverage: (1410+1510−1250)/1300 — порог 8 баллов (строго ниже, раз).</summary>
    public const decimal LeverageLow = 1m;

    /// <summary>leverage: порог 5 баллов (строго ниже, раз).</summary>
    public const decimal LeverageMedium = 2m;

    /// <summary>leverage: порог 2 баллов (строго ниже, раз); от него и выше — 0 баллов.</summary>
    public const decimal LeverageHigh = 4m;

    public const int LeverageMaxPoints = 8;

    /// <summary>current_ratio: 1200/1500 — порог 4 баллов.</summary>
    public const decimal CurrentRatioStrong = 1.5m;

    /// <summary>current_ratio: порог 2 баллов; ниже — 0.</summary>
    public const decimal CurrentRatioMin = 1m;

    public const int CurrentRatioMaxPoints = 4;

    public const int TrendMaxPoints = 6;
    public const int TrendMixedPoints = 3;

    /// <summary>altman_z_em: Z″-EM ≥ этого — 6 баллов. 5.85 = 2.6 (порог «безопасной» зоны Z″) + 3.25 (константа
    /// EM-версии); совпадает с нижней границей рейтинговой категории BBB в шкале Altman&amp;Hotchkiss 2005.</summary>
    public const decimal AltmanSafeZone = 5.85m;

    /// <summary>altman_z_em: Z″-EM ≥ этого — 3 балла (серая зона), ниже — 0. 4.35 = 1.1 (порог «дистресса» Z″) + 3.25.</summary>
    public const decimal AltmanDistressZone = 4.35m;

    public const int AltmanMaxPoints = 6;
    public const int AltmanGreyPoints = 3;

    /// <summary>Свежесть отчётности: если конец последнего отчётного года старше стольких МЕСЯЦЕВ —
    /// отчётность Stale (нет подачи за прошлый год).</summary>
    public const int FinancialsStaleMonths = 20;

    /// <summary>Stale: во сколько раз уменьшается вклад слоя B в Coverage.</summary>
    public const decimal StaleCoverageMultiplier = 0.5m;

    /// <summary>sovereign (вместо слоя B): баллы для «Гособлигации».</summary>
    public const int SovereignGovernmentPoints = 15;

    /// <summary>sovereign (вместо слоя B): баллы для «Муниципальные».</summary>
    public const int SovereignMunicipalPoints = 10;

    // ─── Слой C — рыночный ────────────────────────────────────────────────────────────────

    /// <summary>Минимум точек истории (торговых дней) для динамических компонентов; меньше — Missing.</summary>
    public const int MinHistoryDays = 10;

    /// <summary>spread_momentum: расширение G-спреда к своей 20-дневной медиане ≤ этого — 8 баллов (ДОЛЯ, 20 б.п.).</summary>
    public const decimal SpreadMomentumCalmFraction = 0.0020m;

    /// <summary>spread_momentum: ≤ этого — 3 балла (ДОЛЯ, 50 б.п.); выше — 0 + критический флаг SpreadShock.</summary>
    public const decimal SpreadMomentumShockFraction = 0.0050m;

    public const int SpreadMomentumMaxPoints = 8;
    public const int SpreadMomentumWarnPoints = 3;

    /// <summary>price_drawdown: просадка цены от 20-дневного максимума строго ниже этого — 6 баллов (ДОЛЯ, 2%).</summary>
    public const decimal DrawdownCalmFraction = 0.02m;

    /// <summary>price_drawdown: ≤ этого — 3 балла (ДОЛЯ, 5%); выше — 0 + флаг PriceDrawdown (не критический).</summary>
    public const decimal DrawdownWarnFraction = 0.05m;

    public const int DrawdownMaxPoints = 6;
    public const int DrawdownWarnPoints = 3;

    public const int LiquidityMaxPoints = 6;
    public const int LiquidityNeutralPoints = 3;

    // ─── Светофор ─────────────────────────────────────────────────────────────────────────

    /// <summary>Red: Score ниже этого (по доступным компонентам).</summary>
    public const decimal RedScoreBelow = 35m;

    /// <summary>Green: Score не ниже этого.</summary>
    public const decimal GreenScoreAtLeast = 65m;

    /// <summary>Green: Coverage не ниже этого (доля максимума, закрытая данными).</summary>
    public const decimal GreenCoverageAtLeast = 0.6m;

    /// <summary>Red: «мёртвая» ликвидность — ось ликвидности Caution И медианный оборот ниже этого, РУБЛИ за день.</summary>
    public const decimal DeadLiquidityTurnoverRub = 300_000m;
}

/// <summary>Имена флагов скоркарты (<see cref="ReliabilityScore.CriticalFlags"/>/<see cref="ReliabilityScore.WarningFlags"/>).</summary>
public static class ReliabilityFlags
{
    public const string IssuerDefaulted = "IssuerDefaulted";
    public const string InterestUncovered = "InterestUncovered";
    public const string NegativeEquity = "NegativeEquity";
    public const string SpreadShock = "SpreadShock";

    public const string IssuerTechDefault = "IssuerTechDefault";
    public const string PriceDrawdown = "PriceDrawdown";
    public const string JunkRating = "JunkRating";
}

using System.Globalization;
using Bonds.Core.Models;

namespace Bonds.Core.Analytics;

/// <summary>Вердикт позиции (задача 49). Формулировки — «кандидат/рассмотреть», не «купите/продайте».</summary>
public enum PositionVerdictKind
{
    /// <summary>В рамках корзины, выгодных замен после издержек нет.</summary>
    Hold,

    /// <summary>Дешёвая к корзине бумага здорового эмитента; докупка укладывается в лимит концентрации.</summary>
    BuyMoreCandidate,

    /// <summary>Есть замена с выгодой выше порога после налогов и издержек при светофоре цели не хуже hold.</summary>
    ConsiderReplace,

    /// <summary>Критический флаг скоркарты / красный светофор / дефолт эмитента: рассмотреть выход.</summary>
    ConsiderExit,
}

/// <summary>Уверенность вердикта — по <see cref="ReliabilityScore.Coverage"/> (сколько данных реально есть).</summary>
public enum VerdictConfidence
{
    Low,
    Medium,
    High,
}

/// <summary>
/// Результат анализа замены в форме, нужной вердикту (из <see cref="SwitchAnalysisResult"/> + светофор цели).
/// Единицы: <see cref="AnnualizedBenefitFraction"/> — ДОЛЯ годовых после НДФЛ, комиссий, слиппеджа и налога продажи
/// (0.005 = 50 б.п.); <see cref="NetBenefitAfterTaxRub"/> — рубли за эффективный горизонт; горизонт — годы.
/// </summary>
public sealed record ReplacementOption
{
    public required string TargetName { get; init; }

    /// <summary>SECID цели в банке облигаций (для ссылки/отладки). Null — неизвестен.</summary>
    public string? TargetSecid { get; init; }

    /// <summary>Светофор надёжности цели (скоркарта банка).</summary>
    public required ReliabilityLight TargetLight { get; init; }

    /// <summary>Годовая чистая выгода, ДОЛЯ. Null — доходность одной из ног неизвестна (замена несравнима).</summary>
    public decimal? AnnualizedBenefitFraction { get; init; }

    /// <summary>Чистая выгода в рублях с учётом налога продажи. Null — налог продажи оценить нельзя (журнал неполон): в расчёт он не вошёл.</summary>
    public decimal? NetBenefitAfterTaxRub { get; init; }

    /// <summary>Горизонт, на котором сравнивались ноги, годы.</summary>
    public decimal EffectiveHorizonYears { get; init; }
}

/// <summary>Вход вердикта по одной позиции. Все данные уже собраны вызывающим слоем (батчево), сервис чистый.</summary>
public sealed record PositionVerdictInput
{
    /// <summary>Скоркарта позиции (для бумаги вне банка — <see cref="ReliabilityScoreService.Unknown()"/>, Coverage 0).</summary>
    public required ReliabilityScore Reliability { get; init; }

    /// <summary>Ось Value (RV-вердикт против корзины). <c>InsufficientData</c> — вердикта нет.</summary>
    public RelativeValueVerdict Value { get; init; } = RelativeValueVerdict.InsufficientData;

    /// <summary>
    /// <see cref="YieldComparability.IsComparable"/> позиции: false для флоатера/индексируемой/неполных данных — замена
    /// «по выгоде» не предлагается (несравнимые доходности), остальные правила работают.
    /// </summary>
    public bool YieldComparable { get; init; } = true;

    /// <summary>Типы АКТИВНЫХ событий по этой позиции (из таблицы событий).</summary>
    public IReadOnlyList<SignalType> ActiveEventTypes { get; init; } = [];

    /// <summary>Лучшая замена (см. <see cref="PositionVerdictService.SelectBestReplacement"/>); null — кандидатов нет/посчитать нельзя.</summary>
    public ReplacementOption? BestReplacement { get; init; }

    /// <summary>
    /// Доля эмитента позиции в портфеле ПОСЛЕ докупки, ПРОЦЕНТЫ 0–100 (см. <see cref="PositionVerdictService.ConcentrationAfterBuyPercent"/>).
    /// Null — оценить нельзя (портфель без стоимости): <see cref="PositionVerdictKind.BuyMoreCandidate"/> не выдаётся.
    /// </summary>
    public decimal? IssuerSharePercentAfterBuy { get; init; }

    /// <summary>Лимит концентрации по эмитенту, ПРОЦЕНТЫ 0–100 (индивидуальный из целей либо <c>DefaultMaxConcentrationPercent</c>).</summary>
    public decimal ConcentrationLimitPercent { get; init; } = 25m;
}

/// <param name="Verdict">Итог.</param>
/// <param name="Reasons">До <see cref="PositionVerdictService.MaxReasons"/> человекочитаемых причин, главная — первой.</param>
/// <param name="Confidence">По Coverage скоркарты.</param>
public sealed record PositionVerdict(PositionVerdictKind Verdict, IReadOnlyList<string> Reasons, VerdictConfidence Confidence);

/// <summary>
/// Вердикт позиции (задача 49): «держать / кандидат докупить / рассмотреть замену / рассмотреть выход» — сводит
/// скоркарту (46), ось Value (44), события (40/49), лучшую замену (48) и лимит концентрации. Чистая функция, без I/O.
/// <para>
/// Правила в порядке приоритета (срабатывает первое):
/// <list type="number">
/// <item>критический флаг скоркарты (<c>IssuerDefaulted</c>, <c>InterestUncovered</c>, <c>NegativeEquity</c>,
/// <c>SpreadShock</c>), Red-светофор или активное событие дефолта эмитента → <see cref="PositionVerdictKind.ConsiderExit"/>;</item>
/// <item>Coverage скоркарты = 0 («мало данных») → Hold с Confidence Low;</item>
/// <item>YieldComparable, замена с годовой выгодой ≥ <see cref="MinReplacementBenefitFraction"/> и светофором цели не хуже
/// hold → <see cref="PositionVerdictKind.ConsiderReplace"/>;</item>
/// <item>Green + Value = Cheap + концентрация после докупки ≤ лимита + нет активных рыночных предупреждений →
/// <see cref="PositionVerdictKind.BuyMoreCandidate"/>;</item>
/// <item>иначе Hold.</item>
/// </list>
/// Это оценка по открытым данным, не инвестиционная рекомендация.
/// </para>
/// </summary>
public static class PositionVerdictService
{
    public const string Disclaimer =
        "Вердикт — оценка по открытым данным (скоркарта надёжности, относительная цена в корзине, расчёт замены с налогами и " +
        "издержками), а не рекомендация. Не учитывает вашу налоговую ситуацию, цели и горизонт; решение принимаете вы.";

    /// <summary>Порог годовой чистой выгоды замены для <see cref="PositionVerdictKind.ConsiderReplace"/>: 0.005 = 50 б.п. годовых ПОСЛЕ налогов и издержек (ДОЛЯ).</summary>
    public const decimal MinReplacementBenefitFraction = 0.005m;

    /// <summary>
    /// Условная докупка для проверки лимита концентрации: 1% стоимости портфеля (ДОЛЯ). Это «проба», а не размер сделки:
    /// вердикт отвечает, остаётся ли у эмитента запас до лимита при небольшой докупке.
    /// </summary>
    public const decimal BuyMoreProbePortfolioFraction = 0.01m;

    /// <summary>Максимум причин в вердикте.</summary>
    public const int MaxReasons = 3;

    /// <summary>Coverage скоркарты не ниже этого → Confidence High (= порог Green скоркарты).</summary>
    public const decimal HighConfidenceCoverage = ReliabilityScoreOptions.GreenCoverageAtLeast;

    /// <summary>Coverage не ниже этого → Medium (= порог, с которого скоркарта вообще может краснить по баллу).</summary>
    public const decimal MediumConfidenceCoverage = ReliabilityScoreOptions.RedScoreMinCoverage;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static PositionVerdict Evaluate(PositionVerdictInput input)
    {
        var score = input.Reliability;
        var confidence = ConfidenceFor(score.Coverage);

        // 1. Выход: критические флаги / Red / дефолт эмитента.
        var hasDefaultEvent = input.ActiveEventTypes.Contains(SignalType.IssuerDefaultEvent);
        if (score.CriticalFlags.Count > 0 || score.Level == ReliabilityLight.Red || hasDefaultEvent)
        {
            return new PositionVerdict(PositionVerdictKind.ConsiderExit, ExitReasons(input, hasDefaultEvent), confidence);
        }

        // 2. Данных нет вовсе — вердикт не выдумываем.
        if (score.Coverage <= 0m)
        {
            return new PositionVerdict(PositionVerdictKind.Hold, ["Оценка невозможна — мало данных: бумаги нет в банке облигаций или по ней нет показателей"], VerdictConfidence.Low);
        }

        // 3. Замена по выгоде.
        if (input.YieldComparable && input.BestReplacement is { AnnualizedBenefitFraction: { } benefit } replacement
            && benefit >= MinReplacementBenefitFraction
            && !IsWorse(replacement.TargetLight, score.Level))
        {
            return new PositionVerdict(PositionVerdictKind.ConsiderReplace, ReplaceReasons(input, replacement, benefit), confidence);
        }

        // 4. Докупка: Green + Cheap + лимит + нет свежих рыночных предупреждений.
        var marketWarnings = MarketWarnings(input);
        var concentrationFits = input.IssuerSharePercentAfterBuy is { } after && after <= input.ConcentrationLimitPercent;
        if (score.Level == ReliabilityLight.Green && input.Value == RelativeValueVerdict.Cheap
            && concentrationFits && marketWarnings.Count == 0)
        {
            return new PositionVerdict(PositionVerdictKind.BuyMoreCandidate, BuyMoreReasons(input), confidence);
        }

        return new PositionVerdict(PositionVerdictKind.Hold, HoldReasons(input, marketWarnings), confidence);
    }

    /// <summary>Уверенность по Coverage скоркарты: &lt; 0.3 Low, &lt; 0.6 Medium, иначе High.</summary>
    public static VerdictConfidence ConfidenceFor(decimal coverage) =>
        coverage >= HighConfidenceCoverage ? VerdictConfidence.High
        : coverage >= MediumConfidenceCoverage ? VerdictConfidence.Medium
        : VerdictConfidence.Low;

    /// <summary>
    /// Доля эмитента в портфеле после условной докупки на <see cref="BuyMoreProbePortfolioFraction"/> стоимости портфеля,
    /// ПРОЦЕНТЫ 0–100 (единица <c>PortfolioCompositionService.SharePercent</c>, с которой сравнивается лимит). Null — портфель без стоимости.
    /// </summary>
    public static decimal? ConcentrationAfterBuyPercent(decimal issuerValueRub, decimal portfolioValueRub)
    {
        if (portfolioValueRub <= 0m) return null;

        var probe = portfolioValueRub * BuyMoreProbePortfolioFraction;
        return (issuerValueRub + probe) / (portfolioValueRub + probe) * 100m;
    }

    /// <summary>
    /// Лучшая замена из прогнанных кандидатов: только с известной годовой выгодой и светофором цели НЕ ХУЖЕ hold
    /// (Red цели не предлагается никогда — он хуже любого hold, кроме Red, а Red-hold уходит в выход раньше),
    /// максимум по <see cref="ReplacementOption.AnnualizedBenefitFraction"/>.
    /// </summary>
    public static ReplacementOption? SelectBestReplacement(ReliabilityLight holdLight, IEnumerable<ReplacementOption> options) =>
        options
            .Where(o => o.AnnualizedBenefitFraction is not null)
            .Where(o => o.TargetLight != ReliabilityLight.Red && !IsWorse(o.TargetLight, holdLight))
            .OrderByDescending(o => o.AnnualizedBenefitFraction)
            .FirstOrDefault();

    /// <summary>Светофор <paramref name="a"/> строго хуже <paramref name="b"/> (порядок enum: Green &lt; Yellow &lt; Red).</summary>
    private static bool IsWorse(ReliabilityLight a, ReliabilityLight b) => a > b;

    // ─── Причины ──────────────────────────────────────────────────────────────────────────

    private static IReadOnlyList<string> ExitReasons(PositionVerdictInput input, bool hasDefaultEvent)
    {
        var reasons = new List<string>();
        if (hasDefaultEvent)
        {
            reasons.Add("У эмитента отмечен дефолт или технический дефолт по облигациям (карточки бумаг MOEX)");
        }

        foreach (var flag in input.Reliability.CriticalFlags)
        {
            var text = flag switch
            {
                ReliabilityFlags.IssuerDefaulted => "У эмитента есть история дефолта по выпускам",
                ReliabilityFlags.InterestUncovered => "Операционная прибыль эмитента не покрывает проценты к уплате",
                ReliabilityFlags.NegativeEquity => "У эмитента отрицательный капитал",
                ReliabilityFlags.SpreadShock => "G-спред бумаги резко расширился к собственной 20-дневной медиане",
                _ => $"Критический признак надёжности: {flag}",
            };
            // IssuerDefaulted дублирует событие дефолта — не засоряем топ-3 двумя одинаковыми по смыслу причинами.
            if (hasDefaultEvent && flag == ReliabilityFlags.IssuerDefaulted) continue;
            reasons.Add(text);
        }

        if (input.Reliability.Level == ReliabilityLight.Red && reasons.Count == 0)
        {
            reasons.Add(input.Reliability.Reason);
        }

        return Cap(reasons);
    }

    private static IReadOnlyList<string> ReplaceReasons(PositionVerdictInput input, ReplacementOption replacement, decimal benefit)
    {
        // Единицы: benefit — ДОЛЯ годовых; в тексте проценты = доля × 100 (одна конверсия, для человека).
        var reasons = new List<string>
        {
            $"Кандидат на замену «{replacement.TargetName}»: оценка выгоды +{(benefit * 100m).ToString("0.0#", Inv)}% годовых " +
            $"после налогов и издержек (порог {(MinReplacementBenefitFraction * 100m).ToString("0.0#", Inv)}%)",
            replacement.TargetLight == input.Reliability.Level
                ? "Светофор надёжности цели не хуже текущей бумаги"
                : "Светофор надёжности цели лучше, чем у текущей бумаги",
        };

        if (replacement.NetBenefitAfterTaxRub is { } rub)
        {
            reasons.Add($"≈ {rub.ToString("N0", Inv).Replace(',', ' ')} ₽ чистой выгоды за {replacement.EffectiveHorizonYears.ToString("0.#", Inv)} г.");
        }
        else
        {
            reasons.Add("Выгода посчитана без налога: налог продажи не оценён (журнал операций неполон), реальная выгода может быть ниже");
        }

        return Cap(reasons);
    }

    private static IReadOnlyList<string> BuyMoreReasons(PositionVerdictInput input) =>
        Cap(
        [
            "Бумага дёшево к корзине (спред шире медианы сопоставимых бумаг) при здоровом эмитенте",
            $"Доля эмитента после небольшой докупки ≈ {input.IssuerSharePercentAfterBuy!.Value.ToString("0.#", Inv)}% — впишется в лимит {input.ConcentrationLimitPercent.ToString("0.#", Inv)}%",
            input.Reliability.Reason,
        ]);

    private static IReadOnlyList<string> HoldReasons(PositionVerdictInput input, List<string> marketWarnings)
    {
        var reasons = new List<string>();
        reasons.AddRange(marketWarnings);

        // Дёшево и здорово, но докупка упёрлась в лимит — это важно объяснить, иначе вердикт выглядит как «почему не докупать».
        if (input.Reliability.Level == ReliabilityLight.Green && input.Value == RelativeValueVerdict.Cheap
            && marketWarnings.Count == 0)
        {
            reasons.Add(input.IssuerSharePercentAfterBuy is { } after
                ? $"Бумага дёшево к корзине, но доля эмитента после докупки ≈ {after.ToString("0.#", Inv)}% превысила бы лимит {input.ConcentrationLimitPercent.ToString("0.#", Inv)}%"
                : "Бумага дёшево к корзине, но запас до лимита концентрации оценить нельзя");
        }

        if (input.Value == RelativeValueVerdict.Rich)
        {
            reasons.Add("Бумага дороже сопоставимых в корзине (спред уже медианы)");
        }

        if (!input.YieldComparable)
        {
            reasons.Add("Доходность несравнима с фиксированным купоном (флоатер/индексируемая/неполные данные) — замена по выгоде не оценивается");
        }

        reasons.Add("В рамках корзины, выгодных замен после издержек нет");
        return Cap(reasons);
    }

    /// <summary>Активные рыночные предупреждения по позиции — тексты причин (расширение спреда / просадка цены / тех. дефолт).</summary>
    private static List<string> MarketWarnings(PositionVerdictInput input)
    {
        var warnings = new List<string>();
        if (input.ActiveEventTypes.Contains(SignalType.SpreadWidening))
        {
            warnings.Add("G-спред бумаги расширился к своей 20-дневной медиане — дешевизна может быть ухудшением, а не скидкой");
        }

        if (input.ActiveEventTypes.Contains(SignalType.PriceDrawdown))
        {
            warnings.Add("Цена бумаги заметно ниже максимума за 20 торговых дней");
        }

        if (input.Reliability.WarningFlags.Contains(ReliabilityFlags.IssuerTechDefault))
        {
            warnings.Add("У эмитента отмечен технический дефолт");
        }

        return warnings;
    }

    private static IReadOnlyList<string> Cap(IEnumerable<string> reasons) =>
        reasons.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct().Take(MaxReasons).ToList();
}

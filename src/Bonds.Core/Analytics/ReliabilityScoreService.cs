using System.Globalization;
using Bonds.Core.Models;
using Bonds.Core.Universe;
using Opt = Bonds.Core.Analytics.ReliabilityScoreOptions;

namespace Bonds.Core.Analytics;

/// <summary>Светофор надёжности: Green / Yellow / Red. Значения сериализуются именем (контракт фильтра
/// <c>reliability=green|yellow|red</c> и фронтового <c>ReliabilityLevel</c> не менялся задачей 46).</summary>
public enum ReliabilityLight
{
    Green,
    Yellow,
    Red,
}

/// <summary>Рейтинг эмитента для скоркарты (ручной ввод владельца; <see cref="IssuerRating"/> без ИНН).</summary>
public sealed record IssuerRatingInput(string Agency, string Rating, DateOnly RatingDate);

/// <summary>
/// Вход скоркарты: ВСЕ данные nullable — «нет данных» превращается в явный Missing-компонент, а не в ноль.
/// Единицы: доли — ДОЛИ (G-спред 0.0035 = 35 б.п.), цена — % от номинала, деньги отчётности — РУБЛИ,
/// оборот — РУБЛИ за день (контракт docs/CODEBASE-GUIDE.md).
/// </summary>
public sealed record ReliabilityScoreInput
{
    /// <summary>«Сегодня» (МСК) — для свежести рейтингов, отчётности и неисполненной оферты.</summary>
    public required DateOnly Today { get; init; }

    /// <summary>Сектор банка (<c>BondUniverseEntry.Sector</c>): «Гособлигации»/«Муниципальные» → бонус sovereign вместо слоя B.</summary>
    public string? Sector { get; init; }

    // ─── Слой A ───
    /// <summary>Агрегат MAX(has_default) по выпускам эмитента (null — неизвестно, НЕ «чисто»).</summary>
    public bool? IssuerHasDefault { get; init; }

    /// <summary>Агрегат MAX(has_technical_default) (null — неизвестно).</summary>
    public bool? IssuerHasTechnicalDefault { get; init; }

    /// <summary>Откуда флаги: true — агрегат по ВСЕМ выпускам эмитента (EMITTER_ID), false — только карточка самого
    /// выпуска (эмитент неизвестен). Влияет на подпись.</summary>
    public bool DefaultHistoryIsIssuerWide { get; init; }

    public int? ListLevel { get; init; }

    /// <summary>Дата ближайшей оферты банк-записи; не в прошлом → оферта неисполнена, 0 баллов no_offer.</summary>
    public DateOnly? OfferDate { get; init; }

    public bool? IsQualifiedInvestors { get; init; }

    public BrokerRiskLevel? BrokerRiskLevel { get; init; }

    /// <summary>Ручные рейтинги эмитента (любое число агентств); null/пусто — компонент manual_rating отсутствует.</summary>
    public IReadOnlyList<IssuerRatingInput>? Ratings { get; init; }

    // ─── Слой B ───
    /// <summary>Годовая отчётность, новейший год первым (ручная уже приоритетнее ГИР БО — решено репозиторием).</summary>
    public IReadOnlyList<IssuerFinancials>? Financials { get; init; }

    /// <summary>Статус проверки ГИР БО (<see cref="IssuerFinancialsStatuses"/>); null — записи нет. Любой статус
    /// ≠ ok обнуляет автоматическую отчётность (остаётся только ручная).</summary>
    public string? FinancialsStatus { get; init; }

    // ─── Слой C ───
    /// <summary>Эффективный G-спред сегодня, ДОЛЯ.</summary>
    public decimal? GSpreadFraction { get; init; }

    /// <summary>G-спред бумаги за предыдущие торговые дни (до 20, без сегодняшнего), ДОЛИ.</summary>
    public IReadOnlyList<decimal>? GSpreadHistory { get; init; }

    /// <summary>Цена сегодня, % от номинала.</summary>
    public decimal? PricePercent { get; init; }

    /// <summary>Цены за предыдущие торговые дни (до 20), % от номинала.</summary>
    public IReadOnlyList<decimal>? PriceHistory { get; init; }

    /// <summary>Ось ликвидности (задача 44, <see cref="CandidateRiskSignals.Liquidity"/>).</summary>
    public SignalLevel? Liquidity { get; init; }

    /// <summary>Сырой скор ликвидности: <see cref="LiquidityScore.None"/> = данных нет → компонент Missing.</summary>
    public LiquidityScore? LiquidityScoreRaw { get; init; }

    /// <summary>Медиана оборота за 5 торговых дней (или сегодняшний), РУБЛИ за день.</summary>
    public decimal? TurnoverMedianRub { get; init; }
}

/// <summary>
/// Один компонент скоркарты.
/// </summary>
/// <param name="Key">Стабильный ключ (issuer_default_history, listing, …).</param>
/// <param name="Title">Подпись для UI.</param>
/// <param name="RawValue">Числовое значение показателя (единицы — см. <paramref name="RawDisplay"/>); null, если нет/не число.</param>
/// <param name="RawDisplay">Значение словами («покрытие 2.4×», «2 из 3 выпусков…», причина Missing).</param>
/// <param name="Points">Набранные баллы (0 у Missing).</param>
/// <param name="MaxPoints">Максимум баллов компонента.</param>
/// <param name="Source">Источник данных словами.</param>
/// <param name="Missing">true — данных нет: компонент НЕ входит ни в числитель, ни в знаменатель Score.</param>
/// <param name="Layer">"A" структурный, "B" фундаментальный, "C" рыночный.</param>
/// <param name="Stale">true — отчётность слоя B устарела (Coverage слоя ×0.5).</param>
public sealed record ScoreComponent(
    string Key,
    string Title,
    decimal? RawValue,
    string? RawDisplay,
    int Points,
    int MaxPoints,
    string Source,
    bool Missing,
    string Layer,
    bool Stale = false);

/// <summary>
/// Результат скоркарты. <see cref="Score"/> 0–100 = набрано / доступно × 100 (по компонентам с данными);
/// <see cref="Coverage"/> 0–1 = доступно / максимум всех применимых компонентов (отчётность Stale вносит
/// <see cref="ReliabilityScoreOptions.StaleCoverageMultiplier"/> своего веса).
/// </summary>
public sealed record ReliabilityScore(
    decimal Score,
    decimal Coverage,
    ReliabilityLight Level,
    string Reason,
    IReadOnlyList<ScoreComponent> Components,
    IReadOnlyList<string> CriticalFlags,
    IReadOnlyList<string> WarningFlags);

/// <summary>
/// Скоркарта надёжности выпуска (задача 46): чистая функция, три слоя — структурный (A), фундаментальный (B,
/// отчётность эмитента) и рыночный (C, динамика). <b>НЕ кредитный рейтинг</b>, оценка по открытым данным.
/// G-спред и перцентиль корзины в риск-скоринг НЕ входят — это ось Value (<c>valueVerdict</c>).
/// Маппинг на светофор — <see cref="Evaluate"/>; все пороги — <see cref="ReliabilityScoreOptions"/>.
/// </summary>
public static class ReliabilityScoreService
{
    private const string LayerA = "A";
    private const string LayerB = "B";
    private const string LayerC = "C";

    private const string SourceCard = "MOEX ISS: карточка бумаги";
    private const string SourceBank = "MOEX ISS: банк облигаций";
    private const string SourceHistory = "Банк облигаций: история";
    private const string SourceTInvest = "T-Invest: risk_level";
    private const string SourceGirBo = "ГИР БО ФНС (РСБУ, соло)";
    private const string SourceManualFinancials = "Ручной ввод отчётности";
    private const string SourceManualRating = "Ручной ввод рейтинга";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static ReliabilityScore Evaluate(ReliabilityScoreInput input)
    {
        var components = new List<ScoreComponent>();
        var critical = new List<string>();
        var warnings = new List<string>();

        var isSovereign = TryGetSovereignPoints(input.Sector, out var sovereignPoints);

        // ── Слой A ──
        components.Add(DefaultHistory(input, critical, warnings));
        components.Add(Listing(input));
        components.Add(NoOffer(input));
        components.Add(NotQualOnly(input));
        components.Add(BrokerRisk(input));
        var ratingComponent = ManualRating(input, warnings);
        if (ratingComponent is not null) components.Add(ratingComponent);

        // ── Слой B ──
        var financialsStale = false;
        if (isSovereign)
        {
            components.Add(new ScoreComponent(
                "sovereign", "Суверенный эмитент", null, input.Sector, sovereignPoints, sovereignPoints,
                "Сектор банка облигаций", false, LayerB));
        }
        else
        {
            var layerB = Fundamentals(input, critical, out financialsStale);
            components.AddRange(layerB);
        }

        // ── Слой C ──
        components.Add(SpreadMomentum(input, critical));
        components.Add(PriceDrawdown(input, warnings));
        components.Add(Liquidity(input));

        // ── Агрегаты ──
        var available = components.Where(c => !c.Missing).Sum(c => c.MaxPoints);
        var earned = components.Where(c => !c.Missing).Sum(c => c.Points);
        var totalMax = components.Sum(c => c.MaxPoints);

        var weightedAvailable = components
            .Where(c => !c.Missing)
            .Sum(c => c.Layer == LayerB && c.Stale ? c.MaxPoints * Opt.StaleCoverageMultiplier : c.MaxPoints);

        var score = available == 0 ? 0m : Math.Round(earned * 100m / available, 1, MidpointRounding.AwayFromZero);
        var coverage = totalMax == 0 ? 0m : Math.Round(weightedAvailable / totalMax, 3, MidpointRounding.AwayFromZero);

        var hasFundamentals = components.Any(c => c.Layer == LayerB && !c.Missing);
        var (level, reason) = ResolveLevel(input, score, coverage, available, critical, isSovereign, hasFundamentals, financialsStale);

        return new ReliabilityScore(score, coverage, level, reason, components, critical, warnings);
    }

    /// <summary>
    /// Скоркарта для бумаги, которой нет в банке облигаций (нечего оценивать): ни одного компонента, Score 0,
    /// Coverage 0, Yellow — «нет данных» не краснит и не зеленит бумагу.
    /// </summary>
    public static ReliabilityScore Unknown() => new(
        0m, 0m, ReliabilityLight.Yellow, "Жёлтый: бумаги нет в банке облигаций MOEX — оценка надёжности невозможна.", [], [], []);

    // ─── Светофор ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Маппинг на светофор. Red: IssuerDefaulted | InterestUncovered | NegativeEquity | SpreadShock | (ось
    /// ликвидности Caution И медианный оборот &lt; <see cref="ReliabilityScoreOptions.DeadLiquidityTurnoverRub"/>) |
    /// Score &lt; <see cref="ReliabilityScoreOptions.RedScoreBelow"/> (только когда есть хоть один компонент с данными).
    /// Green: Score ≥ 65, Coverage ≥ 0.6, листинг ∈ {1,2}, нет критических флагов и — для несуверенных —
    /// есть данные слоя B («Green — при живом фундаментале», docs §7.1; иначе банк с полным набором A+C
    /// набирал бы Coverage 0.62 и краснел бы зелёным без отчётности). Остальное — Yellow.
    /// </summary>
    private static (ReliabilityLight Level, string Reason) ResolveLevel(
        ReliabilityScoreInput input, decimal score, decimal coverage, int available,
        List<string> critical, bool isSovereign, bool hasFundamentals, bool financialsStale)
    {
        var redCauses = new List<string>();
        foreach (var flag in critical)
        {
            redCauses.Add(flag switch
            {
                ReliabilityFlags.IssuerDefaulted => "у эмитента есть история дефолта по выпускам",
                ReliabilityFlags.InterestUncovered => "операционная прибыль не покрывает проценты к уплате",
                ReliabilityFlags.NegativeEquity => "отрицательный капитал",
                ReliabilityFlags.SpreadShock => "резкое расширение G-спреда к собственной 20-дневной медиане",
                _ => flag,
            });
        }

        if (input.Liquidity == SignalLevel.Caution && input.TurnoverMedianRub is { } turnover
            && turnover < Opt.DeadLiquidityTurnoverRub)
        {
            redCauses.Add("ликвидность в зоне риска, медианный оборот ниже 300 тыс. ₽/день");
        }

        if (available > 0 && score < Opt.RedScoreBelow)
        {
            redCauses.Add($"балл {score.ToString("0.#", Inv)} ниже порога {Opt.RedScoreBelow.ToString("0", Inv)}");
        }

        if (redCauses.Count > 0)
        {
            return (ReliabilityLight.Red, $"Красный: {string.Join("; ", redCauses)}.");
        }

        var listingOk = input.ListLevel is 1 or 2;
        var greenOk = score >= Opt.GreenScoreAtLeast
                      && coverage >= Opt.GreenCoverageAtLeast
                      && listingOk
                      && (isSovereign || hasFundamentals);
        if (greenOk)
        {
            return (ReliabilityLight.Green,
                $"Зелёный: балл {score.ToString("0.#", Inv)}, покрытие данных {(coverage * 100m).ToString("0", Inv)}%, " +
                $"листинг {input.ListLevel}, критических флагов нет" +
                (isSovereign ? "; суверенный эмитент." : "."));
        }

        var gaps = new List<string>();
        if (score < Opt.GreenScoreAtLeast) gaps.Add($"балл {score.ToString("0.#", Inv)} ниже {Opt.GreenScoreAtLeast.ToString("0", Inv)}");
        if (coverage < Opt.GreenCoverageAtLeast)
        {
            gaps.Add($"покрытие данных {(coverage * 100m).ToString("0", Inv)}% ниже {(Opt.GreenCoverageAtLeast * 100m).ToString("0", Inv)}%");
        }
        if (!isSovereign && !hasFundamentals) gaps.Add("нет отчётности эмитента (слой B)");
        if (financialsStale) gaps.Add("отчётность эмитента устарела");
        if (!listingOk) gaps.Add(input.ListLevel is null ? "листинг неизвестен" : $"листинг {input.ListLevel} (для зелёного нужен 1 или 2)");

        return (ReliabilityLight.Yellow, gaps.Count == 0
            ? "Жёлтый: недостаточно оснований для зелёного."
            : $"Жёлтый: {string.Join("; ", gaps)}.");
    }

    // ─── Слой A ───────────────────────────────────────────────────────────────────────────

    private static ScoreComponent DefaultHistory(ReliabilityScoreInput input, List<string> critical, List<string> warnings)
    {
        const string key = "issuer_default_history";
        const string title = "История дефолтов эмитента";
        var scope = input.DefaultHistoryIsIssuerWide ? "по всем выпускам эмитента" : "по выпуску (эмитент не определён)";

        if (input.IssuerHasDefault == true)
        {
            critical.Add(ReliabilityFlags.IssuerDefaulted);
            return new ScoreComponent(key, title, null, $"допущен дефолт {scope}", 0, Opt.DefaultHistoryMaxPoints, SourceCard, false, LayerA);
        }

        if (input.IssuerHasTechnicalDefault == true)
        {
            warnings.Add(ReliabilityFlags.IssuerTechDefault);
            return new ScoreComponent(key, title, null, $"технический дефолт {scope}", Opt.DefaultHistoryTechDefaultPoints,
                Opt.DefaultHistoryMaxPoints, SourceCard, false, LayerA);
        }

        if (input.IssuerHasDefault == false && input.IssuerHasTechnicalDefault == false)
        {
            return new ScoreComponent(key, title, null, $"дефолтов нет {scope}", Opt.DefaultHistoryMaxPoints,
                Opt.DefaultHistoryMaxPoints, SourceCard, false, LayerA);
        }

        return Missing(key, title, Opt.DefaultHistoryMaxPoints, SourceCard, LayerA, "флаги дефолтов неизвестны (карточка не загружена)");
    }

    private static ScoreComponent Listing(ReliabilityScoreInput input)
    {
        var (points, display) = input.ListLevel switch
        {
            1 => (Opt.ListingMaxPoints, "уровень 1"),
            2 => (Opt.ListingLevel2Points, "уровень 2"),
            3 => (0, "уровень 3"),
            _ => (0, "вне котировальных списков / уровень неизвестен"),
        };
        return new ScoreComponent("listing", "Уровень листинга", input.ListLevel, display, points, Opt.ListingMaxPoints, SourceBank, false, LayerA);
    }

    private static ScoreComponent NoOffer(ReliabilityScoreInput input)
    {
        var hasOffer = input.OfferDate is { } date && date >= input.Today;
        return new ScoreComponent("no_offer", "Нет неисполненной оферты", null,
            hasOffer ? $"оферта {input.OfferDate:yyyy-MM-dd}" : "оферты нет",
            hasOffer ? 0 : Opt.NoOfferMaxPoints, Opt.NoOfferMaxPoints, SourceBank, false, LayerA);
    }

    private static ScoreComponent NotQualOnly(ReliabilityScoreInput input)
    {
        const string key = "not_qual_only";
        const string title = "Не только для квалифицированных";
        if (input.IsQualifiedInvestors is not { } qual)
        {
            return Missing(key, title, Opt.NotQualOnlyMaxPoints, SourceCard, LayerA, "признак квалификации неизвестен");
        }

        return new ScoreComponent(key, title, null, qual ? "только для квалифицированных" : "доступна всем инвесторам",
            qual ? 0 : Opt.NotQualOnlyMaxPoints, Opt.NotQualOnlyMaxPoints, SourceCard, false, LayerA);
    }

    private static ScoreComponent BrokerRisk(ReliabilityScoreInput input)
    {
        const string key = "broker_risk_level";
        const string title = "Оценка риска брокером";
        return input.BrokerRiskLevel switch
        {
            BrokerRiskLevel.Low => new ScoreComponent(key, title, 1, "низкий риск", Opt.BrokerRiskMaxPoints, Opt.BrokerRiskMaxPoints, SourceTInvest, false, LayerA),
            BrokerRiskLevel.Moderate => new ScoreComponent(key, title, 2, "средний риск", Opt.BrokerRiskModeratePoints, Opt.BrokerRiskMaxPoints, SourceTInvest, false, LayerA),
            BrokerRiskLevel.High => new ScoreComponent(key, title, 3, "высокий риск", 0, Opt.BrokerRiskMaxPoints, SourceTInvest, false, LayerA),
            _ => Missing(key, title, Opt.BrokerRiskMaxPoints, SourceTInvest, LayerA, "брокер оценку не дал"),
        };
    }

    /// <summary>Компонент ручного рейтинга; null — рейтингов нет (максимум слоя A не растёт). Несколько свежих
    /// рейтингов → консервативно берётся самый низкий; все просрочены → Missing («протух»).</summary>
    private static ScoreComponent? ManualRating(ReliabilityScoreInput input, List<string> warnings)
    {
        const string key = "manual_rating";
        const string title = "Рейтинг агентства (ручной ввод)";
        if (input.Ratings is not { Count: > 0 } ratings) return null;

        var cutoff = input.Today.AddMonths(-Opt.ManualRatingFreshMonths);
        var fresh = ratings.Where(r => r.RatingDate >= cutoff).ToList();
        if (fresh.Count == 0)
        {
            var newest = ratings.OrderByDescending(r => r.RatingDate).First();
            return Missing(key, title, Opt.ManualRatingMaxPoints, SourceManualRating, LayerA,
                $"рейтинг {newest.Agency} {newest.Rating} от {newest.RatingDate:yyyy-MM-dd} протух (старше {Opt.ManualRatingFreshMonths} мес.)");
        }

        (IssuerRatingInput Rating, int Points, bool Junk)? worst = null;
        foreach (var rating in fresh)
        {
            if (!TryScoreRating(rating.Rating, out var points, out var junk)) continue;
            if (worst is null || points < worst.Value.Points) worst = (rating, points, junk);
        }

        if (worst is null)
        {
            return Missing(key, title, Opt.ManualRatingMaxPoints, SourceManualRating, LayerA, "формат рейтинга не распознан");
        }

        if (worst.Value.Junk) warnings.Add(ReliabilityFlags.JunkRating);
        var r = worst.Value.Rating;
        return new ScoreComponent(key, title, null, $"{r.Agency}: {r.Rating} от {r.RatingDate:yyyy-MM-dd}",
            worst.Value.Points, Opt.ManualRatingMaxPoints, SourceManualRating, false, LayerA);
    }

    /// <summary>Разбор шкалы рейтингов (AAA/AA → 10, A → 8, BBB → 6, BB → 4, B → 2, ниже/отозван → 0 + junk).
    /// Модификаторы +/−, суффиксы (RU)/ru и пробелы игнорируются. false — формат не распознан.</summary>
    public static bool TryScoreRating(string rating, out int points, out bool junk)
    {
        points = 0;
        junk = false;
        if (!TryNormalizeRating(rating, out var normalized)) return false;

        switch (normalized)
        {
            case "AAA":
            case "AA":
                points = Opt.ManualRatingMaxPoints;
                break;
            case "A":
                points = 8;
                break;
            case "BBB":
                points = 6;
                break;
            case "BB":
                points = 4;
                break;
            case "B":
                points = 2;
                break;
            default: // CCC, CC, C, D, RD, SD, WD (отозван)
                points = 0;
                junk = true;
                break;
        }

        return true;
    }

    /// <summary>Нормализует рейтинг к базовой категории (AAA, AA, A, BBB, BB, B, CCC, CC, C, D, RD, SD, WD).
    /// «Отозван»/«withdrawn»/«NR» → WD. false — формат не распознан.</summary>
    public static bool TryNormalizeRating(string? rating, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(rating)) return false;

        var s = rating.Trim().ToUpperInvariant();
        if (s is "ОТОЗВАН" or "ОТОЗВАНА" or "WITHDRAWN" or "NR" or "WD")
        {
            normalized = "WD";
            return true;
        }

        s = s.Replace("(RU)", string.Empty).Replace("(РУ)", string.Empty).Replace("RU", string.Empty)
            .Replace(".", string.Empty).Replace("+", string.Empty).Replace("-", string.Empty).Replace("−", string.Empty).Replace(" ", string.Empty);

        if (s is "AAA" or "AA" or "A" or "BBB" or "BB" or "B" or "CCC" or "CC" or "C" or "D" or "RD" or "SD")
        {
            normalized = s;
            return true;
        }

        return false;
    }

    // ─── Слой B ───────────────────────────────────────────────────────────────────────────

    private static readonly (string Key, string Title, int Max)[] FundamentalMeta =
    [
        ("revenue_size", "Размер выручки", Opt.RevenueMaxPoints),
        ("interest_coverage", "Покрытие процентов", Opt.InterestCoverageMaxPoints),
        ("leverage", "Долговая нагрузка", Opt.LeverageMaxPoints),
        ("current_ratio", "Текущая ликвидность баланса", Opt.CurrentRatioMaxPoints),
        ("trend", "Динамика выручки и прибыли", Opt.TrendMaxPoints),
        ("altman_z_em", "Z″-счёт Альтмана (EM)", Opt.AltmanMaxPoints),
    ];

    private static List<ScoreComponent> Fundamentals(ReliabilityScoreInput input, List<string> critical, out bool stale)
    {
        stale = false;

        // Любой статус ГИР БО ≠ ok (cb, not_found, hidden, error, нет записи) — автоматическая отчётность
        // недоступна. Ручной ввод (source='manual') работает при любом статусе.
        var statusOk = input.FinancialsStatus == IssuerFinancialsStatuses.Ok;
        var usable = (input.Financials ?? [])
            .Where(f => statusOk || f.Source == IssuerFinancialsSources.Manual)
            .OrderByDescending(f => f.Period)
            .ToList();

        if (usable.Count == 0)
        {
            var reason = input.FinancialsStatus switch
            {
                IssuerFinancialsStatuses.Cb => "кредитная организация: отчитывается в ЦБ, в ГИР БО отчётности нет",
                IssuerFinancialsStatuses.NotFound => "эмитент не найден в ГИР БО",
                IssuerFinancialsStatuses.Hidden => "эмитент скрыл отчётность (пост. №1624) или подач нет",
                IssuerFinancialsStatuses.Error => "сбой загрузки из ГИР БО",
                IssuerFinancialsStatuses.Ok => "ГИР БО не вернул данных по годам",
                _ => "отчётность эмитента не загружена",
            };
            return FundamentalMeta
                .Select(m => Missing(m.Key, m.Title, m.Max, SourceGirBo, LayerB, reason))
                .ToList();
        }

        var latest = usable[0];
        var source = latest.Source == IssuerFinancialsSources.Manual ? SourceManualFinancials : SourceGirBo;
        var periodEnd = new DateOnly(latest.Period, 12, 31);
        var isStale = periodEnd.AddMonths(Opt.FinancialsStaleMonths) < input.Today;
        stale = isStale;
        var staleNote = isStale ? $" [Stale: последняя отчётность за {latest.Period}]" : string.Empty;

        ScoreComponent Tag(ScoreComponent c) => c with
        {
            Source = $"{source}, {latest.Period}",
            RawDisplay = c.RawDisplay + staleNote,
            Stale = isStale,
        };

        var result = new List<ScoreComponent>
        {
            Tag(RevenueSize(latest)),
            Tag(InterestCoverage(latest, critical)),
            Tag(Leverage(latest, critical)),
            Tag(CurrentRatio(latest)),
            Tag(Trend(usable)),
            Tag(AltmanZ(latest)),
        };
        return result;
    }

    private static ScoreComponent RevenueSize(IssuerFinancials f)
    {
        const string key = "revenue_size";
        const string title = "Размер выручки";
        if (f.Revenue is not { } revenue) return Missing(key, title, Opt.RevenueMaxPoints, SourceGirBo, LayerB, "выручка (2110) не указана");

        var points = revenue >= Opt.RevenueLargeRub ? Opt.RevenueMaxPoints : revenue >= Opt.RevenueMediumRub ? Opt.RevenueMediumPoints : 0;
        return new ScoreComponent(key, title, revenue, $"выручка {FormatRub(revenue)}", points, Opt.RevenueMaxPoints, SourceGirBo, false, LayerB);
    }

    private static ScoreComponent InterestCoverage(IssuerFinancials f, List<string> critical)
    {
        const string key = "interest_coverage";
        const string title = "Покрытие процентов";
        var debtKnown = f.LongDebt is not null || f.ShortDebt is not null;
        var debt = (f.LongDebt ?? 0m) + (f.ShortDebt ?? 0m);

        if (f.InterestPaid is not { } interest || interest <= 0m)
        {
            if (debtKnown && debt <= 0m)
            {
                return new ScoreComponent(key, title, null, "заёмных средств и процентов нет", Opt.InterestCoverageMaxPoints,
                    Opt.InterestCoverageMaxPoints, SourceGirBo, false, LayerB);
            }

            return Missing(key, title, Opt.InterestCoverageMaxPoints, SourceGirBo, LayerB,
                debtKnown ? "проценты к уплате (2330) не указаны при ненулевом долге" : "проценты (2330) и долг не указаны");
        }

        if (f.OperatingProfit is not { } profit)
        {
            return Missing(key, title, Opt.InterestCoverageMaxPoints, SourceGirBo, LayerB, "прибыль от продаж (2200) не указана");
        }

        var ratio = profit / interest;
        var display = $"2200/2330 = {ratio.ToString("0.0", Inv)}×";
        if (ratio >= Opt.InterestCoverageStrong) return new ScoreComponent(key, title, ratio, display, Opt.InterestCoverageMaxPoints, Opt.InterestCoverageMaxPoints, SourceGirBo, false, LayerB);
        if (ratio >= Opt.InterestCoverageGood) return new ScoreComponent(key, title, ratio, display, 5, Opt.InterestCoverageMaxPoints, SourceGirBo, false, LayerB);
        if (ratio >= Opt.InterestCoverageMin) return new ScoreComponent(key, title, ratio, display, 2, Opt.InterestCoverageMaxPoints, SourceGirBo, false, LayerB);

        critical.Add(ReliabilityFlags.InterestUncovered);
        return new ScoreComponent(key, title, ratio, display, 0, Opt.InterestCoverageMaxPoints, SourceGirBo, false, LayerB);
    }

    private static ScoreComponent Leverage(IssuerFinancials f, List<string> critical)
    {
        const string key = "leverage";
        const string title = "Долговая нагрузка";
        if (f.Equity is not { } equity) return Missing(key, title, Opt.LeverageMaxPoints, SourceGirBo, LayerB, "капитал (1300) не указан");

        if (equity <= 0m)
        {
            critical.Add(ReliabilityFlags.NegativeEquity);
            return new ScoreComponent(key, title, null, $"капитал {FormatRub(equity)} (≤ 0)", 0, Opt.LeverageMaxPoints, SourceGirBo, false, LayerB);
        }

        if (f.LongDebt is null && f.ShortDebt is null)
        {
            return Missing(key, title, Opt.LeverageMaxPoints, SourceGirBo, LayerB, "заёмные средства (1410/1510) не указаны");
        }

        // Чистый долг = 1410 + 1510 − 1250; деньги (1250) не указаны — считаем 0 (консервативно).
        var netDebt = (f.LongDebt ?? 0m) + (f.ShortDebt ?? 0m) - (f.Cash ?? 0m);
        var ratio = netDebt / equity;
        var display = $"чистый долг / капитал = {ratio.ToString("0.0", Inv)}×";
        var points = ratio < Opt.LeverageLow ? Opt.LeverageMaxPoints : ratio < Opt.LeverageMedium ? 5 : ratio < Opt.LeverageHigh ? 2 : 0;
        return new ScoreComponent(key, title, ratio, display, points, Opt.LeverageMaxPoints, SourceGirBo, false, LayerB);
    }

    private static ScoreComponent CurrentRatio(IssuerFinancials f)
    {
        const string key = "current_ratio";
        const string title = "Текущая ликвидность баланса";
        if (f.CurrentAssets is not { } assets || f.CurrentLiabilities is not { } liabilities)
        {
            return Missing(key, title, Opt.CurrentRatioMaxPoints, SourceGirBo, LayerB, "оборотные активы (1200) или краткосрочные обязательства (1500) не указаны");
        }

        if (liabilities <= 0m)
        {
            return new ScoreComponent(key, title, null, "краткосрочных обязательств нет", Opt.CurrentRatioMaxPoints,
                Opt.CurrentRatioMaxPoints, SourceGirBo, false, LayerB);
        }

        var ratio = assets / liabilities;
        var display = $"1200/1500 = {ratio.ToString("0.00", Inv)}";
        var points = ratio >= Opt.CurrentRatioStrong ? Opt.CurrentRatioMaxPoints : ratio >= Opt.CurrentRatioMin ? 2 : 0;
        return new ScoreComponent(key, title, ratio, display, points, Opt.CurrentRatioMaxPoints, SourceGirBo, false, LayerB);
    }

    /// <summary>Тренд по ≤ 3 последним годам, где известны и выручка, и чистая прибыль: рост = последний год больше
    /// самого раннего окна. Оба растут → 6; оба падают или прибыль &lt; 0 в последнем году → 0; иначе → 3.
    /// Меньше 2 таких лет → Missing.</summary>
    private static ScoreComponent Trend(IReadOnlyList<IssuerFinancials> yearsNewestFirst)
    {
        const string key = "trend";
        const string title = "Динамика выручки и прибыли";
        var window = yearsNewestFirst
            .Where(y => y.Revenue is not null && y.NetProfit is not null)
            .Take(3)
            .ToList();
        if (window.Count < 2)
        {
            return Missing(key, title, Opt.TrendMaxPoints, SourceGirBo, LayerB, "меньше 2 лет данных по выручке и чистой прибыли");
        }

        var newest = window[0];
        var oldest = window[^1];
        var revenueDelta = newest.Revenue!.Value - oldest.Revenue!.Value;
        var profitDelta = newest.NetProfit!.Value - oldest.NetProfit!.Value;
        var span = $"{oldest.Period}→{newest.Period}";
        var display = $"{span}: выручка {(revenueDelta > 0 ? "растёт" : revenueDelta < 0 ? "падает" : "без изменений")}, " +
                      $"чистая прибыль {(profitDelta > 0 ? "растёт" : profitDelta < 0 ? "падает" : "без изменений")}";

        if (newest.NetProfit < 0m)
        {
            return new ScoreComponent(key, title, null, display + "; убыток в последнем году", 0, Opt.TrendMaxPoints, SourceGirBo, false, LayerB);
        }

        if (revenueDelta > 0 && profitDelta > 0) return new ScoreComponent(key, title, null, display, Opt.TrendMaxPoints, Opt.TrendMaxPoints, SourceGirBo, false, LayerB);
        if (revenueDelta < 0 && profitDelta < 0) return new ScoreComponent(key, title, null, display, 0, Opt.TrendMaxPoints, SourceGirBo, false, LayerB);
        return new ScoreComponent(key, title, null, display, Opt.TrendMixedPoints, Opt.TrendMaxPoints, SourceGirBo, false, LayerB);
    }

    /// <summary>
    /// Z″-EM (Altman 2005, «An emerging market credit scoring system for corporate bonds», Emerging Markets
    /// Review; основа — Altman, Hartzell, Peck 1995): Z = 3.25 + 6.56·X1 + 3.26·X2 + 6.72·X3 + 1.05·X4, где
    /// X1 = (1200−1500)/1600, X2 = 1370/1600, X3 = 2200/1600 (прибыль от продаж как приближение EBIT),
    /// X4 = 1300/(1600−1300) = капитал / обязательства. Константа 3.25 сдвигает шкалу так, что 0 = рейтинг D;
    /// поэтому пороги зон «безопасно / дистресс» оригинального Z″ (2.6 / 1.1) сдвинуты на 3.25 →
    /// 5.85 / 4.35 (<see cref="ReliabilityScoreOptions.AltmanSafeZone"/>/<see cref="ReliabilityScoreOptions.AltmanDistressZone"/>).
    /// Коэффициенты откалиброваны на облигациях EM, для РФ применяются как есть — это один компонент из многих.
    /// </summary>
    public static decimal? ComputeAltmanZEm(IssuerFinancials f)
    {
        if (f.CurrentAssets is not { } ca || f.CurrentLiabilities is not { } cl || f.TotalAssets is not { } ta
            || f.RetainedEarnings is not { } re || f.OperatingProfit is not { } op || f.Equity is not { } equity)
        {
            return null;
        }

        var liabilities = ta - equity;
        if (ta <= 0m || liabilities <= 0m) return null;

        var x1 = (ca - cl) / ta;
        var x2 = re / ta;
        var x3 = op / ta;
        var x4 = equity / liabilities;
        return 3.25m + 6.56m * x1 + 3.26m * x2 + 6.72m * x3 + 1.05m * x4;
    }

    private static ScoreComponent AltmanZ(IssuerFinancials f)
    {
        const string key = "altman_z_em";
        const string title = "Z″-счёт Альтмана (EM)";
        if (ComputeAltmanZEm(f) is not { } z)
        {
            return Missing(key, title, Opt.AltmanMaxPoints, SourceGirBo, LayerB, "не хватает строк баланса (1200, 1500, 1600, 1370, 1300) или 2200");
        }

        var display = $"Z″-EM = {z.ToString("0.00", Inv)}";
        var points = z >= Opt.AltmanSafeZone ? Opt.AltmanMaxPoints : z >= Opt.AltmanDistressZone ? Opt.AltmanGreyPoints : 0;
        return new ScoreComponent(key, title, Math.Round(z, 2), display, points, Opt.AltmanMaxPoints, SourceGirBo, false, LayerB);
    }

    // ─── Слой C ───────────────────────────────────────────────────────────────────────────

    private static ScoreComponent SpreadMomentum(ReliabilityScoreInput input, List<string> critical)
    {
        const string key = "spread_momentum";
        const string title = "Динамика G-спреда";
        var history = input.GSpreadHistory ?? [];
        if (input.GSpreadFraction is not { } today || history.Count < Opt.MinHistoryDays)
        {
            return Missing(key, title, Opt.SpreadMomentumMaxPoints, SourceHistory, LayerC,
                input.GSpreadFraction is null ? "G-спред неизвестен" : $"истории меньше {Opt.MinHistoryDays} торговых дней");
        }

        var median = Median(history);
        var shift = today - median;
        // Единицы: shift — ДОЛЯ; в подписи б.п. = доля × 10000 (одна конверсия, для человека).
        var display = $"{(shift * 10000m).ToString("+0;-0;0", Inv)} б.п. к медиане за {history.Count} дн.";

        if (shift <= Opt.SpreadMomentumCalmFraction)
            return new ScoreComponent(key, title, shift, display, Opt.SpreadMomentumMaxPoints, Opt.SpreadMomentumMaxPoints, SourceHistory, false, LayerC);
        if (shift <= Opt.SpreadMomentumShockFraction)
            return new ScoreComponent(key, title, shift, display, Opt.SpreadMomentumWarnPoints, Opt.SpreadMomentumMaxPoints, SourceHistory, false, LayerC);

        critical.Add(ReliabilityFlags.SpreadShock);
        return new ScoreComponent(key, title, shift, display, 0, Opt.SpreadMomentumMaxPoints, SourceHistory, false, LayerC);
    }

    private static ScoreComponent PriceDrawdown(ReliabilityScoreInput input, List<string> warnings)
    {
        const string key = "price_drawdown";
        const string title = "Просадка цены";
        var history = input.PriceHistory ?? [];
        if (input.PricePercent is not { } price || history.Count < Opt.MinHistoryDays)
        {
            return Missing(key, title, Opt.DrawdownMaxPoints, SourceHistory, LayerC,
                input.PricePercent is null ? "цена неизвестна" : $"истории меньше {Opt.MinHistoryDays} торговых дней");
        }

        var peak = Math.Max(history.Max(), price);
        var drawdown = peak <= 0m ? 0m : (peak - price) / peak;
        // Единицы: drawdown — ДОЛЯ; в подписи проценты = доля × 100 (одна конверсия, для человека).
        var display = $"{(drawdown * 100m).ToString("0.0", Inv)}% от максимума за {history.Count} дн.";

        if (drawdown < Opt.DrawdownCalmFraction)
            return new ScoreComponent(key, title, drawdown, display, Opt.DrawdownMaxPoints, Opt.DrawdownMaxPoints, SourceHistory, false, LayerC);
        if (drawdown <= Opt.DrawdownWarnFraction)
            return new ScoreComponent(key, title, drawdown, display, Opt.DrawdownWarnPoints, Opt.DrawdownMaxPoints, SourceHistory, false, LayerC);

        warnings.Add(ReliabilityFlags.PriceDrawdown);
        return new ScoreComponent(key, title, drawdown, display, 0, Opt.DrawdownMaxPoints, SourceHistory, false, LayerC);
    }

    private static ScoreComponent Liquidity(ReliabilityScoreInput input)
    {
        const string key = "liquidity";
        const string title = "Ликвидность";
        if (input.Liquidity is not { } level || input.LiquidityScoreRaw is null or LiquidityScore.None)
        {
            return Missing(key, title, Opt.LiquidityMaxPoints, SourceBank, LayerC, "недостаточно данных по обороту и стакану");
        }

        var display = input.TurnoverMedianRub is { } turnover
            ? $"{level}, медианный оборот {FormatRub(turnover)}/день"
            : level.ToString();
        var points = level switch
        {
            SignalLevel.Good => Opt.LiquidityMaxPoints,
            SignalLevel.Neutral => Opt.LiquidityNeutralPoints,
            _ => 0,
        };
        return new ScoreComponent(key, title, input.TurnoverMedianRub, display, points, Opt.LiquidityMaxPoints, SourceBank, false, LayerC);
    }

    // ─── Хелперы ──────────────────────────────────────────────────────────────────────────

    private static ScoreComponent Missing(string key, string title, int max, string source, string layer, string reason) =>
        new(key, title, null, $"нет данных: {reason}", 0, max, source, true, layer);

    private static bool TryGetSovereignPoints(string? sector, out int points)
    {
        if (string.Equals(sector, CandidateRiskSignalService.GovernmentSectorLabel, StringComparison.OrdinalIgnoreCase))
        {
            points = Opt.SovereignGovernmentPoints;
            return true;
        }

        if (string.Equals(sector, CandidateRiskSignalService.MunicipalSectorLabel, StringComparison.OrdinalIgnoreCase))
        {
            points = Opt.SovereignMunicipalPoints;
            return true;
        }

        points = 0;
        return false;
    }

    private static decimal Median(IReadOnlyList<decimal> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 0 ? (sorted[mid - 1] + sorted[mid]) / 2m : sorted[mid];
    }

    /// <summary>Рубли словами: «4.5 млрд ₽», «320 млн ₽», «12 тыс. ₽».</summary>
    private static string FormatRub(decimal rub)
    {
        var abs = Math.Abs(rub);
        if (abs >= 1_000_000_000m) return $"{(rub / 1_000_000_000m).ToString("0.0", Inv)} млрд ₽";
        if (abs >= 1_000_000m) return $"{(rub / 1_000_000m).ToString("0", Inv)} млн ₽";
        return $"{(rub / 1_000m).ToString("0", Inv)} тыс. ₽";
    }
}

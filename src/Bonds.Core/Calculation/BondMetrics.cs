namespace Bonds.Core.Calculation;

/// <summary>
/// Результат расчёта метрик одной облигации на дату (spec §6; plan/05 Часть C).
/// Несёт значения И флаги качества/применимости — потребители (этапы 06, 08, 09) показывают
/// пометки в UI. Все поля — простые сериализуемые типы (value types / string), без ссылок на
/// модели БД, что делает результат пригодным для последующей персистентности снимков метрик
/// (plan/05 Часть E, реализуется на этапе 07) без дополнительного маппинга.
/// Без побочных эффектов — чистый DTO, как и весь движок.
/// </summary>
public sealed class BondMetrics
{
    public required ulong InstrumentId { get; init; }
    public required DateOnly AsOf { get; init; }

    public required decimal CleanPrice { get; init; }
    public required decimal AccruedInterest { get; init; }
    public required decimal DirtyPrice { get; init; }

    /// <summary>Источник НКД: пришёл готовым (T-Invest/MOEX) или посчитан фолбэком движка.</summary>
    public required bool AccruedInterestEstimatedByEngine { get; init; }

    /// <summary>
    /// YTM (эффективная, годовое сложное начисление). Null для флоатера/индексируемой бумаги
    /// (spec §6 «Краевые случаи») или если расчёт не сходится / входные данные недостаточны.
    /// </summary>
    public decimal? YtmEffective { get; init; }

    /// <summary>YTM в простой (линейной) форме — без сложного начисления.</summary>
    public decimal? YtmSimple { get; init; }

    /// <summary>
    /// Текущая купонная доходность к грязной цене (годовая) — для флоатеров/индексируемых бумаг
    /// заменяет YTM (spec §6 «Краевые случаи»); считается и для обычных бумаг как побочная метрика.
    /// </summary>
    public decimal? CurrentYield { get; init; }

    public decimal? MacaulayDuration { get; init; }
    public decimal? ModifiedDuration { get; init; }
    public decimal? Convexity { get; init; }

    /// <summary>PVBP = модифицированная дюрация × грязная цена × 0.0001 (spec §6.7).</summary>
    public decimal? Pvbp { get; init; }

    /// <summary>G-спред = YTM бумаги − значение безрисковой кривой на сопоставимый срок (spec §6.8).</summary>
    public decimal? GSpread { get; init; }

    /// <summary>Горизонт, до которого фактически считались метрики (погашение либо оферта).</summary>
    public required DateOnly HorizonDate { get; init; }

    // ─── Флаги качества/применимости (plan/05 Часть C) ────────────────────────

    /// <summary>Бумага с плавающим купоном — YTM не считается, см. <see cref="CurrentYield"/>.</summary>
    public required bool IsFloater { get; init; }

    /// <summary>Бумага с индексируемым номиналом/купоном (напр. ОФЗ-ИН) — трактуется как флоатер-подобная.</summary>
    public required bool IsIndexed { get; init; }

    /// <summary>Метрика — оценка, а не точное значение (флоатер/индексируемая/неполные данные).</summary>
    public required bool IsEstimated { get; init; }

    /// <summary>Исходные данные по инструменту неполные (spec §4.4) — метрики недостоверны.</summary>
    public required bool DataIncomplete { get; init; }

    /// <summary>Метрики посчитаны к ближайшей неисполненной оферте, а не к погашению (spec §7.3).</summary>
    public required bool CalculatedToOffer { get; init; }

    /// <summary>Признак того, что использовалась амортизация при построении денежного потока.</summary>
    public required bool HasAmortization { get; init; }

    /// <summary>Признак сходимости YTM по Ньютону (для диагностики/мониторинга качества расчёта).</summary>
    public bool? YtmConvergedByNewton { get; init; }

    /// <summary>Человекочитаемые заметки о допущениях/деградации расчёта (накопительно).</summary>
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

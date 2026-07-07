using Bonds.Core.Models;

namespace Bonds.Core.Universe;

/// <summary>Причина, по которой бумага банка облигаций скрыта гигиеническим фильтром по умолчанию
/// (задача 26 часть C.4). None — бумага видима. Порядок значений не имеет значения (не флаги) —
/// <see cref="UniverseHygieneFilter.Evaluate"/> возвращает первую применимую причину по порядку
/// проверок, описанному в doc-comment метода.</summary>
public enum HygieneHiddenReason
{
    None = 0,

    /// <summary>Оборот за сегодня меньше <see cref="UniverseHygieneOptions.MinTurnoverRub"/>.</summary>
    LowTurnover,

    /// <summary>Третий уровень листинга MOEX (LISTLEVEL=3) и опция скрытия включена (дефолт).</summary>
    ListLevelThree,

    /// <summary>Доходность выше <see cref="UniverseHygieneOptions.MaxSaneYieldFraction"/> — рынок
    /// закладывает дефолт/техническую котировку, а не реальную доходность к погашению.</summary>
    ImplausibleYield,

    /// <summary>Дюрация или цена не пришли от MOEX (нет сделок/нет корректных marketdata) —
    /// сравнение с такой бумагой ненадёжно.</summary>
    MissingDurationOrPrice,

    /// <summary>До погашения (или ближайшей оферты) осталось меньше
    /// <see cref="UniverseHygieneOptions.MinDaysToMaturity"/> дней — почти денежный эквивалент,
    /// метрики теряют смысл.</summary>
    NearMaturity,
}

/// <summary>Пороги гигиенического фильтра (задача 26 часть C.4) — намеренно консервативные
/// дефолты уровня "разумно", не откалиброванные на исторических данных.</summary>
public sealed class UniverseHygieneOptions
{
    /// <summary>Дефолт: 100 тыс ₽/день — ниже этого оборота бумага считается неликвидной для
    /// целей сравнения (не значит "не торгуется совсем").</summary>
    public decimal MinTurnoverRub { get; set; } = 100_000m;

    /// <summary>Дефолт: true — скрывать LISTLEVEL=3 (некотировальный список, повышенный риск/
    /// заведомо ограниченная прозрачность отчётности эмитента).</summary>
    public bool HideListLevelThree { get; set; } = true;

    /// <summary>Дефолт: 0.45 (45%) — выше этого рынок закладывает вероятный дефолт, доходность
    /// не отражает разумную оценку "премии за риск", а технический артефакт котировки.</summary>
    public decimal MaxSaneYieldFraction { get; set; } = 0.45m;

    /// <summary>Дефолт: 14 дней — бумаги ближе к погашению/оферте почти денежный эквивалент,
    /// сравнение по YTM/дюрации для них некорректно.</summary>
    public int MinDaysToMaturity { get; set; } = 14;
}

/// <summary>
/// Задача 26 часть C.4 — чистая функция классификации "скрыта ли бумага банка облигаций по
/// умолчанию" с указанием причины (для UI "скрыто N: неликвид"). НЕ удаляет бумагу из банка —
/// только помечает, вызывающий код (API часть D) решает, включать ли скрытые в выдачу
/// (параметр includeHidden).
/// </summary>
public static class UniverseHygieneFilter
{
    /// <summary>
    /// Возвращает причину скрытия (первую применимую в порядке: оборот → листинг → доходность →
    /// отсутствие дюрации/цены → близость погашения) либо <see cref="HygieneHiddenReason.None"/>,
    /// если бумага видима. <paramref name="today"/> передаётся явно (а не берётся из DateTime.UtcNow)
    /// для тестируемости — тот же паттерн, что остальные чистые калькуляторы этого проекта.
    /// </summary>
    public static HygieneHiddenReason Evaluate(BondUniverseEntry entry, UniverseHygieneOptions options, DateOnly today)
    {
        var turnover = entry.TurnoverRub ?? 0m;
        if (turnover < options.MinTurnoverRub)
        {
            return HygieneHiddenReason.LowTurnover;
        }

        if (options.HideListLevelThree && entry.ListLevel == 3)
        {
            return HygieneHiddenReason.ListLevelThree;
        }

        if (entry.YieldFraction is { } y && y > options.MaxSaneYieldFraction)
        {
            return HygieneHiddenReason.ImplausibleYield;
        }

        if (entry.DurationYears is null || entry.PricePercent is null)
        {
            return HygieneHiddenReason.MissingDurationOrPrice;
        }

        // Ближайшая дата "выхода из жизни" бумаги для сравнения — оферта, если она раньше погашения
        // (после оферты держатель может/будет обязан выйти) — тот же принцип горизонта, что
        // OfferCutoffResolver использует для точного движка, здесь — упрощённо min(maturity, offer).
        var nearestExitDate = entry.OfferDate is { } offer && entry.MaturityDate is { } mat
            ? (offer < mat ? offer : mat)
            : entry.MaturityDate ?? entry.OfferDate;

        if (nearestExitDate is { } exit)
        {
            var daysToExit = exit.DayNumber - today.DayNumber;
            if (daysToExit < options.MinDaysToMaturity)
            {
                return HygieneHiddenReason.NearMaturity;
            }
        }

        return HygieneHiddenReason.None;
    }
}

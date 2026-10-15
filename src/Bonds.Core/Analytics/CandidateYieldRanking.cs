using Bonds.Core.Models;

namespace Bonds.Core.Analytics;

/// <summary>Ранг доходности кандидата банка (задача 50). Все доходности — ДОЛИ (0.15 = 15%).</summary>
/// <param name="AfterTaxYieldFraction">Доходность к worst после НДФЛ (<see cref="AfterTaxYield"/>) — ключ сортировки.</param>
/// <param name="WorstYieldFraction">Доходность к worst ДО налога: min(YTM банка, доходность к оферте).</param>
/// <param name="Basis">К чему считана доходность: <see cref="CandidateYieldRanking.BasisOffer"/> /
/// <see cref="CandidateYieldRanking.BasisMaturity"/>.</param>
public sealed record RankedYield(decimal AfterTaxYieldFraction, decimal WorstYieldFraction, string Basis);

/// <summary>
/// Ранжирование кандидатов банка по ПОСЛЕ-НАЛОГОВОЙ доходности к worst (задача 50):
/// <c>y_rank = min(yield, yield_to_offer ?? yield) × (1 − 13%)</c>. Вместо сырой YTM, которая ставила наверх
/// бумаги с офертой (доходность «к погашению» недостижима, если эмитент выкупит раньше), самые длинные и
/// рискованные выпуски. Основа подписи (<c>Basis</c>): «offer», если худший сценарий — оферта (или биржа
/// уже считает YIELD к оферте — <c>YIELDDATETYPE=OFFER</c>), иначе «maturity».
/// </summary>
public static class CandidateYieldRanking
{
    public const string BasisOffer = "offer";
    public const string BasisMaturity = "maturity";

    /// <summary>MOEX <c>YIELDDATETYPE</c>: биржевая YIELD посчитана к оферте.</summary>
    private const string YieldDateTypeOffer = "OFFER";

    /// <summary>Ранг бумаги; null — у банк-записи нет доходности (нечем ранжировать).</summary>
    public static RankedYield? Evaluate(BondUniverseEntry entry)
    {
        if (entry.YieldFraction is not { } yield) return null;

        var toOffer = entry.YieldToOfferFraction;
        var worst = toOffer is { } o ? Math.Min(yield, o) : yield;

        var isOffer = (toOffer is { } offerYield && offerYield <= yield)
            || string.Equals(entry.YieldDateType, YieldDateTypeOffer, StringComparison.OrdinalIgnoreCase);

        return new RankedYield(AfterTaxYield.Apply(worst), worst, isOffer ? BasisOffer : BasisMaturity);
    }

    /// <summary>
    /// Записи с доходностью по убыванию после-налогового ранга. Детерминированно: при равенстве — по SECID
    /// (порядок входа не влияет), без записей без доходности.
    /// </summary>
    public static List<(BondUniverseEntry Entry, RankedYield Rank)> Rank(IEnumerable<BondUniverseEntry> entries) =>
        entries
            .Select(e => (Entry: e, Rank: Evaluate(e)))
            .Where(x => x.Rank is not null)
            .Select(x => (x.Entry, Rank: x.Rank!))
            .OrderByDescending(x => x.Rank.AfterTaxYieldFraction)
            .ThenBy(x => x.Entry.Secid, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Топ-<paramref name="limit"/> записей по после-налоговому рангу (<see cref="Rank"/>) — пул кандидатов
    /// аллокации банка (задача 50): пул отбирается по ranked-доходности к worst, а не по сырой YTM.
    /// </summary>
    public static List<BondUniverseEntry> TopByRank(IEnumerable<BondUniverseEntry> entries, int limit) =>
        Rank(entries).Take(limit).Select(x => x.Entry).ToList();
}

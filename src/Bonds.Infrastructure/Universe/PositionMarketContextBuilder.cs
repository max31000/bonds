using Bonds.Core.Analytics;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Bonds.Core.Signals;

namespace Bonds.Infrastructure.Universe;

/// <summary>Результат <see cref="PositionMarketContextBuilder"/>: входы рыночных и кредитного правил движка событий.</summary>
public sealed record PositionMarketInputs(
    IReadOnlyList<PositionMarketContext> Market,
    IReadOnlyList<IssuerDefaultContext> IssuerDefaults)
{
    public static PositionMarketInputs Empty { get; } = new([], []);
}

/// <summary>
/// Задача 49: БАТЧЕВАЯ сборка входов событий SpreadWidening / PriceDrawdown / IssuerDefaultEvent для открытых позиций —
/// один запрос на справочник (банк по ISIN, история 20 дней по secid, карточки, дефолт-агрегаты эмитентов), без N+1.
/// Сегодняшнее значение берётся из текущей банк-записи, а история — за ПРОШЛЫЕ торговые дни (сегодняшняя строка
/// истории пишется только вечером и в сравнение не входит — тот же приём, что у <see cref="ReliabilityScoreProvider"/>).
/// Позиции без ISIN или вне банка рыночного контекста не получают (неторгуемая бумага — правила молчат).
/// <para>
/// <b>Единицы:</b> G-спред — ДОЛЯ (<see cref="BondUniverseHistoryPoint.ResolvedGspreadFraction"/>), цена — % от номинала.
/// Флаги дефолта <c>null</c> = «неизвестно» — события не порождают.
/// </para>
/// </summary>
public sealed class PositionMarketContextBuilder
{
    /// <summary>Сколько прошлых торговых дней истории сравнивать (20, как у скоркарты).</summary>
    public const int HistoryDays = ReliabilityScoreProvider.HistoryTradingDays;

    /// <summary>Сколько последних дат снимка запросить: +1 запас на случай, если сегодняшняя строка уже записана.</summary>
    public const int HistoryFetchDays = HistoryDays + 1;

    private readonly IBondUniverseRepository _universeRepo;
    private readonly ISecurityDetailsRepository _detailsRepo;

    public PositionMarketContextBuilder(IBondUniverseRepository universeRepo, ISecurityDetailsRepository detailsRepo)
    {
        _universeRepo = universeRepo;
        _detailsRepo = detailsRepo;
    }

    public async Task<PositionMarketInputs> BuildAsync(
        IReadOnlyList<PortfolioHolding> holdings, DateOnly asOf, CancellationToken ct = default)
    {
        var open = holdings.Where(h => h.Quantity > 0m && !string.IsNullOrWhiteSpace(h.Isin)).ToList();
        if (open.Count == 0) return PositionMarketInputs.Empty;

        var entries = (await _universeRepo.GetByIsinsAsync(open.Select(h => h.Isin!).ToList(), ct))
            .Where(e => !string.IsNullOrWhiteSpace(e.Isin))
            .GroupBy(e => e.Isin!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        if (entries.Count == 0) return PositionMarketInputs.Empty;

        var secids = entries.Values.Select(e => e.Secid).ToList();
        var historyBySecid = (await _universeRepo.GetRecentHistoryForSecidsAsync(secids, HistoryFetchDays, ct))
            .Where(p => p.SnapshotDate < asOf)
            .GroupBy(p => p.Secid, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(p => p.SnapshotDate).Take(HistoryDays).ToList(),
                StringComparer.OrdinalIgnoreCase);

        var market = new List<PositionMarketContext>(open.Count);
        foreach (var holding in open)
        {
            if (!entries.TryGetValue(holding.Isin!, out var entry)) continue;
            historyBySecid.TryGetValue(entry.Secid, out var points);
            points ??= [];

            market.Add(new PositionMarketContext
            {
                InstrumentId = holding.InstrumentId,
                Isin = holding.Isin,
                // Флоатер/индексируемая: по оценке движка либо по признаку банка — G-спред несравним.
                IsFloater = holding.IsFloater || holding.IsIndexed || entry.IsFloater == true,
                GSpreadFraction = entry.EffectiveGspreadFraction,
                GSpreadHistory = points.Select(p => p.ResolvedGspreadFraction).Where(v => v is not null).Select(v => v!.Value).ToList(),
                PricePercent = entry.PricePercent,
                PriceHistory = points.Select(p => p.PricePercent).Where(v => v is not null).Select(v => v!.Value).ToList(),
            });
        }

        var issuerDefaults = await BuildIssuerDefaultsAsync(open, entries, secids, ct);
        return new PositionMarketInputs(market, issuerDefaults);
    }

    /// <summary>Дефолт-агрегаты по ВСЕМ выпускам эмитентов позиций (<c>emitent_id</c> банка, при его отсутствии — из карточки);
    /// в результат попадают только эмитенты с хотя бы одним признаком = true.</summary>
    private async Task<IReadOnlyList<IssuerDefaultContext>> BuildIssuerDefaultsAsync(
        List<PortfolioHolding> open, Dictionary<string, BondUniverseEntry> entries, List<string> secids, CancellationToken ct)
    {
        var details = (await _detailsRepo.GetBySecidsAsync(secids, ct))
            .ToDictionary(d => d.Secid, StringComparer.OrdinalIgnoreCase);

        int? EmitentOf(BondUniverseEntry entry) =>
            entry.EmitentId ?? (details.TryGetValue(entry.Secid, out var card) ? card.EmitentId : null);

        // Эмитент → первая позиция (ссылка карточки события) и название.
        var firstHoldingByEmitent = new Dictionary<int, PortfolioHolding>();
        var titleByEmitent = new Dictionary<int, string?>();
        foreach (var holding in open)
        {
            if (!entries.TryGetValue(holding.Isin!, out var entry)) continue;
            if (EmitentOf(entry) is not { } emitentId) continue;
            if (firstHoldingByEmitent.TryAdd(emitentId, holding)) titleByEmitent[emitentId] = entry.EmitentTitle;
        }

        if (firstHoldingByEmitent.Count == 0) return [];

        var aggregates = await _detailsRepo.GetEmitentDefaultHistoryAsync(firstHoldingByEmitent.Keys.ToList(), ct);
        var result = new List<IssuerDefaultContext>();
        foreach (var (emitentId, holding) in firstHoldingByEmitent)
        {
            if (!aggregates.TryGetValue(emitentId, out var aggregate)) continue;
            if (aggregate.HasDefault != true && aggregate.HasTechnicalDefault != true) continue;

            result.Add(new IssuerDefaultContext
            {
                EmitentId = emitentId,
                InstrumentId = holding.InstrumentId,
                Subject = holding.Issuer ?? titleByEmitent[emitentId],
                HasDefault = aggregate.HasDefault == true,
                HasTechnicalDefault = aggregate.HasTechnicalDefault == true,
            });
        }

        return result;
    }
}

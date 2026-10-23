using Bonds.Core.Analytics;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Bonds.Core.Policy;
using Bonds.Core.Time;
using Bonds.Core.Universe;
using Bonds.Infrastructure.Sync;
using Bonds.Infrastructure.Universe;
using Microsoft.Extensions.Options;

namespace Bonds.Infrastructure.Policy;

/// <summary>Облигация портфеля: ISIN и рыночная стоимость в рублях (для лимита эмитента).</summary>
public sealed record HeldBondValue(string Isin, decimal MarketValueRub);

/// <summary>
/// Кандидаты-облигации для P2/P3b/P4 — тот же отбор, что источник «recommended» (<c>GET /api/analytics/allocation</c>): гигиеническая фикс-купонная вселенная
/// банка (флоатеры исключены), светофор скоркарты ТОЛЬКО Green (у «recommended» достаточно «не Red»), ликвидность не Caution, ранг — доходность к худшему из
/// погашения/оферты после налога (<see cref="CandidateYieldRanking"/>). Существующий путь <c>AllocationEndpoints</c> не менялся: здесь отдельный сборщик
/// входа поверх тех же сервисов. Метаданные и стакан запрашиваются только у верхушки пула (<see cref="PolicyOptions.BondCandidatePool"/>).
/// <para>
/// <b>Единицы:</b> доходности — ДОЛИ, цены банка — рубли за штуку (<c>PricePercent/100 × FaceValue</c>), НКД — рубли, стакан — пункты. Только чтение рыночных данных.
/// </para>
/// </summary>
public sealed class PolicyBondCandidateProvider
{
    private readonly IBondUniverseRepository _universe;
    private readonly RelativeValueSnapshotBuilder _snapshots;
    private readonly ReliabilityScoreProvider _reliability;
    private readonly IOptions<BondUniverseRefreshOptions> _universeOptions;
    private readonly IOptions<PolicyOptions> _policyOptions;
    private readonly TradingMetaRefresher _meta;
    private readonly IPlanQuoteSource _quotes;

    public PolicyBondCandidateProvider(
        IBondUniverseRepository universe,
        RelativeValueSnapshotBuilder snapshots,
        ReliabilityScoreProvider reliability,
        IOptions<BondUniverseRefreshOptions> universeOptions,
        IOptions<PolicyOptions> policyOptions,
        TradingMetaRefresher meta,
        IPlanQuoteSource quotes)
    {
        _universe = universe;
        _snapshots = snapshots;
        _reliability = reliability;
        _universeOptions = universeOptions;
        _policyOptions = policyOptions;
        _meta = meta;
        _quotes = quotes;
    }

    public async Task<IReadOnlyList<PolicyBondCandidate>> GetAsync(
        ulong accountId, IReadOnlyList<HeldBondValue> held, DateTime nowUtc, CancellationToken ct = default)
    {
        var options = _policyOptions.Value;
        var asOf = BusinessClock.MoscowDate(nowUtc);
        var all = await _universe.GetAllAsync(ct);

        var eligible = all
            .Where(e => UniverseHygieneFilter.Evaluate(e, _universeOptions.Value.Hygiene, asOf) == HygieneHiddenReason.None)
            .Where(e => e.IsFloater != true)
            .Where(e => e.YieldFraction is not null && e.DurationYears is not null && e.PricePercent is not null)
            .Where(e => e.FaceValue is > 0m && !string.IsNullOrWhiteSpace(e.Isin))
            .ToList();

        var snapshot = await _snapshots.GetSnapshotAsync(ct);
        var book = await _reliability.GetBookAsync(ct);

        var passed = new List<BondUniverseEntry>();
        foreach (var entry in eligible)
        {
            if (book.Get(entry.Secid).Level != ReliabilityLight.Green) continue;

            var liquidity = LiquidityScoreCalculator.Assess(snapshot.LiquidityTurnoverRub(entry), entry.BidPercent, entry.OfferPercent, entry.NumTrades);
            var signals = CandidateRiskSignalService.Assess(
                liquidity.Score, entry.ListLevel, null, null, null, liquidity.BookSpreadFraction, bookMissing: liquidity.BookSpreadFraction is null);
            if (signals.Liquidity == SignalLevel.Caution) continue;

            passed.Add(entry);
        }

        var pool = CandidateYieldRanking.TopByRank(passed, Math.Max(1, options.BondCandidatePool));
        if (pool.Count == 0) return [];

        var meta = await _meta.EnsureTInvestAsync(pool.Select(e => e.Isin!).ToList(), nowUtc, options.MaxMetaQueriesPerRun, ct);
        var figis = pool
            .Select(e => meta.GetValueOrDefault(e.Isin!)?.Figi)
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => f!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var books = await _quotes.GetTInvestAsync(figis, ct);

        // Эмитент: удерживаемая стоимость по эмитентам (для лимита концентрации), эмитент бумаги банка — id либо ИНН.
        var byIsin = all.Where(e => !string.IsNullOrWhiteSpace(e.Isin)).GroupBy(e => e.Isin!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var heldByIssuer = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var h in held)
        {
            if (!byIsin.TryGetValue(h.Isin, out var entry) || IssuerKey(entry) is not { } key) continue;
            heldByIssuer[key] = heldByIssuer.GetValueOrDefault(key) + h.MarketValueRub;
        }

        var result = new List<PolicyBondCandidate>();
        foreach (var entry in pool)
        {
            var rank = CandidateYieldRanking.Evaluate(entry);
            var cleanPrice = entry.PricePercent!.Value / 100m * entry.FaceValue!.Value;
            var accrued = entry.AccruedRub ?? 0m;
            var issuerKey = IssuerKey(entry);

            PolicyQuote? quote = null;
            if (meta.TryGetValue(entry.Isin!, out var m) && !string.IsNullOrWhiteSpace(m.Figi) && books.TryGetValue(m.Figi, out var bq)
                && m.MinPriceIncrementPoints is > 0m)
            {
                quote = new PolicyQuote
                {
                    Broker = Brokers.TInvest,
                    AccountId = accountId,
                    InstrumentKey = m.InstrumentKey,
                    Ticker = m.Ticker,
                    Name = entry.ShortName ?? entry.SecName ?? entry.Secid,
                    Kind = PolicyAssetKind.Bond,
                    Market = TradingHours.MarketOf(Brokers.TInvest, m.Ticker, m.ClassCode),
                    Currency = "RUB",
                    Lot = m.Lot,
                    PriceStepPoints = m.MinPriceIncrementPoints,
                    NominalRub = NominalRubOf(m),
                    Bid = bq.Bid,
                    Ask = bq.Ask,
                    AccruedPerPiece = accrued,
                    ReferenceCleanPricePerPiece = cleanPrice,
                    BuyAvailable = m.BuyAvailable && m.ApiTradeAvailable,
                    SellAvailable = m.SellAvailable && m.ApiTradeAvailable,
                };
            }

            result.Add(new PolicyBondCandidate
            {
                Secid = entry.Secid,
                Isin = entry.Isin,
                Name = entry.ShortName ?? entry.SecName ?? entry.Secid,
                Sector = entry.Sector,
                YieldFraction = entry.YieldFraction,
                AfterTaxYieldFraction = rank?.AfterTaxYieldFraction,
                CleanPriceRub = cleanPrice,
                AccruedRub = accrued,
                IssuerKey = issuerKey,
                IssuerHeldValueRub = issuerKey is not null ? heldByIssuer.GetValueOrDefault(issuerKey) : 0m,
                Quote = quote,
            });
        }

        return result;
    }

    /// <summary>Эмитент для лимита концентрации: id эмитента MOEX, иначе ИНН, иначе нет (лимит не применяется).</summary>
    private static string? IssuerKey(BondUniverseEntry entry) =>
        entry.EmitentId is { } id ? $"id:{id}" : !string.IsNullOrWhiteSpace(entry.EmitentInn) ? $"inn:{entry.EmitentInn}" : null;

    /// <summary>
    /// Текущий номинал облигации в РУБЛЯХ из метаданных: рублёвый шаг (рубли за штуку) ÷ шаг в пунктах × 100 (обратное к <see cref="Bonds.Core.Trading.BondPriceStep.PointsToRub"/>).
    /// Нет рублёвого шага (номинал не в RUB) — null.
    /// </summary>
    public static decimal? NominalRubOf(InstrumentTradingMeta m) =>
        m.MinPriceIncrement is > 0m && m.MinPriceIncrementPoints is > 0m ? m.MinPriceIncrement.Value * 100m / m.MinPriceIncrementPoints.Value : null;
}

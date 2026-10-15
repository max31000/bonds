using Bonds.Api.Auth;
using Bonds.Core.Analytics;
using Bonds.Core.Interfaces;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Bonds.Core.Signals;
using Bonds.Core.Time;
using Bonds.Core.Universe;
using Bonds.Infrastructure.Analytics;
using Bonds.Infrastructure.Universe;
using Microsoft.Extensions.Options;
using static Bonds.Core.Analytics.RelativeValueService;

namespace Bonds.Api.Endpoints;

/// <summary>
/// Задача 49: GET /api/analytics/position-verdicts — вердикт по КАЖДОЙ открытой позиции разом
/// (<see cref="PositionVerdictService"/>). Все входы собираются заранее и батчево, без N+1: holdings счёта, скоркарта и снимок
/// банка (кэши), активные события, цели концентрации, настройки, ставка комиссии — по одному запросу; лучшая замена — топ-3
/// RV-кандидата (<c>mode=rv</c>, дешёвые соседи корзины позиции) из снимка банка в памяти, прогнанные через
/// <see cref="SwitchAnalysisService"/> v2 (компаундирование, НДФЛ, слиппедж, налог продажи).
/// </summary>
public static class PositionVerdictEndpoints
{
    /// <summary>Сколько RV-кандидатов прогонять через анализ замены для каждой позиции (как лимит mode=rv в плане).</summary>
    public const int ReplacementCandidatesPerPosition = 3;

    public static void MapPositionVerdictEndpoints(this WebApplication app)
    {
        app.MapGet("/api/analytics/position-verdicts", GetPositionVerdicts);
    }

    private static async Task<IResult> GetPositionVerdicts(
        ICurrentUser currentUser,
        IAccountRepository accountRepo,
        PortfolioHoldingsBuilder holdingsBuilder,
        RelativeValueSnapshotBuilder snapshotBuilder,
        ReliabilityScoreProvider scoreProvider,
        ISignalRepository signalRepo,
        ITargetAllocationRepository targetAllocationRepo,
        IUserSettingsRepository settingsRepo,
        ICommissionRateProvider commissionRateProvider,
        IOptions<SignalEngineOptions> signalOptions,
        CancellationToken ct)
    {
        var accountId = await PositionsEndpoints.ResolveAccountIdAsync(currentUser, accountRepo);
        if (accountId is null)
        {
            return Results.Ok(new PositionVerdictsResponseDto { Verdicts = [], Disclaimer = PositionVerdictService.Disclaimer });
        }

        var asOf = BusinessClock.MoscowToday();
        var holdings = (await holdingsBuilder.BuildForAccountAsync(accountId.Value, asOf, ct))
            .Where(h => h.Quantity > 0m)
            .ToList();
        if (holdings.Count == 0)
        {
            return Results.Ok(new PositionVerdictsResponseDto { Verdicts = [], Disclaimer = PositionVerdictService.Disclaimer });
        }

        var snapshot = await snapshotBuilder.GetSnapshotAsync(ct);
        var book = await scoreProvider.GetBookAsync(ct);

        var userId = await currentUser.GetUserIdAsync();
        var settings = userId is { } uid ? await settingsRepo.GetByUserIdAsync(uid) : null;
        var horizonYears = settings?.ReplacementHorizonYears ?? UserSettings.DefaultReplacementHorizonYears;
        var options = signalOptions.Value.WithOverrides(
            settings?.UpcomingEventDaysThreshold, settings?.UninvestedCashThresholdRub, settings?.DefaultMaxConcentrationPercent);

        var targetAllocations = (await targetAllocationRepo.GetByAccountIdAsync(accountId.Value)).ToList();
        var eventTypesByInstrument = (await signalRepo.GetByAccountIdAsync(accountId.Value))
            .Where(s => s.InstrumentId is not null)
            .GroupBy(s => s.InstrumentId!.Value)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<SignalType>)g.Select(s => s.Type).Distinct().ToList());

        var commission = await commissionRateProvider.GetAsync(accountId.Value, ct);

        // Концентрация: стоимость портфеля и стоимость каждого эмитента (ключ — как в PortfolioCompositionService.ByIssuer).
        var composition = PortfolioCompositionService.Calculate(holdings);
        var issuerValueRub = composition.ByIssuer.ToDictionary(s => s.Key, s => s.MarketValueRub, StringComparer.OrdinalIgnoreCase);

        var verdicts = new List<PositionVerdictDto>(holdings.Count);
        foreach (var holding in holdings)
        {
            ct.ThrowIfCancellationRequested();

            var score = holding.Isin is { } isin && book.TryGetByIsin(isin, out var found) ? found : ReliabilityScoreService.Unknown();
            var comparable = YieldComparability.IsComparable(holding.IsFloater, holding.IsIndexed, holding.DataIncomplete);
            var value = AssessPositionValue(holding, comparable, snapshot);

            eventTypesByInstrument.TryGetValue(holding.InstrumentId, out var activeEvents);
            activeEvents ??= [];

            // Замену считаем только там, где она может повлиять на вердикт: не выход (Red/критический флаг/дефолт),
            // есть данные скоркарты, доходности сравнимы.
            var needsReplacement = comparable && score.Coverage > 0m && score.Level != ReliabilityLight.Red
                && score.CriticalFlags.Count == 0 && !activeEvents.Contains(SignalType.IssuerDefaultEvent);
            var best = needsReplacement
                ? FindBestReplacement(holding, score, snapshot, book, horizonYears, commission.Rate, asOf)
                : null;

            var issuerKey = holding.Issuer ?? PortfolioCompositionService.UnknownIssuerLabel;
            issuerValueRub.TryGetValue(issuerKey, out var issuerValue);
            var limitPercent = targetAllocations
                .FirstOrDefault(ta => ta.Issuer is not null && string.Equals(ta.Issuer, issuerKey, StringComparison.OrdinalIgnoreCase))
                ?.MaxConcentrationPercent ?? options.DefaultMaxConcentrationPercent;

            var verdict = PositionVerdictService.Evaluate(new PositionVerdictInput
            {
                Reliability = score,
                Value = value,
                YieldComparable = comparable,
                ActiveEventTypes = activeEvents,
                BestReplacement = best?.Option,
                // Проценты с обеих сторон (SharePercent-контракт): лимит — проценты, доля после докупки — проценты.
                IssuerSharePercentAfterBuy = PositionVerdictService.ConcentrationAfterBuyPercent(issuerValue, composition.TotalMarketValueRub),
                ConcentrationLimitPercent = limitPercent,
            });

            verdicts.Add(new PositionVerdictDto
            {
                PositionId = holding.PositionId,
                InstrumentId = holding.InstrumentId,
                Verdict = verdict.Verdict.ToString(),
                Reasons = verdict.Reasons,
                Confidence = verdict.Confidence.ToString(),
                // Замена показывается только когда именно она вынесла вердикт — иначе UI ввёл бы в заблуждение.
                BestReplacement = verdict.Verdict == PositionVerdictKind.ConsiderReplace && best is not null ? ToDto(best) : null,
            });
        }

        return Results.Ok(new PositionVerdictsResponseDto { Verdicts = verdicts, Disclaimer = PositionVerdictService.Disclaimer });
    }

    /// <summary>
    /// Ось Value позиции: G-спред ДВИЖКА против медианы корзины (сектор × Маколей), как на странице RV
    /// (<c>GET /api/analytics/relative-value</c>). Несравнимые позиции и позиции без спреда/дюрации — InsufficientData.
    /// </summary>
    private static RelativeValueVerdict AssessPositionValue(
        PortfolioHolding holding, bool comparable, RelativeValueSnapshotBuilder.RelativeValueSnapshot snapshot)
    {
        if (!comparable || holding.GSpread is not { } spread || holding.MacaulayDuration is null)
        {
            return RelativeValueVerdict.InsufficientData;
        }

        return Assess(holding.Sector, holding.MacaulayDuration, spread, snapshot.AllMembers, snapshot.BasketStats).Verdict;
    }

    private sealed record BestReplacement(ReplacementOption Option, string? Isin);

    /// <summary>
    /// Топ-3 RV-кандидата корзины позиции (<c>ReplacementEndpoints.ResolveCheapBasketMembers</c>, как mode=rv) →
    /// <see cref="SwitchAnalysisService.Compare"/> v2 → лучшая по годовой выгоде с учётом светофора цели
    /// (<see cref="PositionVerdictService.SelectBestReplacement"/>). Нога цели собирается из БАНК-записи (доходность MOEX YIELD, Маколей,
    /// полуспред стакана, оферта/погашение), потому что кандидат — бумага банка без позиции: точный движок для неё не гоняется.
    /// Единицы: доходность — ДОЛЯ, дюрация — годы, горизонт — годы; ставки комиссии — ДОЛИ.
    /// </summary>
    private static BestReplacement? FindBestReplacement(
        PortfolioHolding holding, ReliabilityScore holdScore, RelativeValueSnapshotBuilder.RelativeValueSnapshot snapshot,
        ReliabilityBook book, decimal horizonYears, decimal commissionRate, DateOnly asOf)
    {
        if (holding.GSpread is not { } spread || holding.MacaulayDuration is null) return null;

        var assessment = Assess(holding.Sector, holding.MacaulayDuration, spread, snapshot.AllMembers, snapshot.BasketStats);
        var members = ReplacementEndpoints.ResolveCheapBasketMembers(
            assessment.Basket.EffectiveBasket, assessment.Basket.Stats.Median, snapshot, holding.Isin, ReplacementCandidatesPerPosition);
        if (members.Count == 0) return null;

        var hold = ReplacementEndpoints.BuildHoldSwitchCandidate(holding, snapshot);
        var options = new List<(ReplacementOption Option, string? Isin)>(members.Count);
        foreach (var member in members)
        {
            if (!snapshot.CurrentEntriesBySecid.TryGetValue(member.Secid, out var entry) || entry.YieldFraction is null) continue;

            var horizonDate = entry.OfferDate is { } offer && offer > asOf ? offer : entry.MaturityDate;
            var target = new SwitchCandidate
            {
                PositionId = 0,
                MarketValueRub = 0m, // в расчёте цели не используется: капитал идёт от hold
                EffectiveYield = entry.YieldFraction,
                MacaulayDurationYears = entry.DurationYears,
                HalfSpreadFraction = LiquidityScoreCalculator.HalfSpreadFraction(entry.BidPercent, entry.OfferPercent),
                TargetHorizonDate = horizonDate,
                TargetHorizonIsOffer = entry.OfferDate is { } o && o > asOf,
            };

            var result = SwitchAnalysisService.Compare(hold, target, horizonYears, commissionRate, commissionRate, asOf);
            options.Add((new ReplacementOption
            {
                TargetName = entry.ShortName ?? entry.SecName ?? entry.Secid,
                TargetSecid = entry.Secid,
                TargetLight = book.Get(entry.Secid).Level,
                AnnualizedBenefitFraction = result.AnnualizedBenefitFraction,
                NetBenefitAfterTaxRub = result.NetBenefitAfterTaxRub,
                EffectiveHorizonYears = result.EffectiveHorizonYears,
            }, entry.Isin));
        }

        var best = PositionVerdictService.SelectBestReplacement(holdScore.Level, options.Select(o => o.Option));
        return best is null ? null : new BestReplacement(best, options.First(o => ReferenceEquals(o.Option, best)).Isin);
    }

    private static BestReplacementDto ToDto(BestReplacement best) => new()
    {
        Name = best.Option.TargetName,
        Secid = best.Option.TargetSecid,
        Isin = best.Isin,
        TargetReliability = best.Option.TargetLight.ToString(),
        AnnualizedBenefitFraction = best.Option.AnnualizedBenefitFraction,
        NetBenefitAfterTaxRub = best.Option.NetBenefitAfterTaxRub,
        EffectiveHorizonYears = best.Option.EffectiveHorizonYears,
    };
}

public sealed record PositionVerdictsResponseDto
{
    public required IReadOnlyList<PositionVerdictDto> Verdicts { get; init; }
    public required string Disclaimer { get; init; }
}

public sealed record PositionVerdictDto
{
    public required ulong PositionId { get; init; }
    public required ulong InstrumentId { get; init; }

    /// <summary>"Hold" | "BuyMoreCandidate" | "ConsiderReplace" | "ConsiderExit" (<see cref="PositionVerdictKind"/>, имя enum).</summary>
    public required string Verdict { get; init; }

    /// <summary>До 3 причин словами, главная — первой.</summary>
    public required IReadOnlyList<string> Reasons { get; init; }

    /// <summary>"Low" | "Medium" | "High" (<see cref="VerdictConfidence"/>) — по Coverage скоркарты.</summary>
    public required string Confidence { get; init; }

    /// <summary>Лучшая замена — только при вердикте ConsiderReplace.</summary>
    public BestReplacementDto? BestReplacement { get; init; }
}

public sealed record BestReplacementDto
{
    public required string Name { get; init; }
    public string? Secid { get; init; }
    public string? Isin { get; init; }

    /// <summary>Светофор цели: "Green" | "Yellow" | "Red".</summary>
    public required string TargetReliability { get; init; }

    /// <summary>Годовая чистая выгода, ДОЛЯ (0.008 = 80 б.п. годовых) после НДФЛ, комиссий, слиппеджа и налога продажи.</summary>
    public decimal? AnnualizedBenefitFraction { get; init; }

    /// <summary>Чистая выгода за эффективный горизонт, РУБЛИ; null — налог продажи оценить нельзя.</summary>
    public decimal? NetBenefitAfterTaxRub { get; init; }

    /// <summary>Эффективный горизонт сравнения, ГОДЫ.</summary>
    public required decimal EffectiveHorizonYears { get; init; }
}

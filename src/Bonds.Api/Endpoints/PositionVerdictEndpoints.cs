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
        IUserSettingsRepository settingsRepo,
        ICommissionRateProvider commissionRateProvider,
        ISecurityDetailsRepository detailsRepo,
        IOptions<SignalEngineOptions> signalOptions,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Bonds.Api.Endpoints.PositionVerdictEndpoints");
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

        var activeSignals = (await signalRepo.GetByAccountIdAsync(accountId.Value))
            .Where(s => s.InstrumentId is not null)
            .ToList();
        var eventTypesByInstrument = activeSignals
            .GroupBy(s => s.InstrumentId!.Value)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<SignalType>)g.Select(s => s.Type).Distinct().ToList());

        // Эмитенты с активным дефолт-событием. Событие IssuerDefaultEvent рождается ОДНО на эмитента и привязано к его первой
        // позиции, а дефолт относится ко ВСЕМ выпускам эмитента — раскрываем на все позиции эмитента по emitent_id.
        var bankByIsin = BuildBankIndexByIsin(snapshot);
        var cardEmitentBySecid = await LoadCardEmitentsAsync(holdings, bankByIsin, detailsRepo, ct);
        var holdingByInstrument = holdings.GroupBy(h => h.InstrumentId).ToDictionary(g => g.Key, g => g.First());
        var defaultEmitentIds = activeSignals
            .Where(s => s.Type == SignalType.IssuerDefaultEvent && holdingByInstrument.ContainsKey(s.InstrumentId!.Value))
            .Select(s => ResolveEmitentId(holdingByInstrument[s.InstrumentId!.Value], bankByIsin, cardEmitentBySecid))
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .ToHashSet();

        var commission = await commissionRateProvider.GetAsync(accountId.Value, ct);

        // Концентрация: стоимость портфеля и стоимость каждого эмитента (ключ — как в PortfolioCompositionService.ByIssuer).
        var composition = PortfolioCompositionService.Calculate(holdings);
        // Регистронезависимо: PortfolioCompositionService группирует эмитентов регистрозависимо, а лимиты/поиск ниже — нет,
        // поэтому «Эмитент» и «эмитент» схлопываем с суммированием (ToDictionary падал бы на дубликате ключа → 500 на весь запрос).
        var issuerValueRub = composition.ByIssuer
            .GroupBy(s => s.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Sum(s => s.MarketValueRub), StringComparer.OrdinalIgnoreCase);

        var verdicts = new List<PositionVerdictDto>(holdings.Count);
        foreach (var holding in holdings)
        {
            ct.ThrowIfCancellationRequested();

            // Сбой расчёта ОДНОЙ позиции не должен ронять вердикты всего портфеля: она получает Hold «не удалось оценить»,
            // остальные считаются как обычно. Отмену запроса не глотаем.
            try
            {
                verdicts.Add(EvaluatePosition(
                    holding, snapshot, book, bankByIsin, horizonYears, commission.Rate, asOf, options.DefaultMaxConcentrationPercent,
                    eventTypesByInstrument, defaultEmitentIds, cardEmitentBySecid, issuerValueRub, composition.TotalMarketValueRub));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Не удалось посчитать вердикт позиции {PositionId}; отдан Hold «не удалось оценить»", holding.PositionId);
                verdicts.Add(new PositionVerdictDto
                {
                    PositionId = holding.PositionId,
                    InstrumentId = holding.InstrumentId,
                    Verdict = PositionVerdictKind.Hold.ToString(),
                    Reasons = [UnableToEvaluateReason],
                    Confidence = VerdictConfidence.Low.ToString(),
                    BestReplacement = null,
                });
            }
        }

        return Results.Ok(new PositionVerdictsResponseDto { Verdicts = verdicts, Disclaimer = PositionVerdictService.Disclaimer });
    }

    /// <summary>Причина вердикта при сбое расчёта одной позиции (Hold, Confidence Low).</summary>
    public const string UnableToEvaluateReason = "Не удалось оценить позицию — попробуйте обновить данные позже";

    /// <summary>Вердикт одной позиции из заранее собранных батчевых входов (без I/O).</summary>
    private static PositionVerdictDto EvaluatePosition(
        PortfolioHolding holding,
        RelativeValueSnapshotBuilder.RelativeValueSnapshot snapshot,
        ReliabilityBook book,
        IReadOnlyDictionary<string, BondUniverseEntry> bankByIsin,
        decimal horizonYears,
        decimal commissionRate,
        DateOnly asOf,
        decimal defaultConcentrationLimitPercent,
        IReadOnlyDictionary<ulong, IReadOnlyList<SignalType>> eventTypesByInstrument,
        IReadOnlySet<int> defaultEmitentIds,
        IReadOnlyDictionary<string, int> cardEmitentBySecid,
        IReadOnlyDictionary<string, decimal> issuerValueRub,
        decimal portfolioValueRub)
    {
        var score = holding.Isin is { } isin && book.TryGetByIsin(isin, out var found) ? found : ReliabilityScoreService.Unknown();
        var comparable = YieldComparability.IsComparable(holding.IsFloater, holding.IsIndexed, holding.DataIncomplete);
        var value = AssessPositionValue(holding, comparable, snapshot);

        eventTypesByInstrument.TryGetValue(holding.InstrumentId, out var activeEvents);
        activeEvents ??= [];

        // Дефолт эмитента — флаг уровня ЭМИТЕНТА: действует на все его позиции, а не только на ту, к которой привязано событие.
        var hasIssuerDefaultEvent = activeEvents.Contains(SignalType.IssuerDefaultEvent)
            || (ResolveEmitentId(holding, bankByIsin, cardEmitentBySecid) is { } emitentId && defaultEmitentIds.Contains(emitentId));

        // Замену считаем только там, где она может повлиять на вердикт: не выход (Red/критический флаг/дефолт),
        // есть данные скоркарты, доходности сравнимы.
        var needsReplacement = comparable && score.Coverage > 0m && score.Level != ReliabilityLight.Red
            && score.CriticalFlags.Count == 0 && !hasIssuerDefaultEvent;
        var best = needsReplacement
            ? FindBestReplacement(holding, score, snapshot, book, bankByIsin, horizonYears, commissionRate, asOf)
            : null;

        var issuerKey = holding.Issuer ?? PortfolioCompositionService.UnknownIssuerLabel;
        issuerValueRub.TryGetValue(issuerKey, out var issuerValue);
        var limitPercent = defaultConcentrationLimitPercent;

        var verdict = PositionVerdictService.Evaluate(new PositionVerdictInput
        {
            Reliability = score,
            Value = value,
            YieldComparable = comparable,
            ActiveEventTypes = activeEvents,
            HasIssuerDefaultEvent = hasIssuerDefaultEvent,
            BestReplacement = best?.Option,
            // Проценты с обеих сторон (SharePercent-контракт): лимит — проценты, доля после докупки — проценты.
            IssuerSharePercentAfterBuy = PositionVerdictService.ConcentrationAfterBuyPercent(issuerValue, portfolioValueRub),
            ConcentrationLimitPercent = limitPercent,
        });

        return new PositionVerdictDto
        {
            PositionId = holding.PositionId,
            InstrumentId = holding.InstrumentId,
            Verdict = verdict.Verdict.ToString(),
            Reasons = verdict.Reasons,
            Confidence = verdict.Confidence.ToString(),
            // Замена показывается только когда именно она вынесла вердикт — иначе UI ввёл бы в заблуждение.
            BestReplacement = verdict.Verdict == PositionVerdictKind.ConsiderReplace && best is not null ? ToDto(best) : null,
        };
    }

    /// <summary>Индекс текущих банк-записей по ISIN (регистронезависимо) — одним проходом по снимку вместо поиска на позицию.</summary>
    private static IReadOnlyDictionary<string, BondUniverseEntry> BuildBankIndexByIsin(RelativeValueSnapshotBuilder.RelativeValueSnapshot snapshot) =>
        snapshot.CurrentEntriesBySecid.Values
            .Where(e => !string.IsNullOrWhiteSpace(e.Isin))
            .GroupBy(e => e.Isin!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// EMITTER_ID из карточек бумаг (<c>security_details</c>) для позиций, у которых банк-запись не знает эмитента (fallback —
    /// как в <c>PositionMarketContextBuilder</c>, чтобы раскрытие дефолта по эмитенту совпадало с тем, как событие строится). Один запрос.
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, int>> LoadCardEmitentsAsync(
        IReadOnlyList<PortfolioHolding> holdings, IReadOnlyDictionary<string, BondUniverseEntry> bankByIsin,
        ISecurityDetailsRepository detailsRepo, CancellationToken ct)
    {
        var secids = holdings
            .Select(h => h.Isin is { } isin && bankByIsin.TryGetValue(isin, out var entry) && entry.EmitentId is null ? entry.Secid : null)
            .Where(secid => secid is not null)
            .Select(secid => secid!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (secids.Count == 0) return new Dictionary<string, int>();

        return (await detailsRepo.GetBySecidsAsync(secids, ct))
            .Where(d => d.EmitentId is not null)
            .GroupBy(d => d.Secid, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().EmitentId!.Value, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>emitent_id позиции: банк-запись по ISIN, а при её отсутствии у записи эмитента — карточка бумаги. Null — неизвестен.</summary>
    private static int? ResolveEmitentId(
        PortfolioHolding holding, IReadOnlyDictionary<string, BondUniverseEntry> bankByIsin, IReadOnlyDictionary<string, int> cardEmitentBySecid)
    {
        if (holding.Isin is not { } isin || !bankByIsin.TryGetValue(isin, out var entry)) return null;
        if (entry.EmitentId is { } fromBank) return fromBank;
        return cardEmitentBySecid.TryGetValue(entry.Secid, out var fromCard) ? fromCard : null;
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
    /// <para>
    /// <b>Одна методика в обеих ногах:</b> доходность ноги hold — <c>YieldFraction</c> БАНК-записи позиции (MOEX YIELD), когда бумага есть
    /// в <c>bond_universe</c>, — той же природы, что у ног-целей. Доходность движка (<c>YtmEffective</c>, расчёт по нашей модели)
    /// используется только как fallback, если позиции нет в банке или у записи нет YIELD; иначе разница методик маскировалась бы под
    /// «выгоду замены». ВНИМАНИЕ: POST /api/analytics/replacement по-прежнему берёт обе ноги из движка (там цель — позиция/watchlist
    /// с расчётом движка), поэтому цифры вердикта и ручного анализа могут различаться.
    /// </para>
    /// </summary>
    private static BestReplacement? FindBestReplacement(
        PortfolioHolding holding, ReliabilityScore holdScore, RelativeValueSnapshotBuilder.RelativeValueSnapshot snapshot,
        ReliabilityBook book, IReadOnlyDictionary<string, BondUniverseEntry> bankByIsin, decimal horizonYears, decimal commissionRate, DateOnly asOf)
    {
        if (holding.GSpread is not { } spread || holding.MacaulayDuration is null) return null;

        var assessment = Assess(holding.Sector, holding.MacaulayDuration, spread, snapshot.AllMembers, snapshot.BasketStats);
        var members = ReplacementEndpoints.ResolveCheapBasketMembers(
            assessment.Basket.EffectiveBasket, assessment.Basket.Stats.Median, snapshot, holding.Isin, ReplacementCandidatesPerPosition);
        if (members.Count == 0) return null;

        var hold = ReplacementEndpoints.BuildHoldSwitchCandidate(holding, snapshot);
        if (holding.Isin is { } holdIsin && bankByIsin.TryGetValue(holdIsin, out var holdBankEntry) && holdBankEntry.YieldFraction is { } holdBankYield)
        {
            hold = hold with { EffectiveYield = holdBankYield }; // ДОЛЯ, MOEX YIELD банка (см. doc-comment метода)
        }

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

using Bonds.Api.Auth;
using Bonds.Api.Middleware;
using Bonds.Core.Analytics;
using Bonds.Core.Calculation;
using Bonds.Core.CashFlow;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Interfaces;
using Bonds.Core.Models;
using Bonds.Core.Time;
using Bonds.Core.Universe;
using Bonds.Infrastructure.Analytics;
using Bonds.Infrastructure.Universe;
using Microsoft.Extensions.Options;
using static Bonds.Api.Endpoints.AnalyticsSupport;
using static Bonds.Core.Analytics.RelativeValueService;

namespace Bonds.Api.Endpoints;

/// <summary>«Куда вложить сумму»: аллокация (portfolio/market/recommended) и расчёт корзины.</summary>
public static class AllocationEndpoints
{
    public static void MapAllocationEndpoints(this WebApplication app)
    {
        app.MapGet("/api/analytics/allocation", GetAllocation);
        app.MapPost("/api/analytics/basket", PostBasket);
    }

    // ─── GET /api/analytics/allocation ──────────────────────────────────────────────────────

    /// <summary>
    /// «Куда вложить сумму» (задача 34 — источники кандидатов). Жадное распределение
    /// <paramref name="amountRub"/> через <see cref="CashAllocationService"/>. <paramref name="source"/>
    /// выбирает кандидатов: <c>portfolio</c> (дефолт, обратная совместимость — прежнее поведение)
    /// строит кандидатов из <see cref="PortfolioHoldingsBuilder"/> holdings — тот же вход, что у
    /// comparison/replacement, лот берётся равным 1 (в модели <c>Instrument</c> нет поля размера
    /// лота — см. doc-comment плана: при отсутствии источника лота считаем 1 и помечаем
    /// <c>lotSizeAssumed</c>, а не падаем/выдумываем данные); <c>market</c>/<c>recommended</c>
    /// строят кандидатов из ВСЕЙ фикс-купонной вселенной банка <c>bond_universe</c> —
    /// см. <see cref="BuildMarketAllocationResponseAsync"/>. <paramref name="includeWatchlist"/>
    /// осмыслен только для <c>portfolio</c> (для market/recommended игнорируется — вся вселенная и
    /// так шире watchlist).
    /// <para>
    /// <b>Задача 20:</b> <paramref name="includeWatchlist"/>=true добавляет к кандидатам watchlist-
    /// бумаги (<see cref="PortfolioHoldingsBuilder.BuildForInstrumentsAsync"/> — тот же расчётный
    /// путь, holdings с Quantity=1/MarketValueRub=цена одной бумаги). Их
    /// <c>CurrentIssuerMarketValueRub</c> берётся из уже существующих позиций того же эмитента (0,
    /// если эмитента ещё нет в портфеле) — лимит концентрации считается от факта владения, не от
    /// присутствия в watchlist. Дефолт false — не ломает существующий вызов без параметра.
    /// </para>
    /// </summary>
    private static async Task<IResult> GetAllocation(
        ICurrentUser currentUser,
        IAccountRepository accountRepo,
        PortfolioHoldingsBuilder holdingsBuilder,
        IWatchlistItemRepository watchlistRepo,
        IInstrumentRepository instrumentRepo,
        ICommissionRateProvider commissionRateProvider,
        IBondUniverseRepository universeRepo,
        RelativeValueSnapshotBuilder snapshotBuilder,
        IOptions<Bonds.Infrastructure.Universe.BondUniverseRefreshOptions> universeOptions,
        decimal amountRub,
        string source = "portfolio",
        bool includeWatchlist = false,
        CancellationToken ct = default)
    {
        if (amountRub <= 0m)
        {
            return Results.Json(
                new { error = "amountRub должен быть положительным", type = "ValidationException" },
                statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        if (source is not ("portfolio" or "market" or "recommended"))
        {
            return ValidationError("source должен быть 'portfolio', 'market' или 'recommended'");
        }

        var accountId = await PositionsEndpoints.ResolveAccountIdAsync(currentUser, accountRepo);

        // Ставка покупки для оценки грязной цены лота — через резолвер (override →
        // оценка из журнала → дефолт), не захардкоженная константа. Задача 34: market/recommended не
        // зависят от портфеля, но тоже платят комиссию за покупку — резолвим ставку, если есть
        // привязанный счёт, иначе дефолт (тот же фолбэк, что раньше был только у "нет аккаунта").
        var resolvedRate = accountId is not null
            ? await commissionRateProvider.GetAsync(accountId.Value, ct)
            : new ResolvedCommissionRate(SwitchAnalysisService.DefaultCommissionRate, CommissionRateSource.Default, null);
        var commissionRate = resolvedRate.Rate;

        if (source is "market" or "recommended")
        {
            return await BuildMarketAllocationResponseAsync(
                amountRub, source, commissionRate, resolvedRate, universeRepo, universeOptions.Value.Hygiene, snapshotBuilder, ct);
        }

        // ─── source=portfolio (прежнее поведение, обратная совместимость дефолта) ──────────────

        if (accountId is null)
        {
            return Results.Ok(new AllocationResponseDto
            {
                AmountRub = amountRub,
                Source = source,
                Allocations = [],
                Skipped = [],
                LeftoverRub = amountRub,
                Disclaimer = CashAllocationService.Disclaimer,
                CommissionRateUsed = resolvedRate.Rate,
                CommissionRateSource = resolvedRate.Source.ToString(),
            });
        }

        var asOf = BusinessClock.MoscowToday();
        var holdings = (await holdingsBuilder.BuildForAccountAsync(accountId.Value, asOf)).ToList();

        var currentPortfolioValueRub = holdings.Sum(h => h.MarketValueRub);
        var issuerMarketValue = holdings
            .GroupBy(h => h.Issuer ?? $"__instrument_{h.InstrumentId}")
            .ToDictionary(g => g.Key, g => g.Sum(h => h.MarketValueRub));

        var candidates = holdings
            .Where(h => !h.IsOutOfScopeCurrency && h.Quantity > 0m)
            .Select(h =>
            {
                var pricePerUnitRub = h.MarketValueRub / h.Quantity;
                // pricePerUnitRub — грязная цена (чистая + НКД на бумагу, см. doc-comment
                // PortfolioHoldingsBuilder); раскладываем на компоненты для UI, не для расчёта.
                var accruedRub = h.AccruedPerBondRub;
                var cleanPriceRub = pricePerUnitRub - accruedRub;
                var commissionRub = pricePerUnitRub * commissionRate;
                var pricePerLotWithCommission = pricePerUnitRub + commissionRub;
                var effectiveYield = (h.IsFloater || h.IsIndexed) ? h.CurrentYield : h.YtmEffective;
                var issuerKey = h.Issuer ?? $"__instrument_{h.InstrumentId}";

                return new CashAllocationCandidate
                {
                    InstrumentId = h.InstrumentId,
                    Name = h.Name,
                    Issuer = h.Issuer,
                    EffectiveYield = effectiveYield,
                    PricePerLotRub = pricePerLotWithCommission,
                    LotSize = 1m,
                    LotSizeIsAssumed = true,
                    CurrentIssuerMarketValueRub = issuerMarketValue.TryGetValue(issuerKey, out var v) ? v : 0m,
                    CleanPriceRub = cleanPriceRub,
                    AccruedRub = accruedRub,
                    CommissionRub = commissionRub,
                    // Флоатер/индексируемая исключаются из аллокации через skip
                    // (NotComparable), не ранжируются вместе с обычной бумагой по CurrentYield.
                    // Намеренно НЕ полный YieldComparability.IsComparable (тот же, что PostReplacement/
                    // GetRelativeValue используют) — DataIncomplete здесь сознательно не гейтится этим
                    // флагом: бумага с неполными данными и без цены уже естественно получает
                    // EffectiveYield=null и уходит в существующий скип NoYield (см. тест
                    // GetAllocation_SeededPositionWithoutYield_Returns200_WithSkippedReason); заводить
                    // для неё ещё и NotComparable — не предмет задачи 31 (только флоатер/индексируемая).
                    IsComparable = !h.IsFloater && !h.IsIndexed,
                };
            })
            // Одна позиция на инструмент — если счёт по ошибке содержит несколько записей на один
            // InstrumentId, берём первую (защита от дублей, не ожидается в норме).
            .DistinctBy(c => c.InstrumentId)
            .ToList();

        if (includeWatchlist)
        {
            var portfolioInstrumentIds = candidates.Select(c => c.InstrumentId).ToHashSet();
            var watchlistItems = await currentUser.GetUserIdAsync() is { } uid
                ? (await watchlistRepo.GetByUserIdAsync(uid)).ToList()
                : [];

            var watchlistInstrumentIds = new List<ulong>();
            foreach (var item in watchlistItems)
            {
                var instrument = await instrumentRepo.GetByIsinAsync(item.Isin);
                // Бумага уже есть в портфеле — не дублируем кандидата (позиция уже покрыта циклом выше).
                if (instrument is not null && !portfolioInstrumentIds.Contains(instrument.Id))
                {
                    watchlistInstrumentIds.Add(instrument.Id);
                }
            }

            var watchlistHoldings = await holdingsBuilder.BuildForInstrumentsAsync(watchlistInstrumentIds.Distinct().ToList(), asOf);

            var watchlistCandidates = watchlistHoldings
                .Where(h => !h.IsOutOfScopeCurrency)
                .Select(h =>
                {
                    // Quantity=1 у watchlist-holding (см. doc-comment BuildForInstrumentsAsync)
                    // — MarketValueRub уже цена одной бумаги (грязная).
                    var accruedRub = h.AccruedPerBondRub;
                    var cleanPriceRub = h.MarketValueRub - accruedRub;
                    var commissionRub = h.MarketValueRub * commissionRate;
                    var pricePerLotWithCommission = h.MarketValueRub + commissionRub;
                    var effectiveYield = (h.IsFloater || h.IsIndexed) ? h.CurrentYield : h.YtmEffective;
                    var issuerKey = h.Issuer ?? $"__instrument_{h.InstrumentId}";

                    return new CashAllocationCandidate
                    {
                        InstrumentId = h.InstrumentId,
                        Name = h.Name,
                        Issuer = h.Issuer,
                        EffectiveYield = effectiveYield,
                        PricePerLotRub = pricePerLotWithCommission,
                        LotSize = 1m,
                        LotSizeIsAssumed = true,
                        CurrentIssuerMarketValueRub = issuerMarketValue.TryGetValue(issuerKey, out var v) ? v : 0m,
                        CleanPriceRub = cleanPriceRub,
                        AccruedRub = accruedRub,
                        CommissionRub = commissionRub,
                        // См. комментарий в портфельной ветке выше — тот же предикат.
                        IsComparable = !h.IsFloater && !h.IsIndexed,
                    };
                })
                .ToList();

            candidates.AddRange(watchlistCandidates);
        }

        var result = CashAllocationService.Allocate(amountRub, candidates, currentPortfolioValueRub);

        var dto = new AllocationResponseDto
        {
            AmountRub = result.AmountRub,
            Source = source,
            Allocations = result.Allocations.Select(a => new AllocationLineDto
            {
                InstrumentId = a.InstrumentId,
                Name = a.Name,
                Issuer = a.Issuer,
                Quantity = a.Quantity,
                EstimatedCostRub = a.EstimatedCostRub,
                EffectiveYield = a.EffectiveYield,
                LotSizeAssumed = a.LotSizeAssumed,
                CleanCostRub = a.CleanCostRub,
                AccruedCostRub = a.AccruedCostRub,
                CommissionCostRub = a.CommissionCostRub,
                // source=portfolio может тоже нести риск-сигналы, если бумага
                // нашлась в банке по ISIN — план явно допускает более простую альтернативу (null),
                // если не находится/не резолвится; здесь сознательно null (см. NEEDS DECISION в
                // отчёте задачи — потребует resolve-по-ISIN на КАЖДУЮ строку портфеля, план это не
                // требует буквально).
                RiskSignals = null,
            }).ToList(),
            Skipped = result.Skipped.Select(s => new AllocationSkipDto
            {
                InstrumentId = s.InstrumentId,
                Name = s.Name,
                Issuer = s.Issuer,
                Reason = s.Reason.ToString(),
            }).ToList(),
            LeftoverRub = result.LeftoverRub,
            Disclaimer = result.Disclaimer,
            CommissionRateUsed = resolvedRate.Rate,
            CommissionRateSource = resolvedRate.Source.ToString(),
        };

        return Results.Ok(dto);
    }

    // ─── GET /api/analytics/allocation?source=market|recommended ───────────────────

    /// <summary>Верхняя граница числа кандидатов вселенной банка (после
    /// гигиенического + флоатер + [для recommended] риск-фильтра), реально участвующих в жадном
    /// проходе. Банк ~3400 строк — без потолка ответ разрастался бы; "не молча" — см.
    /// <see cref="AllocationResponseDto.CandidatePoolTruncated"/>/дисклеймер с фактическими числами.</summary>
    public const int MarketAllocationCandidatePoolLimit = 200;

    public const string MarketAllocationDisclaimer =
        "Оценка распределения свободных средств по всей фикс-купонной гигиенически-чистой вселенной " +
        "банка облигаций MOEX (флоатеры исключены — их доходность несравнима с фикс-купоном). " +
        "Кандидаты ранжируются по доходности убыв., без учёта вашего текущего портфеля. Оценки — по " +
        "биржевой статистике, не является индивидуальной инвестиционной рекомендацией.";

    public const string RecommendedAllocationDisclaimer =
        "Оценка распределения свободных средств по кандидатам вселенной банка облигаций MOEX, " +
        "отфильтрованным по позитивным риск-сигналам (ликвидность+листинг, спред к рынку — " +
        "информационные сигналы биржевой статистики, НЕ рейтинг рейтинговых агентств) и " +
        "диверсифицированным по секторам. Оценки — по биржевой статистике, не является " +
        "индивидуальной инвестиционной рекомендацией.";

    /// <summary>
    /// source=market/recommended: кандидаты из ВСЕЙ гигиенически-чистой
    /// фикс-купонной вселенной банка (<c>bond_universe</c>, через <see cref="UniverseHygieneFilter"/>),
    /// НЕ holdings портфеля — точный движок здесь не вызывается, только банк-статистика (тот же
    /// принцип "двухъярусной архитектуры", что <see cref="GetReplacementCandidates"/> mode=market;
    /// план явно запрещает материализовывать вселенную через движок). <paramref name="source"/>=
    /// <c>recommended</c> дополнительно исключает кандидатов с Caution-сигналом ликвидности ИЛИ
    /// спреда (<see cref="CandidateRiskSignalService"/>) и ограничивает концентрацию по
    /// СЕКТОРУ (<see cref="CashAllocationConcentrationAxis.Sector"/> — issuer на банк-слое
    /// недоступен, см. doc-comment <see cref="Core.Analytics.CashAllocationCandidate.Sector"/>);
    /// <paramref name="source"/>=<c>market</c> — честный greedy по доходности БЕЗ секторного лимита
    /// (принятый дефолт NEEDS DECISION "весь рынок" должен показывать самое
    /// доходное без искажений диверсификацией). Обе ветки жёстко ограничивают 1 лот на кандидата
    /// (<c>maxLotsPerCandidate=1</c> — диверсификация "не больше 1 позиции на secid", план часть B.3)
    /// и потолок пула кандидатов <see cref="MarketAllocationCandidatePoolLimit"/> — потолок применяется
    /// ПОСЛЕ риск-фильтра (для recommended), чтобы в пул из 200 попадали лучшие ПРОШЕДШИЕ фильтр
    /// кандидаты, а не обрезок топ-200 "по доходности вообще", из которого фильтр потом выбрасывает
    /// половину.
    /// </summary>
    private static async Task<IResult> BuildMarketAllocationResponseAsync(
        decimal amountRub,
        string source,
        decimal commissionRate,
        ResolvedCommissionRate resolvedRate,
        IBondUniverseRepository universeRepo,
        UniverseHygieneOptions hygieneOptions,
        RelativeValueSnapshotBuilder snapshotBuilder,
        CancellationToken ct)
    {
        var asOf = BusinessClock.MoscowToday();
        var all = await universeRepo.GetAllAsync();

        var eligible = all
            .Where(e => UniverseHygieneFilter.Evaluate(e, hygieneOptions, asOf) == HygieneHiddenReason.None)
            .Where(e => e.IsFloater != true)
            .Where(e => e.YieldFraction is not null && e.DurationYears is not null && e.PricePercent is not null)
            // FaceValue не входит в перечень плана буквально, но без него нечем
            // перевести PricePercent в рубли (PricePercent/100 × FaceValue) — тот же принцип "нечем
            // оценить лот", что и для DurationYears/PricePercent.
            .Where(e => e.FaceValue is > 0m)
            .ToList();

        var snapshot = await snapshotBuilder.GetSnapshotAsync(ct);
        // Словарь хранит уже готовый RiskSignalsDto (не "сырой" CandidateRiskSignals)
        // — светофор надёжности (ToRiskSignalsDto→Aggregate) нуждается в ListLevel/Sector банк-записи,
        // которые есть здесь (entry), но не были бы доступны в точке потребления ниже (a.Secid).
        var signalsBySecid = new Dictionary<string, RiskSignalsDto>(StringComparer.OrdinalIgnoreCase);

        List<BondUniverseEntry> pool;
        int poolAvailable;

        if (source == "recommended")
        {
            var withSignals = eligible
                .Select(e => (Entry: e, Signals: AssessEntryRiskSignals(e, snapshot)))
                .Where(x => x.Signals.Liquidity != SignalLevel.Caution && x.Signals.Spread != SignalLevel.Caution)
                .ToList();

            foreach (var (entry, signals) in withSignals)
            {
                signalsBySecid[entry.Secid] = ToRiskSignalsDto(signals, entry.ListLevel, entry.Sector);
            }

            poolAvailable = withSignals.Count;
            pool = withSignals
                .OrderByDescending(x => x.Entry.YieldFraction!.Value)
                .Take(MarketAllocationCandidatePoolLimit)
                .Select(x => x.Entry)
                .ToList();
        }
        else
        {
            poolAvailable = eligible.Count;
            pool = eligible
                .OrderByDescending(e => e.YieldFraction!.Value)
                .Take(MarketAllocationCandidatePoolLimit)
                .ToList();

            foreach (var entry in pool)
            {
                signalsBySecid[entry.Secid] = ToRiskSignalsDto(AssessEntryRiskSignals(entry, snapshot), entry.ListLevel, entry.Sector);
            }
        }

        var candidates = pool.Select(e => ToAllocationCandidate(e, commissionRate)).ToList();

        var concentrationAxis = source == "recommended"
            ? CashAllocationConcentrationAxis.Sector
            : CashAllocationConcentrationAxis.None;
        var maxSectorSharePercent = source == "recommended"
            ? (decimal?)CashAllocationService.DefaultMaxSectorSharePercent
            : null;

        var result = CashAllocationService.Allocate(
            amountRub,
            candidates,
            currentPortfolioValueRub: 0m, // задача 34: рыночный скринер не знает состав портфеля — база лимита растёт только за счёт вносимых денег (см. doc-comment CashAllocationConcentrationAxis.Sector).
            concentrationAxis: concentrationAxis,
            maxSectorSharePercent: maxSectorSharePercent,
            maxLotsPerCandidate: 1);

        var truncated = poolAvailable > MarketAllocationCandidatePoolLimit;
        var baseDisclaimer = source == "recommended" ? RecommendedAllocationDisclaimer : MarketAllocationDisclaimer;
        var disclaimer = truncated
            ? $"{baseDisclaimer} Показаны топ-{MarketAllocationCandidatePoolLimit} из {poolAvailable} подходящих бумаг по доходности — не все кандидаты участвовали в распределении."
            : baseDisclaimer;

        var dto = new AllocationResponseDto
        {
            AmountRub = result.AmountRub,
            Source = source,
            Allocations = result.Allocations.Select(a => new AllocationLineDto
            {
                InstrumentId = a.InstrumentId,
                Secid = a.Secid,
                Name = a.Name,
                Issuer = a.Issuer,
                Sector = a.Sector,
                Quantity = a.Quantity,
                EstimatedCostRub = a.EstimatedCostRub,
                EffectiveYield = a.EffectiveYield,
                LotSizeAssumed = a.LotSizeAssumed,
                CleanCostRub = a.CleanCostRub,
                AccruedCostRub = a.AccruedCostRub,
                CommissionCostRub = a.CommissionCostRub,
                RiskSignals = a.Secid is not null && signalsBySecid.TryGetValue(a.Secid, out var lineSignals)
                    ? lineSignals
                    : null,
            }).ToList(),
            Skipped = result.Skipped.Select(s => new AllocationSkipDto
            {
                InstrumentId = s.InstrumentId,
                Secid = s.Secid,
                Name = s.Name,
                Issuer = s.Issuer,
                Reason = s.Reason.ToString(),
            }).ToList(),
            LeftoverRub = result.LeftoverRub,
            Disclaimer = disclaimer,
            CommissionRateUsed = resolvedRate.Rate,
            CommissionRateSource = resolvedRate.Source.ToString(),
            CandidatePoolAvailable = poolAvailable,
            CandidatePoolLimit = MarketAllocationCandidatePoolLimit,
            CandidatePoolTruncated = truncated,
        };

        return Results.Ok(dto);
    }

    /// <summary>
    /// Маппинг банк-записи в кандидата аллокации. Цена лота (рубли за одну бумагу) =
    /// <c>PricePercent/100 × FaceValue</c> (чистая цена) + <c>AccruedRub</c> (НКД в рублях на бумагу,
    /// MOEX ACCRUEDINT — задача 42; контракт единиц — docs/CODEBASE-GUIDE.md) + комиссия покупки
    /// от ГРЯЗНОЙ цены (как в ветке source=portfolio: <c>pricePerUnit × rate</c>). Пример: цена 95%,
    /// номинал 1000, НКД 12.34 ₽, комиссия 0.3% → (950 + 12.34) × 1.003 = 965.23 ₽.
    /// Если НКД банк-записи неизвестен (<c>accrued_rub IS NULL</c> — снимок до миграции 017 или
    /// биржа не отдала) — прежнее приближение "чистая цена без НКД" (AccruedRub=0): цена лота
    /// занижена на величину НКД. InstrumentId — null (см. doc-comment
    /// <c>CashAllocationCandidate.InstrumentId</c>), идентификатор — Secid. Issuer — null
    /// (банк эмитента не хранит), Sector — из банка (альтернативная ось концентрации).
    /// IsComparable=true безусловно: флоатеры уже исключены фильтром вызывающего кода
    /// (<c>entry.IsFloater != true</c>), других "несравнимых" типов на банк-слое нет (тот же принцип,
    /// что <see cref="GetReplacementCandidates"/> mode=market).
    /// </summary>
    private static CashAllocationCandidate ToAllocationCandidate(BondUniverseEntry entry, decimal commissionRate)
    {
        var cleanPriceRub = entry.PricePercent!.Value / 100m * entry.FaceValue!.Value;
        var accruedRub = entry.AccruedRub ?? 0m;
        var dirtyPriceRub = cleanPriceRub + accruedRub;
        var commissionRub = dirtyPriceRub * commissionRate;
        var pricePerLotRub = dirtyPriceRub + commissionRub;

        return new CashAllocationCandidate
        {
            InstrumentId = null,
            Secid = entry.Secid,
            Name = entry.ShortName ?? entry.SecName ?? entry.Secid,
            Issuer = null,
            Sector = entry.Sector,
            EffectiveYield = entry.YieldFraction,
            PricePerLotRub = pricePerLotRub,
            LotSize = 1m,
            LotSizeIsAssumed = true,
            CurrentIssuerMarketValueRub = 0m,
            CleanPriceRub = cleanPriceRub,
            AccruedRub = accruedRub,
            CommissionRub = commissionRub,
            IsComparable = true,
        };
    }

    // ─── POST /api/analytics/basket ─────────────────────────────────────────────────────────

    /// <summary>
    /// Конструктор портфеля — «собрал корзину процентами → штуки + what-if». Вход —
    /// сумма + строки {instrumentId, weightFraction} (доля 0..1, Σ ≤ 1 — та же конвенция, что
    /// <see cref="BasketBuilderService"/>). Кандидаты собираются тем же путём, что
    /// <see cref="GetAllocation"/> (holdings портфеля через <see cref="PortfolioHoldingsBuilder.BuildForAccountAsync"/>,
    /// бумаги вне портфеля — через <see cref="PortfolioHoldingsBuilder.BuildForInstrumentsAsync"/>,
    /// цена лота — грязная (clean+НКД) + комиссия из <see cref="ICommissionRateProvider"/>).
    /// Вызывает <see cref="BasketBuilderService.Build"/>, затем <see cref="PortfolioWhatIfService.Evaluate"/>
    /// (текущие holdings "до" + строки корзины "после"), лимит концентрации для warnings —
    /// <c>UserSettings.DefaultMaxConcentrationPercent</c> (тот же дефолт, что у аллокации/сигналов).
    /// <para>
    /// <b>Валидация (422):</b> amountRub &gt; 0; каждая строка weightFraction ∈ (0,1]; Σ весов ≤ 1.0001;
    /// instrumentId должен существовать (иначе неясно, что показывать пользователю — лучше явная
    /// ошибка, чем молчаливый пропуск строки).
    /// </para>
    /// </summary>
    private static async Task<IResult> PostBasket(
        ICurrentUser currentUser,
        BasketRequestDto request,
        IAccountRepository accountRepo,
        IUserSettingsRepository userSettingsRepo,
        PortfolioHoldingsBuilder holdingsBuilder,
        IInstrumentRepository instrumentRepo,
        ICommissionRateProvider commissionRateProvider,
        Microsoft.Extensions.Options.IOptions<Bonds.Core.Signals.SignalEngineOptions> defaultSignalOptions,
        CancellationToken ct = default)
    {
        if (request.AmountRub <= 0m)
        {
            return ValidationError("amountRub должен быть положительным");
        }

        if (request.Lines is null || request.Lines.Count == 0)
        {
            return ValidationError("Корзина должна содержать хотя бы одну строку");
        }

        foreach (var line in request.Lines)
        {
            if (line.WeightFraction <= 0m || line.WeightFraction > 1m)
            {
                return ValidationError($"Вес строки instrumentId={line.InstrumentId} должен быть в диапазоне (0, 1]");
            }
        }

        var totalWeight = request.Lines.Sum(l => l.WeightFraction);
        if (totalWeight > 1.0001m)
        {
            return ValidationError($"Сумма весов строк ({totalWeight:0.####}) не может превышать 1");
        }

        var requestedInstrumentIds = request.Lines.Select(l => l.InstrumentId).ToList();
        if (requestedInstrumentIds.Distinct().Count() != requestedInstrumentIds.Count)
        {
            return ValidationError("Инструмент не может встречаться в корзине дважды");
        }

        foreach (var instrumentId in requestedInstrumentIds)
        {
            var instrument = await instrumentRepo.GetByIdAsync(instrumentId);
            if (instrument is null)
            {
                return ValidationError($"Инструмент instrumentId={instrumentId} не найден");
            }
        }

        var accountId = await PositionsEndpoints.ResolveAccountIdAsync(currentUser, accountRepo);
        var asOf = BusinessClock.MoscowToday();

        var resolvedRate = accountId is not null
            ? await commissionRateProvider.GetAsync(accountId.Value, ct)
            : new ResolvedCommissionRate(SwitchAnalysisService.DefaultCommissionRate, CommissionRateSource.Default, null);
        var commissionRate = resolvedRate.Rate;

        var portfolioHoldings = accountId is not null
            ? (await holdingsBuilder.BuildForAccountAsync(accountId.Value, asOf)).ToList()
            : [];

        var portfolioHoldingsByInstrument = portfolioHoldings.ToDictionary(h => h.InstrumentId);
        var missingInstrumentIds = requestedInstrumentIds.Where(id => !portfolioHoldingsByInstrument.ContainsKey(id)).Distinct().ToList();
        var extraHoldings = missingInstrumentIds.Count > 0
            ? await holdingsBuilder.BuildForInstrumentsAsync(missingInstrumentIds, asOf)
            : [];
        var extraHoldingsByInstrument = extraHoldings.ToDictionary(h => h.InstrumentId);

        var basketLineInputs = new List<BasketLineInput>(request.Lines.Count);
        foreach (var line in request.Lines)
        {
            var holding = portfolioHoldingsByInstrument.TryGetValue(line.InstrumentId, out var owned)
                ? owned
                : extraHoldingsByInstrument.GetValueOrDefault(line.InstrumentId);

            if (holding is null)
            {
                // Ссылочная целостность нарушилась между проверкой выше и сборкой holdings — не ожидается
                // в норме (инструмент существует, но BuildForInstrumentsAsync его не вернул из-за гонки).
                return ValidationError($"Не удалось получить котировку для instrumentId={line.InstrumentId}");
            }

            basketLineInputs.Add(BuildBasketLineInput(holding, line.WeightFraction, commissionRate));
        }

        var basketResult = BasketBuilderService.Build(request.AmountRub, basketLineInputs);

        var userSettings = await currentUser.GetUserIdAsync() is { } userId ? await userSettingsRepo.GetByUserIdAsync(userId) : null;
        var maxConcentrationPercent = userSettings?.DefaultMaxConcentrationPercent ?? defaultSignalOptions.Value.DefaultMaxConcentrationPercent;

        var whatIfHoldings = portfolioHoldings
            .Select(h => new WhatIfHoldingInput
            {
                InstrumentId = h.InstrumentId,
                Issuer = h.Issuer,
                MarketValueRub = h.MarketValueRub,
                EffectiveYield = (h.IsFloater || h.IsIndexed) ? h.CurrentYield : h.YtmEffective,
                ModifiedDuration = h.ModifiedDuration,
                IsFloater = h.IsFloater || h.IsIndexed,
            })
            .ToList();

        var whatIfResult = PortfolioWhatIfService.Evaluate(whatIfHoldings, basketResult.Lines, maxConcentrationPercent);

        return Results.Ok(new BasketResponseDto
        {
            Basket = ToBasketDto(basketResult),
            WhatIf = ToWhatIfDto(whatIfResult),
            Disclaimer = BasketBuilderService.Disclaimer,
        });
    }

    private static BasketLineInput BuildBasketLineInput(
        PortfolioHolding holding, decimal weightFraction, decimal commissionRate)
    {
        // Тот же расчёт цены лота, что GetAllocation/BuildCandidate: MarketValueRub уже грязная цена
        // (clean+НКД) на Quantity штук; для holding'ов вне портфеля (BuildForInstrumentsAsync) Quantity=1,
        // поэтому pricePerUnitRub = MarketValueRub в обоих случаях (та же формула).
        var pricePerUnitRub = holding.Quantity > 0m ? holding.MarketValueRub / holding.Quantity : holding.MarketValueRub;
        var accruedRub = holding.AccruedPerBondRub;
        var cleanPriceRub = pricePerUnitRub - accruedRub;
        var commissionRub = pricePerUnitRub * commissionRate;
        var pricePerLotWithCommission = pricePerUnitRub + commissionRub;
        var effectiveYield = (holding.IsFloater || holding.IsIndexed) ? holding.CurrentYield : holding.YtmEffective;

        return new BasketLineInput
        {
            InstrumentId = holding.InstrumentId,
            Name = holding.Name,
            Issuer = holding.Issuer,
            TargetWeightFraction = weightFraction,
            PricePerLotRub = pricePerLotWithCommission,
            CleanPriceRub = cleanPriceRub,
            AccruedRub = accruedRub,
            CommissionRub = commissionRub,
            LotSize = 1m,
            LotSizeIsAssumed = true,
            EffectiveYield = effectiveYield,
            ModifiedDuration = holding.ModifiedDuration,
            IsFloater = holding.IsFloater || holding.IsIndexed,
        };
    }

    private static BasketDto ToBasketDto(BasketBuildResult result) => new()
    {
        AmountRub = result.AmountRub,
        Lines = result.Lines.Select(l => new BasketLineDto
        {
            InstrumentId = l.InstrumentId,
            Name = l.Name,
            Issuer = l.Issuer,
            TargetWeightFraction = l.TargetWeightFraction,
            ActualWeightFraction = l.ActualWeightFraction,
            Quantity = l.Quantity,
            ActualCostRub = l.ActualCostRub,
            EffectiveYield = l.EffectiveYield,
            ModifiedDuration = l.ModifiedDuration,
            IsFloater = l.IsFloater,
            LotSizeAssumed = l.LotSizeAssumed,
            CleanCostRub = l.CleanCostRub,
            AccruedCostRub = l.AccruedCostRub,
            CommissionCostRub = l.CommissionCostRub,
        }).ToList(),
        LeftoverRub = result.LeftoverRub,
        Metrics = new BasketMetricsDto
        {
            TotalCostRub = result.Metrics.TotalCostRub,
            WeightedYield = result.Metrics.WeightedYield,
            WeightedDuration = result.Metrics.WeightedDuration,
            HasExcludedFloaters = result.Metrics.HasExcludedFloaters,
        },
    };

    private static WhatIfDto ToWhatIfDto(PortfolioWhatIfResult result) => new()
    {
        Before = new WhatIfSnapshotDto
        {
            TotalValueRub = result.Before.TotalValueRub,
            WeightedYield = result.Before.WeightedYield,
            WeightedDuration = result.Before.WeightedDuration,
            HasExcludedFloaters = result.Before.HasExcludedFloaters,
        },
        After = new WhatIfSnapshotDto
        {
            TotalValueRub = result.After.TotalValueRub,
            WeightedYield = result.After.WeightedYield,
            WeightedDuration = result.After.WeightedDuration,
            HasExcludedFloaters = result.After.HasExcludedFloaters,
        },
        Concentrations = result.Concentrations.Select(c => new WhatIfConcentrationDto
        {
            Issuer = c.Issuer,
            SharePercentBefore = c.SharePercentBefore,
            SharePercentAfter = c.SharePercentAfter,
        }).ToList(),
        Warnings = result.Warnings.Select(w => new WhatIfWarningDto
        {
            Kind = w.Kind.ToString(),
            Issuer = w.Issuer,
            SharePercentAfter = w.SharePercentAfter,
        }).ToList(),
    };
}

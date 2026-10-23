using System.Globalization;
using Bonds.Core.Analytics;
using Bonds.Core.Models;

namespace Bonds.Core.Signals;

/// <summary>
/// Движок событий (spec §8) — чистый, без I/O. Каждый вызов возвращает ПОЛНЫЙ набор
/// активных событий на дату <see cref="SignalEvaluationInput.AsOf"/> («текущее состояние»), а не
/// «новые к вставке»: что вставить/обновить/закрыть решает <see cref="SignalSetSynchronizer"/>,
/// сравнивая этот набор с таблицей по ключу <see cref="Signal.EventKey"/>.
/// <para>
/// Правила смотрят только на ОТКРЫТЫЕ позиции (Quantity &gt; 0) и только на будущие/текущие даты.
/// Удалены относительно прежнего движка: UpcomingCoupon (календарь выплат уже на «Денежном
/// потоке»), YieldBelowAlternative (дублировал «слабые позиции» Рекомендаций и сравнивал
/// несравнимое), DurationDriftFromTarget (цели никто не задаёт), LowLiquidityWarning (заглушка).
/// </para>
/// Формулировки — «оценка/кандидат», не «купите/продайте» (продуктовая константа репо).
/// </summary>
public static class SignalsEngine
{
    public static IReadOnlyList<Signal> Evaluate(SignalEvaluationInput input)
    {
        var candidates = new List<Signal>();

        candidates.AddRange(UpcomingOfferRule(input));
        candidates.AddRange(UpcomingRedemptionRule(input));
        candidates.AddRange(UpcomingAmortizationRule(input));
        candidates.AddRange(FloaterRateResetRule(input));
        candidates.AddRange(ConcentrationLimitRule(input));
        candidates.AddRange(UninvestedCashRule(input));
        candidates.AddRange(SpreadWideningRule(input));
        candidates.AddRange(PriceDrawdownRule(input));
        candidates.AddRange(IssuerDefaultRule(input));

        // Ключ уникален в БД: если два правила дали один ключ (put и call одной даты по одной бумаге),
        // оставляем более важное событие.
        return candidates
            .GroupBy(s => s.EventKey)
            .Select(g => g.OrderByDescending(s => s.Severity).First())
            .ToList();
    }

    private static string Name(SignalPositionContext p) => p.Name ?? p.Issuer ?? $"Бумага #{p.InstrumentId}";

    private static IEnumerable<SignalPositionContext> OpenPositions(SignalEvaluationInput input) =>
        input.Positions.Where(p => p.Quantity > 0m);

    private static bool InWindow(SignalEvaluationInput input, DateOnly date) =>
        date >= input.AsOf && date <= input.AsOf.AddDays(input.Options.UpcomingEventDaysThreshold);

    private static Signal ForInstrument(
        SignalEvaluationInput input, SignalPositionContext position, SignalType type,
        SignalSeverity severity, DateOnly date, string action) => new()
    {
        AccountId = input.AccountId,
        EventKey = Signal.BuildKey(type, position.InstrumentId.ToString(), date),
        Type = type,
        Severity = severity,
        InstrumentId = position.InstrumentId,
        Subject = Name(position),
        EventDate = date,
        SuggestedAction = action,
    };

    // ─── Оферты: put — Critical (требует решения владельца), call — Warning ────────────────

    private static IEnumerable<Signal> UpcomingOfferRule(SignalEvaluationInput input)
    {
        foreach (var position in OpenPositions(input))
        {
            foreach (var offer in position.Offers)
            {
                if (offer.IsExecuted || !InWindow(input, offer.Date)) continue;

                if (offer.OfferType == OfferType.Put)
                {
                    yield return ForInstrument(input, position, SignalType.UpcomingOffer, SignalSeverity.Critical, offer.Date,
                        $"Put-оферта {offer.Date:dd.MM.yyyy}: решите до этой даты, предъявлять ли бумагу к выкупу. " +
                        "Заявку нужно подать через брокера — иначе бумага останется в портфеле.");
                }
                else
                {
                    yield return ForInstrument(input, position, SignalType.UpcomingOffer, SignalSeverity.Warning, offer.Date,
                        $"Call-оферта {offer.Date:dd.MM.yyyy}: эмитент может досрочно выкупить бумагу — " +
                        "доходность «к погашению» в этом случае не реализуется (оценка).");
                }
            }
        }
    }

    // ─── Погашение и амортизация — Info ───────────────────────────────────────────────────

    private static IEnumerable<Signal> UpcomingRedemptionRule(SignalEvaluationInput input)
    {
        foreach (var position in OpenPositions(input))
        {
            if (!InWindow(input, position.MaturityDate)) continue;

            yield return ForInstrument(input, position, SignalType.UpcomingRedemption, SignalSeverity.Info, position.MaturityDate,
                $"Погашение {position.MaturityDate:dd.MM.yyyy}: номинал и последний купон поступят на счёт — " +
                "кандидат на реинвестирование освободившихся денег.");
        }
    }

    private static IEnumerable<Signal> UpcomingAmortizationRule(SignalEvaluationInput input)
    {
        foreach (var position in OpenPositions(input))
        {
            foreach (var amortization in position.Amortizations)
            {
                if (!InWindow(input, amortization.Date)) continue;

                // Сумма может быть неизвестна (MBS/ипотечный агент) — не показываем 0.00 как реальную.
                var action = amortization.IsKnown
                    ? $"Частичное погашение номинала {amortization.Date:dd.MM.yyyy}: ≈ {amortization.AmountRub:F2} руб. на бумагу."
                    : $"Частичное погашение номинала {amortization.Date:dd.MM.yyyy} (точная сумма не известна).";

                yield return ForInstrument(input, position, SignalType.UpcomingAmortization, SignalSeverity.Info, amortization.Date, action);
            }
        }
    }

    // ─── Пересчёт ставки — ТОЛЬКО настоящие флоатеры ──────────────────────────────────────

    private static IEnumerable<Signal> FloaterRateResetRule(SignalEvaluationInput input)
    {
        foreach (var position in OpenPositions(input))
        {
            // Фикс с офертой/неизвестной суммой купона флоатером не является (BACKLOG-техдолг).
            if (position.CouponType != CouponType.Floating) continue;

            // Ближайшая дата с ещё не зафиксированной ставкой (CouponSchedule.IsKnown == false).
            var nextReset = position.Coupons
                .Where(c => !c.IsKnown && c.CouponDate >= input.AsOf)
                .OrderBy(c => c.CouponDate)
                .FirstOrDefault();

            if (nextReset is null || !InWindow(input, nextReset.CouponDate)) continue;

            yield return ForInstrument(input, position, SignalType.FloaterRateReset, SignalSeverity.Info, nextReset.CouponDate,
                $"Пересчёт ставки купона {nextReset.CouponDate:dd.MM.yyyy}: размер следующего купона станет известен после даты.");
        }
    }

    // ─── Концентрация по эмитенту — Warning, состояние ────────────────────────────────────

    private static IEnumerable<Signal> ConcentrationLimitRule(SignalEvaluationInput input)
    {
        var holdings = input.Holdings.Where(h => h.Quantity > 0m).ToList();
        if (holdings.Count == 0) yield break;

        var composition = PortfolioCompositionService.Calculate(holdings);
        if (composition.TotalMarketValueRub <= 0m) yield break;

        foreach (var share in composition.ByIssuer)
        {
            // Проценты (0..100) с обеих сторон: SharePercent — исключение из «долей», см. CODEBASE-GUIDE.
            // Лимит один на всех эмитентов — user_settings.default_max_concentration_percent (задача 62 убрала индивидуальные).
            var limit = input.Options.DefaultMaxConcentrationPercent;
            if (share.SharePercent <= limit) continue;

            yield return new Signal
            {
                AccountId = input.AccountId,
                EventKey = Signal.BuildKey(SignalType.ConcentrationLimitBreached, share.Key, null),
                Type = SignalType.ConcentrationLimitBreached,
                Severity = SignalSeverity.Warning,
                Subject = share.Key,
                EventDate = null,
                SuggestedAction = $"Доля эмитента «{share.Key}» в портфеле {share.SharePercent:F1}% при лимите {limit:F1}% — " +
                                  "рассмотрите диверсификацию (оценка).",
            };
        }
    }

    // ─── Свободные деньги выше порога — Info, состояние (по accounts.cash_rub) ────────────

    private static IEnumerable<Signal> UninvestedCashRule(SignalEvaluationInput input)
    {
        if (input.CashRub is not { } cash) yield break;
        if (cash <= input.Options.UninvestedCashThresholdRub) yield break;

        yield return new Signal
        {
            AccountId = input.AccountId,
            EventKey = Signal.BuildKey(SignalType.UninvestedCashThreshold, null, null),
            Type = SignalType.UninvestedCashThreshold,
            Severity = SignalSeverity.Info,
            Subject = "Свободные деньги",
            EventDate = null,
            SuggestedAction = $"На счёте ≈ {cash:F0} руб. свободных денег (порог {input.Options.UninvestedCashThresholdRub:F0} руб.) — " +
                              "кандидат на реинвестирование.",
        };
    }
    // ─── Рыночные события (задача 49): расширение G-спреда и просадка цены — Warning, по неделе ──────

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>
    /// SpreadWidening: эффективный G-спред бумаги сегодня − МЕДИАНА его же значений за прошлые торговые дни
    /// (до 20; сегодняшняя строка истории не участвует) строго больше
    /// <see cref="SignalEngineOptions.SpreadWideningThresholdFraction"/>. Меньше
    /// <see cref="SignalEngineOptions.MarketHistoryMinDays"/> дней истории — молчим. Флоатеры и бумаги вне банка
    /// (нет <see cref="PositionMarketContext"/>) пропускаются. Единицы: спред и порог — ДОЛИ; б.п. в тексте = доля × 10000.
    /// </summary>
    private static IEnumerable<Signal> SpreadWideningRule(SignalEvaluationInput input)
    {
        foreach (var position in OpenPositions(input))
        {
            var market = MarketOf(input, position);
            if (market is null || market.IsFloater || market.GSpreadFraction is not { } today) continue;
            if (market.GSpreadHistory.Count < input.Options.MarketHistoryMinDays) continue;

            var median = ReliabilityScoreService.Median(market.GSpreadHistory);
            var shift = today - median;
            if (shift <= input.Options.SpreadWideningThresholdFraction) continue;

            yield return ForMarket(input, position, market, SignalType.SpreadWidening,
                $"G-спред бумаги расширился на {(shift * 10000m).ToString("0", Inv)} б.п. к медиане за {market.GSpreadHistory.Count} торговых дн. " +
                $"(порог {(input.Options.SpreadWideningThresholdFraction * 10000m).ToString("0", Inv)} б.п.) — рынок закладывает больший риск или бумага подешевела: " +
                "рассмотрите состояние эмитента и вердикт позиции (оценка).");
        }
    }

    /// <summary>
    /// PriceDrawdown: чистая цена сегодня ниже максимума (история ∪ сегодня) за прошлые торговые дни строго более чем на
    /// <see cref="SignalEngineOptions.PriceDrawdownThresholdFraction"/> (ДОЛЯ от максимума). Та же формула, что
    /// у компонента price_drawdown скоркарты. Применима и к флоатерам.
    /// </summary>
    private static IEnumerable<Signal> PriceDrawdownRule(SignalEvaluationInput input)
    {
        foreach (var position in OpenPositions(input))
        {
            var market = MarketOf(input, position);
            if (market is null || market.PricePercent is not { } price) continue;
            if (market.PriceHistory.Count < input.Options.MarketHistoryMinDays) continue;

            var peak = Math.Max(market.PriceHistory.Max(), price);
            if (peak <= 0m) continue;
            var drawdown = (peak - price) / peak;
            if (drawdown <= input.Options.PriceDrawdownThresholdFraction) continue;

            yield return ForMarket(input, position, market, SignalType.PriceDrawdown,
                $"Цена бумаги {price.ToString("0.##", Inv)}% номинала — на {(drawdown * 100m).ToString("0.0", Inv)}% ниже максимума за {market.PriceHistory.Count} торговых дн. " +
                $"(порог {(input.Options.PriceDrawdownThresholdFraction * 100m).ToString("0.#", Inv)}%) — рассмотрите причину просадки и вердикт позиции (оценка).");
        }
    }

    private static PositionMarketContext? MarketOf(SignalEvaluationInput input, SignalPositionContext position) =>
        input.MarketContexts.FirstOrDefault(m => m.InstrumentId == position.InstrumentId);

    /// <summary>
    /// Событие по бумаге с ключом «ISIN + ISO-неделя» (без ISIN — «#id инструмента»): внутри недели ключ постоянен,
    /// событие не мигает день-к-дню; при нормализации оно исчезает из набора и закрывается синхронизатором.
    /// </summary>
    private static Signal ForMarket(SignalEvaluationInput input, SignalPositionContext position, PositionMarketContext market,
        SignalType type, string action)
    {
        var subject = $"{market.Isin ?? $"#{position.InstrumentId}"}|{IsoWeekLabel(input.AsOf)}";
        return new Signal
        {
            AccountId = input.AccountId,
            EventKey = Signal.BuildKey(type, subject, null),
            Type = type,
            Severity = SignalSeverity.Warning,
            InstrumentId = position.InstrumentId,
            Subject = Name(position),
            EventDate = null,
            SuggestedAction = action,
        };
    }

    /// <summary>ISO-неделя даты: «2026-W42» (год ISO-недели, а не календарный — на стыке лет неделя не рвётся).</summary>
    internal static string IsoWeekLabel(DateOnly date)
    {
        var dateTime = date.ToDateTime(TimeOnly.MinValue);
        return string.Create(Inv, $"{ISOWeek.GetYear(dateTime)}-W{ISOWeek.GetWeekOfYear(dateTime):00}");
    }

    // ─── Кредитное событие (задача 49): дефолт/техдефолт эмитента — Critical, состояние ───────────────

    /// <summary>
    /// IssuerDefaultEvent: у эмитента открытой позиции в карточках бумаг MOEX (по ВСЕМ его выпускам) стоит
    /// <c>has_default</c> и/или <c>has_technical_default</c>. Ключ — emitent_id + вид («default»/«technical»), без даты:
    /// событие живёт, пока флаг есть, и закрывается, когда он исчез. Два флага — два события. Critical (Telegram-алерт —
    /// в <c>SyncCycleService</c>, как у put-оферты). Эмитенты без открытых позиций сюда не попадают (контекст строит вызывающий слой).
    /// </summary>
    private static IEnumerable<Signal> IssuerDefaultRule(SignalEvaluationInput input)
    {
        var openInstruments = OpenPositions(input).Select(p => p.InstrumentId).ToHashSet();

        foreach (var issuer in input.IssuerDefaults)
        {
            if (!openInstruments.Contains(issuer.InstrumentId)) continue;

            if (issuer.HasDefault)
            {
                yield return IssuerDefault(input, issuer, "default",
                    $"У эмитента «{issuer.Subject}» в карточках бумаг MOEX отмечен дефолт по облигациям — " +
                    "проверьте раскрытие информации эмитента и рассмотрите вердикт позиции (оценка, не рекомендация).");
            }

            if (issuer.HasTechnicalDefault)
            {
                yield return IssuerDefault(input, issuer, "technical",
                    $"У эмитента «{issuer.Subject}» в карточках бумаг MOEX отмечен технический дефолт — " +
                    "проверьте раскрытие информации эмитента и рассмотрите вердикт позиции (оценка, не рекомендация).");
            }
        }
    }

    private static Signal IssuerDefault(SignalEvaluationInput input, IssuerDefaultContext issuer, string kind, string action) => new()
    {
        AccountId = input.AccountId,
        EventKey = Signal.BuildKey(SignalType.IssuerDefaultEvent, $"{issuer.EmitentId}:{kind}", null),
        Type = SignalType.IssuerDefaultEvent,
        Severity = SignalSeverity.Critical,
        InstrumentId = issuer.InstrumentId,
        Subject = issuer.Subject,
        EventDate = null,
        SuggestedAction = action,
    };
}

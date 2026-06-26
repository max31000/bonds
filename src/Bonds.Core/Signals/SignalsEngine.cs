using Bonds.Core.Analytics;
using Bonds.Core.Models;

namespace Bonds.Core.Signals;

/// <summary>
/// Координатор Signals Engine (plan/07 Часть A, spec §8) — чистый, без I/O. Прогоняет все
/// правила-триггеры над <see cref="SignalEvaluationInput"/> и возвращает финальный список сигналов
/// "к вставке" (кандидаты уже дедуплицированы против <see cref="SignalEvaluationInput.ExistingUnreadSignals"/>
/// через <see cref="SignalDeduplicator"/> — координатор делает это сам, чтобы вызывающий код
/// (Infrastructure) мог просто взять результат и вызвать <c>ISignalRepository.CreateAsync</c> для
/// каждого элемента без дополнительной логики).
/// <para>
/// Стиль организации — один файл-координатор + приватные методы-правила (а не отдельный файл на
/// правило): восемь правил §8 умеренного размера каждое, разделение на 8+ файлов добавило бы
/// навигационных накладных расходов без выигрыша в тестируемости (правила тестируются по
/// отдельности через unit-тесты на <see cref="Evaluate"/> с разным входом независимо от того,
/// в скольких файлах лежит реализация).
/// </para>
/// </summary>
public static class SignalsEngine
{
    public static IReadOnlyList<Signal> Evaluate(SignalEvaluationInput input)
    {
        var candidates = new List<Signal>();

        candidates.AddRange(UpcomingCouponRule(input));
        candidates.AddRange(UpcomingAmortizationRule(input));
        candidates.AddRange(UpcomingRedemptionRule(input));
        candidates.AddRange(UpcomingOfferRule(input));
        candidates.AddRange(FloaterRateResetRule(input));
        candidates.AddRange(UninvestedCashRule(input));
        candidates.AddRange(YieldBelowAlternativeRule(input));
        candidates.AddRange(ConcentrationLimitRule(input));
        candidates.AddRange(DurationDriftRule(input));
        // LowLiquidityWarning — намеренно НЕ включено в candidates здесь; см. LowLiquidityWarningRule
        // doc-comment ниже — заглушка вызывается отдельно вызывающим кодом по желанию, всегда пустая.

        return SignalDeduplicator.FilterNew(candidates, input.ExistingUnreadSignals);
    }

    // ─── Правило 1a: приближается купон (spec §8) ──────────────────────────────────────────

    private static IEnumerable<Signal> UpcomingCouponRule(SignalEvaluationInput input)
    {
        var thresholdDate = input.AsOf.AddDays(input.Options.UpcomingEventDaysThreshold);

        foreach (var position in input.Positions)
        {
            foreach (var coupon in position.Coupons)
            {
                if (coupon.CouponDate < input.AsOf || coupon.CouponDate > thresholdDate) continue;

                yield return new Signal
                {
                    AccountId = input.AccountId,
                    Type = SignalType.UpcomingCoupon,
                    Severity = SignalSeverity.Info,
                    PositionId = position.PositionId,
                    InstrumentId = position.InstrumentId,
                    Date = coupon.CouponDate,
                    SuggestedAction = coupon.IsKnown
                        ? $"Купон {coupon.ValueRub:F2} руб. по позиции {position.Name ?? position.Issuer ?? position.InstrumentId.ToString()} ожидается {coupon.CouponDate:yyyy-MM-dd}"
                        : $"Купон по позиции {position.Name ?? position.Issuer ?? position.InstrumentId.ToString()} ожидается {coupon.CouponDate:yyyy-MM-dd} (точная сумма не известна — флоатер)",
                };
            }
        }
    }

    // ─── Правило 1b: приближается амортизация (spec §8) ────────────────────────────────────

    private static IEnumerable<Signal> UpcomingAmortizationRule(SignalEvaluationInput input)
    {
        var thresholdDate = input.AsOf.AddDays(input.Options.UpcomingEventDaysThreshold);

        foreach (var position in input.Positions)
        {
            foreach (var amortization in position.Amortizations)
            {
                if (amortization.Date < input.AsOf || amortization.Date > thresholdDate) continue;

                yield return new Signal
                {
                    AccountId = input.AccountId,
                    Type = SignalType.UpcomingAmortization,
                    Severity = SignalSeverity.Info,
                    PositionId = position.PositionId,
                    InstrumentId = position.InstrumentId,
                    Date = amortization.Date,
                    SuggestedAction = $"Частичное погашение номинала {amortization.AmountRub:F2} руб. по позиции {position.Name ?? position.Issuer ?? position.InstrumentId.ToString()} ожидается {amortization.Date:yyyy-MM-dd}",
                };
            }
        }
    }

    // ─── Правило 1c: приближается погашение (spec §8) ──────────────────────────────────────

    private static IEnumerable<Signal> UpcomingRedemptionRule(SignalEvaluationInput input)
    {
        var thresholdDate = input.AsOf.AddDays(input.Options.UpcomingEventDaysThreshold);

        foreach (var position in input.Positions)
        {
            if (position.MaturityDate < input.AsOf || position.MaturityDate > thresholdDate) continue;

            yield return new Signal
            {
                AccountId = input.AccountId,
                Type = SignalType.UpcomingRedemption,
                Severity = SignalSeverity.Info,
                PositionId = position.PositionId,
                InstrumentId = position.InstrumentId,
                Date = position.MaturityDate,
                SuggestedAction = $"Погашение позиции {position.Name ?? position.Issuer ?? position.InstrumentId.ToString()} ожидается {position.MaturityDate:yyyy-MM-dd}",
            };
        }
    }

    // ─── Правило 2: приближается оферта put/call — приоритетный, Critical (spec §8) ────────

    private static IEnumerable<Signal> UpcomingOfferRule(SignalEvaluationInput input)
    {
        var thresholdDate = input.AsOf.AddDays(input.Options.UpcomingEventDaysThreshold);

        foreach (var position in input.Positions)
        {
            foreach (var offer in position.Offers)
            {
                if (offer.IsExecuted) continue;
                if (offer.Date < input.AsOf || offer.Date > thresholdDate) continue;

                // Put требует активного действия через брокера (легко пропустить, высокая ценность,
                // spec §8) — Critical для обоих типов оферты по заданию ("Приближается оферта
                // put/call — высокая (приоритетный)"), не только put: задание явно не разделяет
                // важность put/call в перечне триггеров, поэтому оба получают Critical, а текст
                // SuggestedAction отдельно подчёркивает, что именно put требует действия.
                var actionText = offer.OfferType == OfferType.Put
                    ? $"Put-оферта по позиции {position.Name ?? position.Issuer ?? position.InstrumentId.ToString()} {offer.Date:yyyy-MM-dd} — требуется активно подать заявку через брокера, иначе бумага останется в портфеле"
                    : $"Call-оферта по позиции {position.Name ?? position.Issuer ?? position.InstrumentId.ToString()} {offer.Date:yyyy-MM-dd} — эмитент может отозвать бумагу";

                yield return new Signal
                {
                    AccountId = input.AccountId,
                    Type = SignalType.UpcomingOffer,
                    Severity = SignalSeverity.Critical,
                    PositionId = position.PositionId,
                    InstrumentId = position.InstrumentId,
                    Date = offer.Date,
                    SuggestedAction = actionText,
                };
            }
        }
    }

    // ─── Правило 3: пересчёт купона флоатера (spec §8) ─────────────────────────────────────

    private static IEnumerable<Signal> FloaterRateResetRule(SignalEvaluationInput input)
    {
        var thresholdDate = input.AsOf.AddDays(input.Options.UpcomingEventDaysThreshold);

        foreach (var position in input.Positions)
        {
            // "Дата пересчёта" — ближайший CouponSchedule с IsKnown == false (значение ставки ещё
            // не зафиксировано MOEX на эту дату, см. doc-comment CouponSchedule.IsKnown).
            var nextReset = position.Coupons
                .Where(c => !c.IsKnown)
                .Where(c => c.CouponDate >= input.AsOf)
                .OrderBy(c => c.CouponDate)
                .FirstOrDefault();

            if (nextReset is null) continue;
            if (nextReset.CouponDate > thresholdDate) continue;

            yield return new Signal
            {
                AccountId = input.AccountId,
                Type = SignalType.FloaterRateReset,
                Severity = SignalSeverity.Info,
                PositionId = position.PositionId,
                InstrumentId = position.InstrumentId,
                Date = nextReset.CouponDate,
                SuggestedAction = $"Пересчёт ставки купона флоатера по позиции {position.Name ?? position.Issuer ?? position.InstrumentId.ToString()} ожидается {nextReset.CouponDate:yyyy-MM-dd}",
            };
        }
    }

    // ─── Правило 4: незаинвестированный кэш выше порога (spec §8) ──────────────────────────

    /// <summary>
    /// Эвристика расчёта незаинвестированного кэша (самостоятельное решение, задание явно
    /// допускает выбор разумной эвристики): за скользящее окно
    /// <see cref="SignalEngineOptions.UninvestedCashLookbackDays"/> дней суммируются поступления
    /// типа Coupon/Amortization/Redemption (положительный денежный поток, см. doc-comment
    /// <see cref="Operation.AmountRub"/> — эти типы у брокера всегда положительны) минус сумма
    /// покупок (Buy, у брокера отрицательный — поэтому здесь складываем с минусом, чтобы получить
    /// положительный расход). Если результат положителен и превышает порог — деньги поступили, но
    /// не были реинвестированы в той же мере, что и поступили: вероятный кэш "застрял" на счёте.
    /// Скользящее окно (а не "с начала истории") — иначе сигнал никогда не угасал бы после
    /// фактического реинвеста старых поступлений новой покупкой, совершённой давно за пределами
    /// окна. Один кандидат на счёт, привязанный к дате <see cref="SignalEvaluationInput.AsOf"/> —
    /// при повторном прогоне в тот же день с тем же входом ключ дедупликации (Type+Date, без
    /// Position/InstrumentId — здесь оба null) не меняется, что и обеспечивает отсутствие дублей
    /// внутри одного дня; на следующий день естественно появится новый кандидат с новой датой,
    /// что ожидаемо (это ежедневный батч, spec §11).
    /// </summary>
    private static IEnumerable<Signal> UninvestedCashRule(SignalEvaluationInput input)
    {
        var lookbackFrom = input.AsOf.AddDays(-input.Options.UninvestedCashLookbackDays);

        decimal inflow = 0m;
        decimal buys = 0m;

        foreach (var op in input.Operations)
        {
            var opDate = DateOnly.FromDateTime(op.Date);
            if (opDate < lookbackFrom || opDate > input.AsOf) continue;

            switch (op.Type)
            {
                case OperationType.Coupon or OperationType.Amortization or OperationType.Redemption:
                    inflow += op.AmountRub;
                    break;
                case OperationType.Buy:
                    buys += -op.AmountRub; // Buy хранится отрицательным — переводим в положительный расход
                    break;
            }
        }

        var uninvested = inflow - buys;
        if (uninvested <= input.Options.UninvestedCashThresholdRub) yield break;

        yield return new Signal
        {
            AccountId = input.AccountId,
            Type = SignalType.UninvestedCashThreshold,
            Severity = SignalSeverity.Info,
            PositionId = null,
            InstrumentId = null,
            Date = input.AsOf,
            SuggestedAction = $"Незаинвестированный остаток за последние {input.Options.UninvestedCashLookbackDays} дн. ≈ {uninvested:F2} руб. — рассмотрите реинвестирование",
        };
    }

    // ─── Правило 5: доходность ниже сопоставимой по сроку альтернативы (spec §8) ───────────

    /// <summary>
    /// "Сопоставимая по сроку альтернатива" — другой holding в портфеле, чей HorizonDate попадает
    /// в окно ±<see cref="SignalEngineOptions.MaturityWindowDaysForAlternativeComparison"/> дней от
    /// HorizonDate рассматриваемой позиции (самостоятельное решение — задание прямо предлагает
    /// "выбери разумное окно... как параметр"). Сравниваются эффективные доходности
    /// (YtmEffective, либо CurrentYield для флоатера/индексируемой бумаги — те же приоритеты, что
    /// и в остальной аналитике, см. PortfolioHolding); holding без посчитанной доходности
    /// (оба null) пропускается — сравнивать нечего. Если у альтернативы доходность выше текущей
    /// позиции более чем на порог б.п. — сигнал на ХУЖУЮ (текущую) позицию, не на альтернативу.
    /// </summary>
    private static IEnumerable<Signal> YieldBelowAlternativeRule(SignalEvaluationInput input)
    {
        var windowDays = input.Options.MaturityWindowDaysForAlternativeComparison;
        var thresholdFraction = input.Options.YieldBelowAlternativeBpsThreshold / 10_000m; // б.п. → доля

        foreach (var holding in input.Holdings)
        {
            var ownYield = EffectiveYield(holding);
            if (ownYield is null) continue;

            var bestAlternative = input.Holdings
                .Where(other => other.PositionId != holding.PositionId)
                .Where(other => Math.Abs((other.HorizonDate.ToDateTime(TimeOnly.MinValue) - holding.HorizonDate.ToDateTime(TimeOnly.MinValue)).Days) <= windowDays)
                .Select(other => (Holding: other, Yield: EffectiveYield(other)))
                .Where(x => x.Yield is not null)
                .OrderByDescending(x => x.Yield)
                .FirstOrDefault();

            if (bestAlternative.Holding is null || bestAlternative.Yield is null) continue;

            var gap = bestAlternative.Yield.Value - ownYield.Value;
            if (gap <= thresholdFraction) continue;

            yield return new Signal
            {
                AccountId = input.AccountId,
                Type = SignalType.YieldBelowAlternative,
                Severity = SignalSeverity.Info,
                PositionId = holding.PositionId,
                InstrumentId = holding.InstrumentId,
                Date = input.AsOf,
                SuggestedAction = $"Доходность позиции {holding.Name ?? holding.Issuer ?? holding.InstrumentId.ToString()} ({ownYield:P2}) ниже сопоставимой по сроку альтернативы в портфеле ({bestAlternative.Yield:P2}) на {gap * 10_000m:F0} б.п.",
            };
        }
    }

    /// <summary>YTM эффективная, либо текущая доходность для флоатера/индексируемой бумаги (та же конвенция, что в spec §6/§9).</summary>
    private static decimal? EffectiveYield(PortfolioHolding holding) =>
        holding.YtmEffective ?? holding.CurrentYield;

    // ─── Правило 6: нарушение лимита концентрации по эмитенту — Critical (spec §8) ─────────

    private static IEnumerable<Signal> ConcentrationLimitRule(SignalEvaluationInput input)
    {
        if (input.Holdings.Count == 0) yield break;

        var composition = PortfolioCompositionService.Calculate(input.Holdings);
        if (composition.TotalMarketValueRub <= 0m) yield break;

        foreach (var share in composition.ByIssuer)
        {
            var specificLimit = input.TargetAllocations
                .FirstOrDefault(ta => ta.Issuer is not null && string.Equals(ta.Issuer, share.Key, StringComparison.OrdinalIgnoreCase))
                ?.MaxConcentrationPercent;

            var limit = specificLimit ?? input.Options.DefaultMaxConcentrationPercent;
            if (share.SharePercent <= limit) continue;

            // Привязываем сигнал к одной из позиций этого эмитента (первой найденной) — Signal не
            // несёт поля "эмитент" напрямую, только PositionId/InstrumentId; имя эмитента уходит
            // только в SuggestedAction (человекочитаемый текст для UI).
            var representativeHolding = input.Holdings.FirstOrDefault(h => (h.Issuer ?? "Не определено") == share.Key);

            yield return new Signal
            {
                AccountId = input.AccountId,
                Type = SignalType.ConcentrationLimitBreached,
                Severity = SignalSeverity.Critical,
                PositionId = representativeHolding?.PositionId,
                InstrumentId = representativeHolding?.InstrumentId,
                Date = input.AsOf,
                SuggestedAction = $"Доля эмитента «{share.Key}» в портфеле {share.SharePercent:F2}% превышает лимит {limit:F2}%",
            };
        }
    }

    // ─── Правило 7: дрейф дюрации портфеля от целевой — только если задан TargetAllocation (spec §8) ─

    private static IEnumerable<Signal> DurationDriftRule(SignalEvaluationInput input)
    {
        // Без записи TargetAllocation с непустым TargetDurationYears — сигнал не генерируется,
        // без exception (задание явно требует этого как негативный кейс).
        var targetDuration = input.TargetAllocations
            .Where(ta => ta.Issuer is null) // null = лимит/цель по портфелю в целом, см. doc-comment TargetAllocation.Issuer
            .Select(ta => ta.TargetDurationYears)
            .FirstOrDefault(d => d is not null);

        if (targetDuration is null) yield break;

        var weighted = input.Holdings
            .Where(h => h.ModifiedDuration is not null && h.MarketValueRub > 0m)
            .ToList();

        if (weighted.Count == 0) yield break; // ни одной позиции с посчитанной дюрацией — не делим на 0, не генерируем сигнал

        var totalMarketValue = weighted.Sum(h => h.MarketValueRub);
        if (totalMarketValue <= 0m) yield break;

        var portfolioDuration = weighted.Sum(h => h.MarketValueRub * h.ModifiedDuration!.Value) / totalMarketValue;
        var drift = Math.Abs(portfolioDuration - targetDuration.Value);

        if (drift <= input.Options.DurationDriftToleranceYears) yield break;

        yield return new Signal
        {
            AccountId = input.AccountId,
            Type = SignalType.DurationDriftFromTarget,
            Severity = SignalSeverity.Info,
            PositionId = null,
            InstrumentId = null,
            Date = input.AsOf,
            SuggestedAction = $"Модифицированная дюрация портфеля {portfolioDuration:F2} лет отклонилась от целевой {targetDuration.Value:F2} лет на {drift:F2} лет",
        };
    }

    // ─── Правило 8: низкая ликвидность — заглушка, см. doc-comment ─────────────────────────

    /// <summary>
    /// Низкая ликвидность по позиции (тонкий стакан, spec §8) — явно вне функциональной готовности
    /// MVP (см. doc-comment <see cref="Bonds.Core.Models.MarketQuote"/> и комментарий в
    /// <c>BondSyncService</c> рядом с местом, где стакан запрашивается у T-Invest, но не
    /// персистируется: bid/ask существуют только как мгновенный результат живого вызова
    /// <c>ITInvestPortfolioClient.GetQuotesAsync</c>, который Signals Engine как чистый слой не
    /// может сделать сам). Метод принимает опциональные bid/ask по позиции — на сегодняшний день
    /// вызывающий код (Infrastructure) не имеет источника для них на момент прогона цикла сигналов
    /// (они доступны только в момент синка, а не позже), поэтому всегда передаётся null и метод
    /// всегда возвращает пустой список. Сохранён в движке (не удалён), чтобы при появлении
    /// персистентности стакана (этап вне MVP, см. ссылку в MarketQuote) реализация дополнилась
    /// прямо здесь без изменения сигнатуры вызова со стороны Infrastructure.
    /// </summary>
    public static IReadOnlyList<Signal> LowLiquidityWarningRule(
        ulong accountId,
        DateOnly asOf,
        IReadOnlyList<(ulong PositionId, ulong InstrumentId, decimal? BestBid, decimal? BestAsk)> positionsWithOrderBook)
    {
        // Bid/Ask сейчас всегда null на входе (нет персистентности стакана, см. doc-comment выше) —
        // условие ниже намеренно никогда не дополняет результат на MVP, но оставлено явным (а не
        // удалённым/закомментированным), чтобы при появлении реальных bid/ask заработало без правок.
        var signals = new List<Signal>();
        foreach (var (positionId, instrumentId, bestBid, bestAsk) in positionsWithOrderBook)
        {
            if (bestBid is null || bestAsk is null || bestBid <= 0m) continue;

            var spreadFraction = (bestAsk.Value - bestBid.Value) / bestBid.Value;
            const decimal wideSpreadThreshold = 0.02m; // 2% спред — эвристический порог "тонкого стакана", не калиброван

            if (spreadFraction < wideSpreadThreshold) continue;

            signals.Add(new Signal
            {
                AccountId = accountId,
                Type = SignalType.LowLiquidityWarning,
                Severity = SignalSeverity.Info,
                PositionId = positionId,
                InstrumentId = instrumentId,
                Date = asOf,
                SuggestedAction = $"Широкий спред bid/ask ({spreadFraction:P2}) — низкая ликвидность по позиции",
            });
        }

        return signals;
    }
}

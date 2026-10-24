using System.Globalization;
using Bonds.Core.Capital;
using Bonds.Core.Models;
using Bonds.Core.Policy;

namespace Bonds.Core.Execution;

/// <summary>
/// Цены заявок исполнителя — чистые функции. <b>Единицы брокера:</b> облигации T-Invest — ПУНКТЫ (процент номинала, сетка <c>min_price_increment_points</c>;
/// источник — <see cref="TradePlanLine.LimitPricePoints"/>), остальное (фонды, акции) — валюта цены за 1 штуку (сетка <c>min_price_increment</c>; источник —
/// <see cref="TradePlanLine.LimitPrice"/>). Стакан T-Invest приходит в тех же единицах (облигации — пункты), поэтому сравнение дрейфа идёт без конверсий.
/// Все дрейфы — ДОЛИ от УТВЕРЖДЁННОГО лимита.
/// </summary>
public static class OrderPricing
{
    /// <summary>Строка — облигация с ценой в пунктах.</summary>
    public static bool IsPointsPriced(TradePlanLine line) => line.LimitPricePoints is not null;

    /// <summary>Утверждённый лимит строки в единицах брокера (пункты облигации либо валюта за штуку).</summary>
    public static decimal ApprovedLimit(TradePlanLine line) => line.LimitPricePoints ?? line.LimitPrice;

    public static string PriceUnitOf(TradePlanLine line) => IsPointsPriced(line) ? OrderPriceUnits.Points : OrderPriceUnits.Currency;

    /// <summary>Шаг сетки в единицах брокера: облигации — <c>min_price_increment_points</c>, остальное — <c>min_price_increment</c>. null — шаг неизвестен.</summary>
    public static decimal? StepOf(TradePlanLine line, InstrumentTradingMeta meta)
    {
        var step = IsPointsPriced(line) ? meta.MinPriceIncrementPoints : meta.MinPriceIncrement;
        return step is > 0m ? step : null;
    }

    /// <summary>|mid − лимит| / лимит, ДОЛЯ.</summary>
    public static decimal DriftFraction(decimal mid, decimal approvedLimit) =>
        approvedLimit <= 0m ? decimal.MaxValue : Math.Abs(mid - approvedLimit) / approvedLimit;

    /// <summary>
    /// Цена попытки <paramref name="attempt"/> (1 — утверждённый лимит, приведённый к сетке: покупка вниз, продажа вверх). Перевыставление сдвигает цену к противоположной стороне стакана (покупка — к ask,
    /// продажа — к bid) равными долями: попытка k из N — (k−1)/(N−1) пути; цель ограничена <c>лимит × (1 ± drift)</c>. Покупка округляется ВНИЗ к сетке,
    /// продажа — ВВЕРХ; цена не ухудшается относительно <paramref name="previousPrice"/> обратно (покупка не ниже прошлой, продажа не выше) и никогда не
    /// выходит за предел дрейфа. Нет противоположной стороны стакана — цель = предел дрейфа.
    /// </summary>
    public static decimal PriceForAttempt(
        string side, decimal approvedLimit, decimal? bid, decimal? ask, decimal step, decimal maxDriftFraction, int attempt, int maxAttempts, decimal? previousPrice)
    {
        if (attempt <= 1) return LinePricer.RoundToStep(approvedLimit, step, side);

        var isSell = side == PlanSides.Sell;
        var cap = isSell
            ? LinePricer.RoundToStep(approvedLimit * (1m - maxDriftFraction), step, PlanSides.Sell) // продажа: не ниже лимит × (1 − drift), округление вверх
            : LinePricer.RoundToStep(approvedLimit * (1m + maxDriftFraction), step, PlanSides.Buy); // покупка: не выше лимит × (1 + drift), округление вниз
        var opposite = isSell ? bid : ask;
        var target = opposite is > 0m
            ? (isSell ? Math.Max(opposite.Value, cap) : Math.Min(opposite.Value, cap))
            : cap;

        var share = maxAttempts <= 1 ? 1m : Math.Min(1m, (decimal)(attempt - 1) / (maxAttempts - 1));
        var price = LinePricer.RoundToStep(approvedLimit + (target - approvedLimit) * share, step, side);

        if (previousPrice is { } prev) price = isSell ? Math.Min(price, prev) : Math.Max(price, prev);
        price = isSell ? Math.Max(price, cap) : Math.Min(price, cap);
        return price <= 0m ? approvedLimit : price;
    }

    /// <summary>
    /// Сколько денег нужно на покупку <paramref name="lots"/> лотов по цене <paramref name="brokerPrice"/>, в валюте строки: оценка строки
    /// (<c>est_amount</c> — сумма с НКД — плюс <c>est_fee</c>) пропорционально лотам и масштабированная вверх, если цена выше утверждённой
    /// (вниз не масштабируется — консервативно). Единицы цены сокращаются (пункты/пункты или рубли/рубли).
    /// </summary>
    public static decimal RequiredMoney(TradePlanLine line, decimal brokerPrice, long lots) => Scaled(line, line.EstAmount + line.EstFee, brokerPrice, lots);

    /// <summary>Сумма заявки без комиссии (<c>est_amount</c> пропорционально лотам, масштаб вверх при цене выше утверждённой) — для лимита авто на заявку, в валюте строки.</summary>
    public static decimal OrderAmount(TradePlanLine line, decimal brokerPrice, long lots) => Scaled(line, line.EstAmount, brokerPrice, lots);

    private static decimal Scaled(TradePlanLine line, decimal amount, decimal brokerPrice, long lots)
    {
        if (line.QuantityLots <= 0) return 0m;
        var approved = ApprovedLimit(line);
        var priceFactor = approved > 0m ? Math.Max(1m, brokerPrice / approved) : 1m;
        return amount * lots / line.QuantityLots * priceFactor;
    }

    /// <summary>Порядок отправки строк: сначала продажи (освобождают деньги), затем покупки; внутри — по номеру строки.</summary>
    public static IReadOnlyList<TradePlanLine> ExecutionOrder(IEnumerable<TradePlanLine> lines) =>
        lines.OrderBy(l => l.Side == PlanSides.Sell ? 0 : 1).ThenBy(l => l.LineNo).ToList();
}

/// <summary>Вход предполётной проверки одной заявки (все рыночные данные — в единицах брокера, деньги — в валюте строки).</summary>
public sealed record PreflightInput
{
    public required string Side { get; init; }
    public required decimal ApprovedLimit { get; init; }
    public required decimal OrderPrice { get; init; }
    public required long Lots { get; init; }
    public required decimal LotSize { get; init; }
    public required decimal MaxDriftFraction { get; init; }
    public required DateTime NowUtc { get; init; }
    public required TimeSpan MaxQuoteAge { get; init; }

    public decimal? Bid { get; init; }
    public decimal? Ask { get; init; }
    public DateTime? QuoteTimeUtc { get; init; }

    /// <summary><c>GetTradingStatus == NormalTrading</c>; null — статус не получен.</summary>
    public bool? NormalTrading { get; init; }
    public bool LimitOrderAvailable { get; init; }

    /// <summary><c>api_trade_available</c> по брокеру (GetTradingStatus) И по метаданным бумаги.</summary>
    public bool ApiTradeAvailable { get; init; }

    /// <summary>Сторона доступна по метаданным (buy_available / sell_available).</summary>
    public bool SideAvailable { get; init; }

    /// <summary>Покупка: свободные деньги брокера в валюте строки (без заблокированных под уже отправленные заявки). null — неизвестно.</summary>
    public decimal? AvailableMoney { get; init; }

    public decimal RequiredMoney { get; init; }

    /// <summary>Продажа: незаблокированный остаток бумаги, ШТУКИ. null — неизвестно.</summary>
    public decimal? AvailablePieces { get; init; }

    /// <summary>Только для <c>AutoApproved</c>: Σ уже исполняемых сегодня авто-покупок других планов, рубли. null — план не авто.</summary>
    public decimal? AutoUsedTodayRub { get; init; }
    public decimal AutoPlanBuysRub { get; init; }
    public decimal AutoPerDayRub { get; init; }

    /// <summary>Только для <c>AutoApproved</c>: лимит ОДНОЙ заявки, рубли (<see cref="ExecutionLimitsOptions.AutoPerOrderRub"/>). null — план не авто.</summary>
    public decimal? AutoPerOrderRub { get; init; }

    /// <summary>Сумма этой заявки по её цене (оценка строки пропорционально лотам, масштабированная вверх при цене выше утверждённой), в валюте строки, без комиссии.</summary>
    public decimal OrderAmount { get; init; }

    public bool Paused { get; init; }

    /// <summary>До закрытия сессии больше <c>CancelBeforeClose</c> и рынок открыт.</summary>
    public bool SessionOpenForNewOrders { get; init; }
}

/// <summary>Провал проверки: код (стабильный, для тестов и <c>error_code</c>) и текст для человека.</summary>
public sealed record PreflightFailure(string Code, string Message);

/// <summary>
/// Предполётные проверки заявки (задача 65) — чистая функция, ВСЕ проверки до любого вызова отправки. Порядок: пауза → сессия → свежая котировка →
/// дрейф mid от утверждённого лимита → статус торгов → api_trade_available → деньги/бумаги → лимит авто на заявку → суточный лимит авто. Возвращает первую не прошедшую (null — можно отправлять).
/// </summary>
public static class PreflightChecks
{
    public const string Paused = "paused";
    public const string SessionClosing = "session_closing";
    public const string StaleQuote = "stale_quote";
    public const string PriceDrift = "price_drift";
    public const string NotNormalTrading = "not_normal_trading";
    public const string ApiTradeUnavailable = "api_trade_unavailable";
    public const string InsufficientFunds = "insufficient_funds";
    public const string InsufficientPosition = "insufficient_position";
    public const string AutoDayLimit = "auto_day_limit";
    public const string AutoOrderLimit = "auto_order_limit";
    public const string InvalidOrder = "invalid_order";

    public static PreflightFailure? Evaluate(PreflightInput i)
    {
        if (i.Paused) return new(Paused, "Пауза (/stop): заявка не отправлена");
        if (!i.SessionOpenForNewOrders) return new(SessionClosing, "Рынок закрыт или до закрытия сессии меньше допустимого — заявка не отправлена");
        if (i.Lots <= 0 || i.OrderPrice <= 0m || i.ApprovedLimit <= 0m) return new(InvalidOrder, "Некорректная заявка (лоты/цена)");

        if (i.Bid is not > 0m || i.Ask is not > 0m || i.QuoteTimeUtc is null || i.NowUtc - i.QuoteTimeUtc.Value > i.MaxQuoteAge)
            return new(StaleQuote, "Нет свежей котировки (стакан пуст или устарел)");

        var mid = (i.Bid.Value + i.Ask.Value) / 2m;
        var drift = OrderPricing.DriftFraction(mid, i.ApprovedLimit);
        if (drift > i.MaxDriftFraction)
            return new(PriceDrift, string.Create(CultureInfo.InvariantCulture, $"Цена ушла от утверждённого лимита: отклонение mid {drift * 100m:0.##}% больше допустимых {i.MaxDriftFraction * 100m:0.##}%"));
        if (OrderPricing.DriftFraction(i.OrderPrice, i.ApprovedLimit) > i.MaxDriftFraction)
            return new(PriceDrift, "Цена заявки вне допустимого отклонения от утверждённого лимита");

        if (i.NormalTrading != true || !i.LimitOrderAvailable) return new(NotNormalTrading, "Торги по бумаге сейчас не в обычном режиме (не NormalTrading) или лимитные заявки недоступны");
        if (!i.ApiTradeAvailable || !i.SideAvailable) return new(ApiTradeUnavailable, "Бумага недоступна для торговли через API (api_trade_available или сторона сделки)");

        if (i.Side == PlanSides.Buy)
        {
            if (i.AvailableMoney is null || i.AvailableMoney.Value < i.RequiredMoney)
                return new(InsufficientFunds, string.Create(CultureInfo.InvariantCulture, $"Не хватает денег: нужно {i.RequiredMoney:0.##}, свободно {(i.AvailableMoney ?? 0m):0.##}"));
            if (i.AutoPerOrderRub is { } cap && i.OrderAmount > cap)
                return new(AutoOrderLimit, string.Create(CultureInfo.InvariantCulture, $"Лимит авто-исполнения на заявку: {i.OrderAmount:0.##} ₽ больше {cap:0.##} ₽"));
            if (i.AutoUsedTodayRub is { } used && used + i.AutoPlanBuysRub > i.AutoPerDayRub)
                return new(AutoDayLimit, string.Create(CultureInfo.InvariantCulture, $"Суточный лимит авто-исполнения: уже {used:0.##} ₽ + план {i.AutoPlanBuysRub:0.##} ₽ больше {i.AutoPerDayRub:0.##} ₽"));
        }
        else if (i.AvailablePieces is null || i.AvailablePieces.Value < i.Lots * i.LotSize)
        {
            return new(InsufficientPosition, string.Create(CultureInfo.InvariantCulture, $"Не хватает бумаг для продажи: нужно {i.Lots * i.LotSize:0.##} шт., доступно {(i.AvailablePieces ?? 0m):0.##}"));
        }

        return null;
    }
}

/// <summary>Состояние строки плана по её заявкам (все попытки).</summary>
public sealed record LineExecutionState(
    long FilledLots, long RemainingLots, TradeOrder? Active, TradeOrder? Last, bool Terminal, string Status)
{
    /// <summary>Статус строки для UI/итогов: Pending (заявок нет), иначе статус последней заявки; полностью исполнена — Filled.</summary>
    public static LineExecutionState From(TradePlanLine line, IReadOnlyCollection<TradeOrder> orders)
    {
        var filled = orders.Sum(o => o.LotsFilled);
        var remaining = Math.Max(0, line.QuantityLots - filled);
        var last = orders.OrderByDescending(o => o.Attempt).FirstOrDefault();
        var active = last is not null && TradeOrderStatuses.IsActive(last.Status) ? last : null;
        if (last is null) return new(filled, remaining, null, null, false, "Pending");
        if (remaining == 0) return new(filled, 0, active, last, active is null, active is null ? TradeOrderStatuses.Filled : last.Status);

        var terminal = active is null && !(last.Status == TradeOrderStatuses.Cancelled && last.ErrorCode == TradeOrderErrorCodes.Reprice);
        return new(filled, remaining, active, last, terminal, last.Status);
    }
}

/// <summary>Сверка фактической комиссии с моделью плана.</summary>
public static class FeeReconciliation
{
    /// <summary>Модельная комиссия за исполненное: <c>est_fee</c> строки × доля исполненных лотов (в валюте строки).</summary>
    public static decimal ExpectedFee(TradePlanLine line, long filledLots) =>
        line.QuantityLots <= 0 ? 0m : line.EstFee * filledLots / line.QuantityLots;

    /// <summary>Отклонение факта от модели (факт − модель), в валюте строки; <see cref="IsMismatch"/> — по модулю больше порога.</summary>
    public static decimal Deviation(TradePlanLine line, long filledLots, decimal actualFee) => actualFee - ExpectedFee(line, filledLots);

    public static bool IsMismatch(TradePlanLine line, long filledLots, decimal actualFee, decimal threshold) =>
        Math.Abs(Deviation(line, filledLots, actualFee)) > threshold;
}

/// <summary>Итог плана по строкам: все исполнены — Done, что-то исполнено — PartiallyDone, ничего — Failed.</summary>
public static class PlanExecutionOutcome
{
    public static string Resolve(IReadOnlyCollection<(long Requested, long Filled)> lines)
    {
        if (lines.Count > 0 && lines.All(l => l.Filled >= l.Requested)) return PlanStatuses.Done;
        return lines.Any(l => l.Filled > 0) ? PlanStatuses.PartiallyDone : PlanStatuses.Failed;
    }
}

/// <summary>
/// Суточный лимит авто-исполнения по ФАКТУ (задача 65, ревью): исполненное — <c>lots_filled × lot_size × avg_fill_price</c> (рубли, цена ОДНОЙ штуки), ещё не
/// завершённое (план ждёт исполнителя или строка в работе) — оценка <c>est_amount</c> пропорционально неисполненным лотам. Только покупки в RUB и только планы
/// с <c>decided_via = auto</c> (задача 67, #10: ручное подтверждение лимит авто не расходует).
/// </summary>
public static class AutoDayUsage
{
    private static readonly HashSet<string> TerminalPlans = [PlanStatuses.Done, PlanStatuses.PartiallyDone, PlanStatuses.Failed];

    public static decimal SpentRub(IEnumerable<TradePlan> plans, IEnumerable<TradeOrder> orders, ulong? excludePlanId = null)
    {
        var byPlan = orders.ToLookup(o => o.PlanId);
        var total = 0m;
        foreach (var plan in plans)
        {
            if (excludePlanId is { } exclude && plan.Id == exclude) continue;
            if (!string.Equals(plan.DecidedVia, DecidedVia.Auto, StringComparison.Ordinal)) continue; // задача 67 (#10): ручные планы авто-лимит не тратят
            var planTerminal = TerminalPlans.Contains(plan.Status);
            foreach (var line in plan.Lines.Where(l => l.Side == PlanSides.Buy && IsRub(l.Currency)))
            {
                var lineOrders = byPlan[plan.Id].Where(o => o.LineNo == line.LineNo).ToList();
                total += FilledAmount(line, lineOrders) + FilledCommissionRub(lineOrders);
                var state = LineExecutionState.From(line, lineOrders);
                if (!planTerminal && !state.Terminal && line.QuantityLots > 0)
                    total += (line.EstAmount + line.EstFee) * state.RemainingLots / line.QuantityLots; // задача 67 (#12): лимит — с комиссией
            }
        }

        return total;
    }

    /// <summary>Исполненная сумма строки: Σ лоты × лотность × средняя цена штуки (нет цены — утверждённая цена штуки <c>limit_price</c>), в валюте строки.</summary>
    public static decimal FilledAmount(TradePlanLine line, IEnumerable<TradeOrder> orders) =>
        orders.Where(o => o.LotsFilled > 0).Sum(o => o.LotsFilled * line.LotSize * (o.AvgFillPrice ?? line.LimitPrice));

    /// <summary>Фактическая комиссия исполненных заявок строки в рублях (валюта комиссии не указана = валюта строки, т.е. рубли).</summary>
    public static decimal FilledCommissionRub(IEnumerable<TradeOrder> orders) =>
        orders.Where(o => o.LotsFilled > 0 && (o.CommissionCurrency is null || IsRub(o.CommissionCurrency))).Sum(o => Math.Abs(o.ExecutedCommission ?? 0m));

    private static bool IsRub(string currency) => currency.Trim().ToUpperInvariant() is "RUB" or "RUR";
}

/// <summary>
/// Повторная проверка <c>AutoApproved</c>-плана в момент исполнения (ревью задачи 65): то же, что разрешило авто-подтверждение (<see cref="PolicyKit.DecideStatus"/>),
/// на ТЕКУЩИХ настройках — только покупки в RUB, не облигации, каждая строка ≤ лимита на заявку, бумага — член (правило «instrument», T-Invest) корзины с
/// включённой авто-покупкой (дизайн §5: белый список — фонды корзины «Ликвидность»; авто-покупку корзин включает только владелец).
/// Задача 67 (#1): белый список — В КОДЕ (<see cref="AutoWhitelist"/>): корзина обязана иметь код <c>liquidity</c>, бумага — фонд T-Invest по метаданным торгов
/// (метаданные есть, цена не в пунктах) без выплат (<c>pays_distributions = false</c>) либо из <see cref="PolicyOptions.AutoFundWhitelist"/>. Исполнитель
/// вызывает проверку для ЛЮБОГО плана в статусе <c>AutoApproved</c> или с <c>decided_via = auto</c> (подделанный в БД статус не обходит её).
/// </summary>
public static class AutoExecutionWhitelist
{
    /// <summary>
    /// null — план можно исполнять как авто; иначе причина отказа. <paramref name="metaOf"/> — метаданные торгов строки (null — неизвестна: отказ);
    /// <paramref name="policy"/> — конфиг-список фондов (по умолчанию пустой).
    /// </summary>
    public static string? Check(
        TradePlan plan, IReadOnlyCollection<CapitalBasket> baskets, decimal autoPerOrderRub,
        Func<TradePlanLine, InstrumentTradingMeta?> metaOf, PolicyOptions? policy = null)
    {
        policy ??= new PolicyOptions();
        if (plan.Lines.Count == 0) return "в плане нет строк";
        foreach (var line in plan.Lines)
        {
            if (line.Side != PlanSides.Buy) return $"авто-исполнение — только покупки ({line.Ticker})";
            if (!string.Equals(line.Broker?.Trim(), Bonds.Core.Models.Brokers.TInvest, StringComparison.OrdinalIgnoreCase))
                return $"авто-исполнение — только T-Invest ({line.Ticker})";
            if (!string.Equals(line.Currency.Trim(), "RUB", StringComparison.OrdinalIgnoreCase)) return $"авто-исполнение — только рубли ({line.Ticker})";
            if (OrderPricing.IsPointsPriced(line)) return $"облигации исполняются только после подтверждения ({line.Ticker})";
            if (line.EstAmount > autoPerOrderRub)
                return string.Create(CultureInfo.InvariantCulture, $"заявка {line.Ticker} на {line.EstAmount:0.##} ₽ больше лимита авто {autoPerOrderRub:0.##} ₽");
            if (!AutoWhitelist.IsLiquidityMember(baskets, line.InstrumentKey, line.Ticker))
                return $"бумага {line.Ticker} не входит в корзину с авто-покупкой «{AutoWhitelist.BasketCode}»";

            // Класс «фонд»: у T-Invest в метаданных торгов только облигации (шаг в пунктах) и фонды (EtfBy) — метаданные есть и цена не в пунктах.
            var meta = metaOf(line);
            if (meta is null || meta.MinPriceIncrementPoints is > 0m) return $"бумага {line.Ticker} не фонд по метаданным торгов — авто только для фондов";
            if (AutoWhitelist.DistributionsRejection(line.Ticker, meta.PaysDistributions, policy) is { } distributions) return distributions;
        }

        return null;
    }
}

/// <summary>Средняя цена исполнения ОДНОЙ штуки (ревью задачи 65 — семантика полей T-Invest разная у ответа на отправку заявки и у <c>OrderState</c>).</summary>
public static class FillPrice
{
    /// <summary>
    /// Есть ИТОГОВАЯ исполненная сумма заявки (<paramref name="executedAmount"/>, валюта) — средняя = сумма / (лоты × лотность), штуки; иначе
    /// <paramref name="perPieceFallback"/> (цена штуки от брокера). Нет исполнения — null.
    /// </summary>
    public static decimal? PerPiece(decimal? executedAmount, long lotsExecuted, decimal lotSize, decimal? perPieceFallback)
    {
        if (lotsExecuted <= 0) return null;
        if (executedAmount is > 0m && lotSize > 0m) return executedAmount.Value / (lotsExecuted * lotSize);
        return perPieceFallback;
    }
}

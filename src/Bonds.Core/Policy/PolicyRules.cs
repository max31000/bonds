using BrokerIds = Bonds.Core.Models.Brokers;
using Bonds.Core.Capital;
using Bonds.Core.Fees;
using Bonds.Core.Models;

namespace Bonds.Core.Policy;

/// <summary>
/// ДАННЫЕ ПЛАНОВ, НЕ ЗАЯВКИ (задача 63). Правила ниже — чистые функции <c>(PolicyInput) → PolicyOutcome</c>: из состояния портфеля строят черновик плана
/// (строки = будущие лимитные заявки). В этом пространстве имён НЕТ вызовов брокеров и отправки заявок — исполнитель появится в фазе 3. Все суммы — в
/// валюте портфеля, доли — ДОЛИ, цены — по стакану, округлённые к шагу (покупка вниз, продажа вверх, облигации — по сетке в пунктах).
/// </summary>
internal static class PolicyNames
{
    public const string Liquidity = "liquidity";
    public const string GlobalCore = "global_core";
}

/// <summary>P1 — парковка свободных рублей T-Invest в корзину «ликвидность» (по <c>sub_weight</c>).</summary>
public static class SweepRule
{
    public const string TriggerText = "Свободные рубли T-Invest не меньше цены лота корзины «Ликвидность»";

    public static PolicyOutcome Evaluate(PolicyInput input)
    {
        var notes = new List<string>();
        var skipped = new List<PlanSkip>();
        if (input.Broker != BrokerIds.TInvest) return PolicyOutcome.None("P1 — только портфель T-Invest");

        var basket = input.BasketByCode(PolicyNames.Liquidity);
        if (basket is null) return PolicyOutcome.None("P1: нет корзины «liquidity»");
        if (basket.FillRule == BasketFillRules.Hold) return PolicyOutcome.None("P1: корзина «liquidity» в режиме hold");
        // Задача 67 (#7): устаревшие данные или неполная оценка ликвидности/свободных денег — решение «выше цели или нет» и сама сумма ненадёжны.
        if (PolicyDataGate.LiquidityProblem(input) is { } p1Data) return PolicyOutcome.None($"P1: {p1Data} — кэш не паркуется");
        // Задача 67 (#3): открыт план вывода (или не прошло WithdrawHoldTtl с его исполнения) — деньги ждут ручного вывода в банк, парковать нельзя.
        if (input.CashHoldActive)
            return PolicyOutcome.None("P1: деньги удержаны под вывод (открыт план вывода или он исполнен недавно) — не паркуются; снять удержание: /withdrawn или «Деньги выведены»");
        if (input.FreeCash <= 0m) return PolicyOutcome.None("P1: свободных рублей нет");

        // Корзина уже выше цели (Above/HardAbove): парковать в неё дальше нельзя — кэш остаётся свободным (его разберёт P2/P3b/P4 или владелец).
        if (input.RowByCode(PolicyNames.Liquidity) is { State: BasketState.Above or BasketState.HardAbove } liquidityRow)
            return PolicyOutcome.None($"P1: корзина «liquidity» выше цели ({liquidityRow.State}) — кэш не паркуется, остаётся свободным");
        if (basket.MinTrade > 0m && input.FreeCash < basket.MinTrade) return PolicyOutcome.None("P1: свободных рублей меньше минимальной сделки корзины");

        var lines = PolicyKit.BuyMembers(input, basket.Members, input.FreeCash, "P1 парковка кэша", skipped);
        if (lines.Count == 0)
        {
            notes.Add("P1: свободных рублей не хватает даже на один лот или бумаги пропущены");
        }

        return PolicyKit.Finish(input, PlanRules.P1Sweep, TriggerText, lines, autoAllowed: basket.AutoBuy, skipped, notes);
    }
}

/// <summary>Распределение суммы по корзинам по дефициту до цели (общая часть P2 и ручного «распределить сумму»).</summary>
public static class InflowRule
{
    /// <summary>
    /// P2 — деньги идут в корзины пропорционально <c>deficitToTarget</c> (корзины <c>hold</c> не получают). Если сумма больше суммарного дефицита, дефициты закрываются,
    /// остаток делится по целям. <c>fixed</c> → члены по под-весам, <c>engine</c> → кандидаты <c>CashAllocationService</c> (облигационная часть делает ВЕСЬ план
    /// <c>PendingApproval</c>: авто только при чисто «фондовом» плане и включённом <c>auto_buy</c> каждой затронутой корзины). Сумма ограничена свободными деньгами.
    /// </summary>
    public static PolicyOutcome Evaluate(PolicyInput input, decimal amount, string trigger, string rule = PlanRules.P2Inflow)
    {
        var notes = new List<string>();
        var skipped = new List<PlanSkip>();
        if (amount <= 0m) return PolicyOutcome.None("P2: сумма должна быть положительной");
        // Задача 67 (#7): деление по дефициту требует полной и свежей оценки корзин.
        if (PolicyDataGate.Problem(input) is { } p2Data) return PolicyOutcome.None($"P2: {p2Data} — распределение по дефициту корзин не строится");

        var budget = Math.Min(amount, input.FreeCash);
        if (budget <= 0m) return PolicyOutcome.None("P2: свободных денег нет");
        if (budget < amount) notes.Add($"Сумма ограничена свободными деньгами: {budget:0.##} из {amount:0.##} {input.Currency}");

        var shares = Shares(input, budget, notes);
        if (shares.Count == 0) return PolicyOutcome.None(notes.Append("P2: нет корзин, которым можно направить деньги").ToArray());

        var lines = new List<TradePlanLine>();
        var autoAllowed = true;
        foreach (var (basket, share) in shares)
        {
            if (share <= 0m || (basket.MinTrade > 0m && share < basket.MinTrade))
            {
                notes.Add($"«{basket.Title}»: доля {share:0.##} {input.Currency} меньше минимальной сделки");
                continue;
            }

            if (basket.FillRule == BasketFillRules.Engine)
            {
                autoAllowed = false;
                if (input.Portfolio != CapitalPortfolios.Rub)
                {
                    notes.Add($"«{basket.Title}»: облигационный движок есть только в рублёвом портфеле");
                    continue;
                }

                var bond = PolicyKit.BuyBonds(input, share, null, $"{rule} «{basket.Title}»", skipped, applyIssuerLimit: false);
                if (bond.Count == 0) notes.Add($"«{basket.Title}»: кандидатов под {share:0.##} {input.Currency} не нашлось");
                lines.AddRange(bond);
            }
            else
            {
                if (!basket.AutoBuy) autoAllowed = false;
                var fixedLines = PolicyKit.BuyMembers(input, basket.Members, share, $"{rule} «{basket.Title}»", skipped);
                if (fixedLines.Count == 0) notes.Add($"«{basket.Title}»: доля {share:0.##} {input.Currency} не покрывает лот");
                lines.AddRange(fixedLines);
            }
        }

        return PolicyKit.Finish(input, rule, trigger, lines, autoAllowed, skipped, notes);
    }

    /// <summary>Доли суммы по корзинам (только с целью, не hold): по дефициту; избыток суммы над суммарным дефицитом — по целям.</summary>
    internal static List<(CapitalBasket Basket, decimal Share)> Shares(PolicyInput input, decimal budget, List<string> notes)
    {
        var eligible = new List<(CapitalBasket Basket, decimal Deficit, decimal Target)>();
        foreach (var row in input.Drift.Rows.Where(r => !r.IsSystem && r.Target is > 0m))
        {
            var basket = input.Baskets.FirstOrDefault(b => b.Id == row.BasketId);
            if (basket is null || basket.FillRule == BasketFillRules.Hold) continue;
            eligible.Add((basket, Math.Max(0m, row.DeficitToTarget ?? 0m), row.Target!.Value));
        }

        if (eligible.Count == 0) return [];

        var totalDeficit = eligible.Sum(e => e.Deficit);
        var result = new List<(CapitalBasket, decimal)>();
        if (totalDeficit >= budget && totalDeficit > 0m)
        {
            foreach (var e in eligible.Where(e => e.Deficit > 0m)) result.Add((e.Basket, budget * e.Deficit / totalDeficit));
            return result;
        }

        var surplus = budget - totalDeficit;
        var targetSum = eligible.Sum(e => e.Target);
        foreach (var e in eligible)
        {
            var share = e.Deficit + (targetSum > 0m ? surplus * e.Target / targetSum : 0m);
            if (share > 0m) result.Add((e.Basket, share));
        }

        if (surplus > 0m) notes.Add("Сумма больше суммарного дефицита до целей: остаток распределён по целевым долям");
        return result;
    }
}

/// <summary>Общая часть продаж из корзины (P3b, P4).</summary>
internal static class BasketSell
{
    /// <summary>
    /// Продажа на <paramref name="grossTarget"/> (валюта портфеля) из позиций корзины пропорционально стоимости, целыми лотами вниз.
    /// Возвращает строки продажи и чистую выручку (после комиссии и налога) в валюте портфеля.
    /// </summary>
    public static (List<TradePlanLine> Lines, decimal Net, bool TaxUnknown) Sell(
        PolicyInput input, string basketCode, decimal grossTarget, string reason, List<PlanSkip> skipped)
    {
        var lines = new List<TradePlanLine>();
        var net = 0m;
        var taxUnknown = false;
        var holdings = input.Holdings.Where(h => h.BasketCode == basketCode && h.QuantityPieces > 0m).ToList();
        var total = holdings.Sum(h => h.MarketValue);
        if (total <= 0m) return (lines, 0m, false);

        foreach (var h in holdings)
        {
            var q = input.FindQuote(h.InstrumentKey) ?? input.FindQuote(h.Ticker);
            if (q is null) { skipped.Add(new PlanSkip(h.Ticker, "нет торговых метаданных или стакана")); continue; }
            if (!q.SellAvailable) { skipped.Add(new PlanSkip(h.Ticker, "продажа недоступна у брокера")); continue; }
            if (!PolicyKit.MarketOpen(input, q)) { skipped.Add(new PlanSkip(h.Ticker, "рынок закрыт")); continue; }
            var p = LinePricer.TryPrice(q, PlanSides.Sell, out var why, input.Options);
            if (p is null) { skipped.Add(new PlanSkip(h.Ticker, why ?? "нет цены")); continue; }

            var perLot = p.DirtyPerPiece * q.Lot * q.FxToPortfolio;
            if (perLot <= 0m) continue;
            var maxLots = (long)Math.Floor(h.QuantityPieces / q.Lot);
            var lots = Math.Min(maxLots, (long)Math.Floor(grossTarget * (h.MarketValue / total) / perLot));
            if (lots <= 0) continue;

            var (line, lineNet, known) = PolicyKit.BuildSell(input, h, q, p, lots, reason);
            taxUnknown |= !known;
            net += lineNet * q.FxToPortfolio;
            lines.Add(line);
        }

        return (lines, net, taxUnknown);
    }
}

/// <summary>
/// P3b — перелив излишка ликвидности в облигации (раз в неделю). Срабатывает при состоянии корзины «ликвидность» Above/HardAbove и весе выше порога. Кандидаты как у P2
/// плюс: after-tax доходность ≥ ожидаемой after-tax доходности фонда + премия (ДОЛЯ, опция), лимит эмитента. Нет подходящих — плана нет («замены нет»). Всегда PendingApproval.
/// </summary>
public static class LiquidityToBondsRule
{
    public static string TriggerFor(DateTime nowUtc)
    {
        var msk = TradingHours.ToMoscow(nowUtc);
        var week = System.Globalization.ISOWeek.GetWeekOfYear(msk);
        return $"Ликвидность выше цели (неделя {System.Globalization.ISOWeek.GetYear(msk)}-W{week:00})";
    }

    public static PolicyOutcome Evaluate(PolicyInput input)
    {
        var notes = new List<string>();
        var skipped = new List<PlanSkip>();
        if (input.Portfolio != CapitalPortfolios.Rub) return PolicyOutcome.None("P3b — только рублёвый портфель");
        if (PolicyDataGate.Problem(input) is { } p3Data) return PolicyOutcome.None($"P3b: {p3Data} — перелив не строится");

        var row = input.RowByCode(PolicyNames.Liquidity);
        var basket = input.BasketByCode(PolicyNames.Liquidity);
        if (row is null || basket is null) return PolicyOutcome.None("P3b: нет корзины «liquidity»");
        if (row.State is not (BasketState.Above or BasketState.HardAbove) || row.Weight <= input.Options.LiquidityOverflowWeight)
            return PolicyOutcome.None("P3b: ликвидность не превышает цель и порог");

        var excess = row.Value - (row.Target ?? 0m) * input.Drift.BaseValue;
        if (excess <= 0m || (basket.MinTrade > 0m && excess < basket.MinTrade)) return PolicyOutcome.None("P3b: излишка до цели нет");

        if (input.LiquidityFundAfterTaxYield is not { } fundYield)
            return PolicyOutcome.None("P3b: нет оценки after-tax доходности фонда ликвидности — сравнивать не с чем");
        var floor = fundYield + input.Options.LiquidityPremiumFraction;

        var (sells, net, taxUnknown) = BasketSell.Sell(input, PolicyNames.Liquidity, excess, "P3b: излишек ликвидности сверх цели", skipped);
        if (sells.Count == 0) return PolicyOutcome.None(notes.Append("P3b: нечего продавать из ликвидности (нет цены или лота)").ToArray());

        var buys = PolicyKit.BuyBonds(input, net, floor, "P3b замена ликвидности", skipped, applyIssuerLimit: true);
        if (buys.Count == 0)
        {
            return PolicyOutcome.None(notes.Append($"P3b: замены нет — нет облигаций с доходностью после налога ≥ {floor:P2} (фонд {fundYield:P2} + премия {input.Options.LiquidityPremiumFraction:P2})").ToArray());
        }

        var warnings = new List<string>();
        if (taxUnknown) warnings.Add("Налог продажи оценён сверху (журнал сделок по части позиций неполон)");
        var lines = sells.Concat(buys).ToList();
        notes.Add($"Порог доходности: фонд ликвидности {fundYield:P2} после налога + премия {input.Options.LiquidityPremiumFraction:P2}");
        return PolicyKit.Finish(input, PlanRules.P3bLiqToBonds, TriggerFor(input.NowUtc), lines, autoAllowed: false, skipped, notes, warnings);
    }
}

/// <summary>P4 — ребаланс: только корзины в HardAbove/HardBelow без hold. Продаём излишек (если у корзины включён auto_sell) и докупаем дефицит. Всегда PendingApproval.</summary>
public static class RebalanceRule
{
    public static string TriggerFor(IEnumerable<string> codes) => $"Жёсткое отклонение корзин: {string.Join(", ", codes.OrderBy(c => c, StringComparer.Ordinal))}";

    /// <summary>Корзины, которые P4 рассматривает: не системные, HardAbove/HardBelow, не hold.</summary>
    private static List<(BasketDriftRow Row, CapitalBasket Basket)> HardRows(PolicyInput input)
    {
        var rows = new List<(BasketDriftRow Row, CapitalBasket Basket)>();
        foreach (var row in input.Drift.Rows.Where(r => !r.IsSystem && r.State is BasketState.HardAbove or BasketState.HardBelow))
        {
            var basket = input.Baskets.FirstOrDefault(b => b.Id == row.BasketId);
            if (basket is null || basket.FillRule == BasketFillRules.Hold) continue;
            rows.Add((row, basket));
        }

        return rows;
    }

    /// <summary>Причина (trigger) плана P4 для входа без построения плана (раннер проверяет дубликат ДО дорогого сбора облигационных данных); null — жёстких отклонений нет.</summary>
    public static string? TriggerFor(PolicyInput input)
    {
        var rows = HardRows(input);
        return rows.Count == 0 ? null : TriggerFor(rows.Select(r => r.Basket.Code));
    }

    public static PolicyOutcome Evaluate(PolicyInput input)
    {
        var notes = new List<string>();
        var skipped = new List<PlanSkip>();
        if (PolicyDataGate.Problem(input) is { } p4Data) return PolicyOutcome.None($"P4: {p4Data} — ребаланс не строится");
        var rows = HardRows(input);
        if (rows.Count == 0) return PolicyOutcome.None("P4: жёстких отклонений нет");

        var lines = new List<TradePlanLine>();
        var net = 0m;
        var taxUnknown = false;
        foreach (var (row, basket) in rows.Where(r => r.Row.State == BasketState.HardAbove))
        {
            if (!basket.AutoSell)
            {
                notes.Add($"«{basket.Title}»: выше цели, но продажа излишка не разрешена (auto_sell выключен)");
                continue;
            }

            var excess = -(row.DeficitToTarget ?? 0m);
            var (sells, sellNet, unknown) = BasketSell.Sell(input, basket.Code, excess, $"P4 ребаланс «{basket.Title}»: излишек", skipped);
            lines.AddRange(sells);
            net += sellNet;
            taxUnknown |= unknown;
        }

        var below = rows.Where(r => r.Row.State == BasketState.HardBelow).ToList();
        var deficitSum = below.Sum(r => Math.Max(0m, r.Row.DeficitToTarget ?? 0m));
        var budget = input.FreeCash + net;
        var autoAllowed = false; // P4 всегда PendingApproval
        if (below.Count > 0 && deficitSum > 0m && budget > 0m)
        {
            foreach (var (row, basket) in below)
            {
                var deficit = Math.Max(0m, row.DeficitToTarget ?? 0m);
                var share = deficitSum <= budget ? deficit : budget * deficit / deficitSum;
                if (share <= 0m || (basket.MinTrade > 0m && share < basket.MinTrade)) continue;

                if (basket.FillRule == BasketFillRules.Engine)
                {
                    if (input.Portfolio == CapitalPortfolios.Rub)
                        lines.AddRange(PolicyKit.BuyBonds(input, share, null, $"P4 ребаланс «{basket.Title}»: дефицит", skipped, applyIssuerLimit: false));
                    else notes.Add($"«{basket.Title}»: облигационный движок есть только в рублёвом портфеле");
                }
                else
                {
                    lines.AddRange(PolicyKit.BuyMembers(input, basket.Members, share, $"P4 ребаланс «{basket.Title}»: дефицит", skipped));
                }
            }
        }
        else if (below.Count > 0)
        {
            notes.Add("P4: есть недобор, но нет денег и продаваемого излишка");
        }

        var warnings = new List<string>();
        if (taxUnknown) warnings.Add("Налог продажи оценён сверху (журнал сделок по части позиций неполон)");
        return PolicyKit.Finish(input, PlanRules.P4Rebalance, TriggerFor(rows.Select(r => r.Basket.Code)), lines, autoAllowed, skipped, notes, warnings);
    }
}

/// <summary>
/// P5 — вывод средств. Сумма «к выводу после налога и комиссии»: сначала свободные деньги, затем продажи по <c>liquidity_tier</c> 0 → 1 → 2 (корзины hold и tier ≥ 3 — только
/// если код указан в <c>includeBaskets</c>); внутри тира — по возрастанию ставки налога (налог ÷ выручка, сначала убыточные позиции). Налог — <see cref="Bonds.Core.Analytics.SaleTaxEstimator"/>
/// по СРЕДНЕЙ цене входа (журнал не хранит FIFO-лоты: расхождение с фактом брокера возможно). Всегда PendingApproval.
/// </summary>
public static class WithdrawRule
{
    public const decimal FreedomMinWithdrawFeeUsd = 20m;

    public static PolicyOutcome Evaluate(PolicyInput input, decimal? amount, decimal? fraction, IReadOnlyCollection<string>? includeBaskets)
    {
        var notes = new List<string>();
        var skipped = new List<PlanSkip>();
        var warnings = new List<string>();

        var requested = amount ?? (fraction is { } f ? f * input.Drift.TotalValue : 0m);
        if (requested <= 0m) return PolicyOutcome.None("P5: сумма вывода должна быть положительной");

        var fromCash = Math.Min(Math.Max(0m, input.FreeCash), requested);
        var remaining = requested - fromCash;
        if (fromCash > 0m) notes.Add($"Из свободных денег: {fromCash:0.##} {input.Currency}");

        var included = new HashSet<string>(includeBaskets ?? [], StringComparer.Ordinal);
        var lines = new List<TradePlanLine>();
        var taxUnknown = false;

        if (remaining > 0m)
        {
            var candidates = new List<(PolicyHolding Holding, PolicyQuote Quote, PricedQuote Price, int Tier, decimal Rate)>();
            foreach (var h in input.Holdings.Where(h => h.QuantityPieces > 0m))
            {
                var basket = input.BasketByCode(h.BasketCode);
                if (basket is null) continue; // вне корзин (unassigned/fx) не трогаем
                var explicitly = included.Contains(basket.Code);
                if (!explicitly && (basket.FillRule == BasketFillRules.Hold || basket.LiquidityTier > 2)) continue;

                var q = input.FindQuote(h.InstrumentKey) ?? input.FindQuote(h.Ticker);
                if (q is null) { skipped.Add(new PlanSkip(h.Ticker, "нет торговых метаданных или стакана")); continue; }
                if (!q.SellAvailable) { skipped.Add(new PlanSkip(h.Ticker, "продажа недоступна у брокера")); continue; }
                if (!PolicyKit.MarketOpen(input, q)) { skipped.Add(new PlanSkip(h.Ticker, "рынок закрыт")); continue; }
                var p = LinePricer.TryPrice(q, PlanSides.Sell, out var why, input.Options);
                if (p is null) { skipped.Add(new PlanSkip(h.Ticker, why ?? "нет цены")); continue; }

                var maxLots = (long)Math.Floor(h.QuantityPieces / q.Lot);
                if (maxLots <= 0) continue;
                var (amountAll, feeAll) = PolicyKit.AmountAndFee(input, q, p, maxLots);
                var (taxAll, _) = PolicyKit.SaleTax(h, amountAll - feeAll, maxLots * q.Lot);
                var rate = amountAll > 0m ? taxAll / amountAll : 0m;
                candidates.Add((h, q, p, basket.LiquidityTier, rate));
            }

            foreach (var c in candidates.OrderBy(c => c.Tier).ThenBy(c => c.Rate).ThenByDescending(c => c.Holding.MarketValue).ThenBy(c => c.Holding.Ticker, StringComparer.Ordinal))
            {
                if (remaining <= 0m) break;
                var maxLots = (long)Math.Floor(c.Holding.QuantityPieces / c.Quote.Lot);
                var lots = PolicyKit.LotsForNet(input, c.Holding, c.Quote, c.Price, remaining, maxLots);
                if (lots <= 0) continue;

                var (line, lineNet, known) = PolicyKit.BuildSell(input, c.Holding, c.Quote, c.Price, lots, $"P5 вывод: тир {c.Tier}, налог ≈ {c.Rate:P1} выручки");
                taxUnknown |= !known;
                lines.Add(line);
                remaining -= lineNet * c.Quote.FxToPortfolio;
            }
        }

        decimal? shortfall = remaining > 0.005m ? remaining : null;
        if (shortfall is { } s) warnings.Add($"Не хватает {s:0.##} {input.Currency}: продавать больше нечего в разрешённых корзинах");
        if (taxUnknown) warnings.Add("Налог оценён сверху (журнал сделок по части позиций неполон)");
        if (input.Broker == BrokerIds.Freedom) warnings.Add($"Freedom: минимальная комиссия вывода ${FreedomMinWithdrawFeeUsd:0} — учтите при выводе, в сумму она не включена");
        warnings.Add("Налог — оценка по средней цене входа (не FIFO), плоские 13%, без сальдирования и ЛДВ");

        var trigger = fraction is { } fr && amount is null
            ? $"Вывод {fr:P0} портфеля ({requested:0.##} {input.Currency})"
            : $"Вывод {requested:0.##} {input.Currency}";

        if (lines.Count == 0 && remaining <= 0m)
        {
            notes.Add("Вся сумма покрывается свободными деньгами — продажи не нужны");
            return new PolicyOutcome(null, notes);
        }

        // Удержание под вывод (задача 67, #3) — сумма, которую владелец выведет: запрошенное за вычетом нехватки.
        var cashHold = Math.Max(0m, requested - (shortfall ?? 0m));
        return PolicyKit.Finish(input, PlanRules.P5Withdraw, trigger, lines, autoAllowed: false, skipped, notes, warnings, requested, shortfall, cashHold);
    }
}

/// <summary>Выход из корзины без цели (обычно «временные»): продажа всех членов, целыми лотами. Всегда PendingApproval.</summary>
public static class ExitBasketRule
{
    public static PolicyOutcome Evaluate(PolicyInput input, string code)
    {
        var notes = new List<string>();
        var skipped = new List<PlanSkip>();
        var basket = input.BasketByCode(code);
        if (basket is null) return PolicyOutcome.None($"Выход: корзина «{code}» не найдена");

        var lines = new List<TradePlanLine>();
        var taxUnknown = false;
        foreach (var h in input.Holdings.Where(h => h.BasketCode == basket.Code && h.QuantityPieces > 0m))
        {
            var q = input.FindQuote(h.InstrumentKey) ?? input.FindQuote(h.Ticker);
            if (q is null) { skipped.Add(new PlanSkip(h.Ticker, "нет торговых метаданных или стакана")); continue; }
            if (!q.SellAvailable) { skipped.Add(new PlanSkip(h.Ticker, "продажа недоступна у брокера")); continue; }
            if (!PolicyKit.MarketOpen(input, q)) { skipped.Add(new PlanSkip(h.Ticker, "рынок закрыт")); continue; }
            var p = LinePricer.TryPrice(q, PlanSides.Sell, out var why, input.Options);
            if (p is null) { skipped.Add(new PlanSkip(h.Ticker, why ?? "нет цены")); continue; }

            var lots = (long)Math.Floor(h.QuantityPieces / q.Lot);
            if (lots <= 0)
            {
                skipped.Add(new PlanSkip(h.Ticker, "остаток меньше одного лота"));
                continue;
            }

            if (h.QuantityPieces > lots * q.Lot) notes.Add($"{h.Ticker}: остаток {h.QuantityPieces - lots * q.Lot:0.##} шт. меньше лота — не продаётся");
            var (line, _, known) = PolicyKit.BuildSell(input, h, q, p, lots, $"Выход из корзины «{basket.Title}»");
            taxUnknown |= !known;
            lines.Add(line);
        }

        var warnings = new List<string>();
        if (taxUnknown) warnings.Add("Налог оценён сверху (журнал сделок по части позиций неполон)");
        warnings.Add("Налог — оценка по средней цене входа (не FIFO), плоские 13%, без сальдирования и ЛДВ");
        // Задача 67 (#3, «P5 / exit-basket»): чистая выручка выхода удерживается под вывод — P1 не паркует её обратно, пока владелец не отметит «деньги выведены».
        return PolicyKit.Finish(input, PlanRules.Manual, $"Выход из корзины «{basket.Title}»", lines, autoAllowed: false, skipped, notes, warnings, holdSaleProceeds: true);
    }
}

/// <summary>
/// P7 — доливка Freedom: свободные USD ≥ порога (дефолт $50) → покупка <c>global_core</c>. Сумма до точки безубыточности <see cref="FreedomFeeModel.CheaperRoute"/>
/// (≈ $315.79) — <c>SPYM.US</c> (ITS-фонды 0.5%), больше — <c>VUAA.EU</c> (0.12% + $1.2). Всегда PendingApproval (авто на Freedom = 0).
/// </summary>
public static class FreedomTopUpRule
{
    public const string TriggerText = "Свободные доллары на Freedom не меньше порога доливки";

    public static PolicyOutcome Evaluate(PolicyInput input)
    {
        var notes = new List<string>();
        var skipped = new List<PlanSkip>();
        if (input.Broker != BrokerIds.Freedom) return PolicyOutcome.None("P7 — только портфель Freedom");
        if (input.FreeCash < input.Options.MinFreedomTopUpUsd) return PolicyOutcome.None($"P7: свободных долларов меньше ${input.Options.MinFreedomTopUpUsd:0}");
        if (input.BasketByCode(PolicyNames.GlobalCore) is null) return PolicyOutcome.None("P7: нет корзины «global_core»");

        // Маршрут выбираем по РЕАЛЬНЫМ комиссиям FreedomFeeModel обоих маршрутов для фактической суммы заявки (весь свободный остаток) и тарифу счёта:
        // на Investor ITS-фонды дешевле при любой сумме (CheaperRoute знает только Standard). При равенстве комиссий с точностью до цента — CheaperRoute.
        var routes = new List<(string Ticker, FreedomMarket Market, PolicyQuote Quote, PricedQuote Price, decimal Fee)>();
        foreach (var (ticker, market) in new[]
                 {
                     (input.Options.FreedomSmallTopUpTicker, FreedomMarket.KazakhstanItsFunds),
                     (input.Options.FreedomLargeTopUpTicker, FreedomMarket.Europe),
                 })
        {
            var q = input.FindQuote(ticker);
            if (q is null) { notes.Add($"P7: нет торговых метаданных или стакана {ticker}"); continue; }
            var priced = LinePricer.TryPrice(q, PlanSides.Buy, out var why, input.Options);
            if (priced is null) { notes.Add($"P7: {ticker} — {why}"); continue; }

            if (PolicyKit.MaxAffordableLots(input, q, priced, input.FreeCash, market) <= 0)
            {
                notes.Add($"P7: свободных долларов не хватает на один лот {ticker}");
                continue;
            }

            var pieces = Math.Floor(input.FreeCash / (priced.DirtyPerPiece * q.FxToPortfolio));
            var feeUsd = FreedomFeeModel.Fee(input.Fees.FreedomTariffId, market, input.FreeCash, pieces);
            routes.Add((ticker, market, q, priced, feeUsd));
        }

        if (routes.Count == 0) return PolicyOutcome.None(notes.ToArray());

        var best = routes.OrderBy(r => r.Fee).First();
        if (routes.Count > 1 && routes.All(r => r.Fee == best.Fee)) best = routes.First(r => r.Market == FreedomFeeModel.CheaperRoute(input.FreeCash));
        var (chosenTicker, chosenMarket, chosenQuote, chosenPrice, _) = best;

        // Доступность и часы проверяем у ВЫБРАННОГО (самого дешёвого) маршрута: дорогой запасной вариант молча не подставляем.
        if (!chosenQuote.BuyAvailable) return PolicyOutcome.None(notes.Append($"P7: покупка {chosenTicker} недоступна").ToArray());
        if (!PolicyKit.MarketOpen(input, chosenQuote)) return PolicyOutcome.None(notes.Append($"P7: рынок {chosenTicker} закрыт").ToArray());

        var lots = PolicyKit.MaxAffordableLots(input, chosenQuote, chosenPrice, input.FreeCash, chosenMarket);

        var route = chosenMarket == FreedomMarket.KazakhstanItsFunds ? "ITS-фонды 0.5%" : "Europe 0.12% + $1.2";
        var line = PolicyKit.BuildLine(input, chosenQuote, chosenPrice, PlanSides.Buy, lots, $"P7 доливка global_core: {chosenTicker} ({route})", freedomMarket: chosenMarket);
        notes.Add($"Маршрут комиссии: {route} (сравнены комиссии обоих маршрутов по тарифу {input.Fees.FreedomTariffId}: "
                  + string.Join(", ", routes.Select(r => $"{r.Ticker} ${r.Fee:0.00}")) + $"); точка безубыточности Standard ≈ ${FreedomFeeModel.BreakEvenAmountUsd:0.00}");
        return PolicyKit.Finish(input, PlanRules.P7FreedomTopUp, TriggerText, [line], autoAllowed: false, skipped, notes);
    }
}

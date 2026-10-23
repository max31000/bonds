using BrokerIds = Bonds.Core.Models.Brokers;
using Bonds.Core.Analytics;
using Bonds.Core.Capital;
using Bonds.Core.Fees;
using Bonds.Core.Models;

namespace Bonds.Core.Policy;

/// <summary>Цена заявки. <see cref="PricePerPiece"/> — ЧИСТАЯ цена за штуку в валюте цены; <see cref="Points"/> — облигации: цена в ПУНКТАХ; <see cref="DirtyPerPiece"/> — с НКД.</summary>
public sealed record PricedQuote(decimal PricePerPiece, decimal? Points, decimal DirtyPerPiece);

/// <summary>
/// Цена лимитной заявки по стакану (как <c>_resolve_limit</c> эталона <c>tn</c>): mid = (bid+ask)/2, округлённый к шагу — покупка ВНИЗ, продажа ВВЕРХ.
/// Нет bid или ask — цену не строим (мид по одной стороне вводил бы в заблуждение). Облигации T-Invest считаются по сетке в ПУНКТАХ
/// (<c>min_price_increment_points</c>): рубли за штуку = пункты / 100 × номинал; рублёвый шаг у облигаций с нерублёвым номиналом NULL, поэтому для
/// них рубли берутся из ориентира <see cref="PolicyQuote.ReferenceCleanPricePerPiece"/>.
/// </summary>
public static class LinePricer
{
    /// <summary>Округление цены к шагу: покупка вниз, продажа вверх. Шаг должен быть положительным.</summary>
    public static decimal RoundToStep(decimal price, decimal step, string side) =>
        side == PlanSides.Sell ? Math.Ceiling(price / step) * step : Math.Floor(price / step) * step;

    private static readonly PolicyOptions DefaultOptions = new();

    /// <summary>
    /// Санити-проверка стакана до построения цены: перевёрнутый (ask &lt; bid), слишком широкий спред ((ask−bid)/mid &gt; <see cref="PolicyOptions.MaxSpreadFraction"/>,
    /// для облигаций <see cref="PolicyOptions.MaxBondSpreadFraction"/>) или mid далеко от последней сделки (|mid−last|/last &gt; <see cref="PolicyOptions.MaxLastDeviationFraction"/>,
    /// только при известной last). Все пороги — ДОЛИ; bid/ask/last в одних единицах (облигации — пункты), поэтому отношения безразмерны.
    /// </summary>
    public static string? SanityProblem(PolicyQuote q, PolicyOptions options)
    {
        var bid = q.Bid!.Value;
        var ask = q.Ask!.Value;
        if (ask < bid) return "перевёрнутый стакан (ask < bid) — цене нельзя доверять";

        var mid = (bid + ask) / 2m;
        var maxSpread = q.Kind == PolicyAssetKind.Bond ? options.MaxBondSpreadFraction : options.MaxSpreadFraction;
        var spread = (ask - bid) / mid;
        if (spread > maxSpread) return $"спред стакана {spread:P1} шире допустимого {maxSpread:P1}";

        if (q.Last is > 0m)
        {
            var deviation = Math.Abs(mid - q.Last.Value) / q.Last.Value;
            if (deviation > options.MaxLastDeviationFraction) return $"mid отклоняется от последней сделки на {deviation:P1} (допустимо {options.MaxLastDeviationFraction:P1})";
        }

        return null;
    }

    public static PricedQuote? TryPrice(PolicyQuote q, string side, out string? skipReason, PolicyOptions? options = null)
    {
        skipReason = null;
        if (q.Bid is not > 0m || q.Ask is not > 0m)
        {
            skipReason = "нет стакана (bid/ask)";
            return null;
        }

        skipReason = SanityProblem(q, options ?? DefaultOptions);
        if (skipReason is not null) return null;

        var mid = (q.Bid.Value + q.Ask.Value) / 2m;
        if (q.Kind == PolicyAssetKind.Bond)
        {
            if (q.PriceStepPoints is not > 0m)
            {
                skipReason = "нет шага цены облигации в пунктах";
                return null;
            }

            var points = RoundToStep(mid, q.PriceStepPoints.Value, side);
            decimal clean;
            if (q.NominalRub is > 0m)
            {
                clean = points / 100m * q.NominalRub.Value;
            }
            else if (q.ReferenceCleanPricePerPiece is > 0m)
            {
                clean = q.ReferenceCleanPricePerPiece.Value; // номинал не в рублях: сумма — оценка по последней известной цене
            }
            else
            {
                skipReason = "номинал облигации не в рублях и нет ориентира цены для оценки суммы";
                return null;
            }

            return new PricedQuote(clean, points, clean + q.AccruedPerPiece);
        }

        if (q.PriceStep is not > 0m)
        {
            skipReason = "нет шага цены";
            return null;
        }

        var price = RoundToStep(mid, q.PriceStep.Value, side);
        return new PricedQuote(price, null, price);
    }
}

/// <summary>Общие операции построения строк и итогов планов (внутреннее ядро правил).</summary>
internal static class PolicyKit
{
    public static bool MarketOpen(PolicyInput input, PolicyQuote q) =>
        input.IgnoreTradingHours || TradingHours.IsOpen(q.Market, input.NowUtc, input.Options);

    /// <summary>Стоимость заявки на <paramref name="lots"/> лотов в ВАЛЮТЕ ЦЕНЫ (с НКД) и комиссия в ней же.</summary>
    public static (decimal Amount, decimal Fee) AmountAndFee(
        PolicyInput input, PolicyQuote q, PricedQuote p, long lots, FreedomMarket? freedomMarket = null)
    {
        var pieces = lots * q.Lot;
        var amount = p.DirtyPerPiece * pieces;
        return (amount, input.Fees.Estimate(q, amount, pieces, freedomMarket));
    }

    /// <summary>Наибольшее число лотов, чья стоимость с комиссией не превышает <paramref name="budget"/> (валюта портфеля). Комиссия Freedom нелинейна, поэтому уменьшаем по одному.</summary>
    public static long MaxAffordableLots(PolicyInput input, PolicyQuote q, PricedQuote p, decimal budget, FreedomMarket? freedomMarket = null)
    {
        var perLot = p.DirtyPerPiece * q.Lot * q.FxToPortfolio;
        if (perLot <= 0m || budget <= 0m) return 0;

        var lots = (long)Math.Floor(budget / perLot);
        while (lots > 0)
        {
            var (amount, fee) = AmountAndFee(input, q, p, lots, freedomMarket);
            if ((amount + fee) * q.FxToPortfolio <= budget) break;
            lots--;
        }

        return Math.Max(0, lots);
    }

    public static TradePlanLine BuildLine(
        PolicyInput input, PolicyQuote q, PricedQuote p, string side, long lots, string reason, decimal tax = 0m, FreedomMarket? freedomMarket = null)
    {
        var (amount, fee) = AmountAndFee(input, q, p, lots, freedomMarket);
        return new TradePlanLine
        {
            Broker = q.Broker,
            AccountId = q.AccountId,
            InstrumentKey = q.InstrumentKey,
            Ticker = q.Ticker,
            Side = side,
            QuantityLots = lots,
            LotSize = q.Lot,
            LimitPrice = p.PricePerPiece,
            LimitPricePoints = p.Points,
            EstAmount = amount,
            EstFee = fee,
            EstTax = tax,
            Currency = q.Currency,
            Reason = reason,
        };
    }

    /// <summary>Вес участника внутри корзины: заданные <c>sub_weight</c> нормируются на сумму; ни у кого не задан — поровну.</summary>
    public static IReadOnlyList<decimal> NormalizedWeights(IReadOnlyList<decimal?> raw)
    {
        if (raw.Count == 0) return [];
        var sum = raw.Sum(w => w ?? 0m);
        if (sum <= 0m) return raw.Select(_ => 1m / raw.Count).ToList();
        return raw.Select(w => (w ?? 0m) / sum).ToList();
    }

    /// <summary>
    /// Покупка членов корзины по <c>sub_weight</c> на <paramref name="budget"/> (валюта портфеля): сначала бюджет члена = budget × вес и целые лоты, затем
    /// остаток по одному лоту тому, кому больше всего не хватает до своей доли (пока лот помещается). Возвращает строки; пропуски — в <paramref name="skipped"/>.
    /// </summary>
    public static List<TradePlanLine> BuyMembers(
        PolicyInput input, IReadOnlyList<CapitalBasketMember> members, decimal budget, string reasonPrefix, List<PlanSkip> skipped)
    {
        var eligible = new List<(CapitalBasketMember Member, PolicyQuote Quote, PricedQuote Price, decimal? Weight)>();
        foreach (var member in members.Where(m => m.MatchRule == BasketMatchRules.Instrument))
        {
            var q = input.FindQuote(member.InstrumentKey);
            if (q is null)
            {
                skipped.Add(new PlanSkip(member.InstrumentKey, "нет торговых метаданных или стакана"));
                continue;
            }

            if (!q.BuyAvailable)
            {
                skipped.Add(new PlanSkip(q.Ticker, "покупка недоступна у брокера"));
                continue;
            }

            if (!q.KnownCommission)
            {
                skipped.Add(new PlanSkip(q.Ticker, "нет сделки с известной комиссией в журнале — авто-покупка стороннего фонда пропущена"));
                continue;
            }

            if (!MarketOpen(input, q))
            {
                skipped.Add(new PlanSkip(q.Ticker, "рынок закрыт"));
                continue;
            }

            var priced = LinePricer.TryPrice(q, PlanSides.Buy, out var why, input.Options);
            if (priced is null)
            {
                skipped.Add(new PlanSkip(q.Ticker, why ?? "нет цены"));
                continue;
            }

            eligible.Add((member, q, priced, member.SubWeight));
        }

        if (eligible.Count == 0 || budget <= 0m) return [];

        var weights = NormalizedWeights(eligible.Select(e => e.Weight).ToList());
        var lots = new long[eligible.Count];
        var spent = new decimal[eligible.Count]; // валюта портфеля, с комиссией
        var left = budget;
        for (var i = 0; i < eligible.Count; i++)
        {
            var (_, q, p, _) = eligible[i];
            lots[i] = MaxAffordableLots(input, q, p, budget * weights[i]);
            spent[i] = CostWithFee(input, q, p, lots[i]);
            left -= spent[i];
        }

        while (true)
        {
            var best = -1;
            var bestGap = decimal.MinValue;
            for (var i = 0; i < eligible.Count; i++)
            {
                var (_, q, p, _) = eligible[i];
                var next = CostWithFee(input, q, p, lots[i] + 1) - spent[i];
                if (next > left) continue;
                var gap = budget * weights[i] - spent[i];
                if (gap > bestGap)
                {
                    bestGap = gap;
                    best = i;
                }
            }

            if (best < 0) break;
            var (_, bq, bp, _) = eligible[best];
            var newCost = CostWithFee(input, bq, bp, lots[best] + 1);
            left -= newCost - spent[best];
            spent[best] = newCost;
            lots[best]++;
        }

        var lines = new List<TradePlanLine>();
        for (var i = 0; i < eligible.Count; i++)
        {
            if (lots[i] <= 0) continue;
            var (_, q, p, _) = eligible[i];
            lines.Add(BuildLine(input, q, p, PlanSides.Buy, lots[i], $"{reasonPrefix}: доля {weights[i]:P0}"));
        }

        return lines;
    }

    /// <summary>Стоимость покупки с комиссией в валюте портфеля.</summary>
    public static decimal CostWithFee(PolicyInput input, PolicyQuote q, PricedQuote p, long lots)
    {
        if (lots <= 0) return 0m;
        var (amount, fee) = AmountAndFee(input, q, p, lots);
        return (amount + fee) * q.FxToPortfolio;
    }

    /// <summary>
    /// Покупка облигаций-кандидатов на <paramref name="budget"/> рублей через <see cref="CashAllocationService.Allocate"/> (источник «recommended»: ось сектора,
    /// не больше <see cref="PolicyOptions.MaxBondLotsPerCandidate"/> лотов на бумагу) с РЕАЛЬНЫМ лотом и ценой по стакану. Кандидат без стакана пропускается.
    /// </summary>
    public static List<TradePlanLine> BuyBonds(
        PolicyInput input, decimal budget, decimal? minAfterTaxYield, string reasonPrefix, List<PlanSkip> skipped, bool applyIssuerLimit)
    {
        if (budget <= 0m) return [];

        var candidates = new List<CashAllocationCandidate>();
        var byKey = new Dictionary<string, (PolicyBondCandidate Cand, PolicyQuote Quote, PricedQuote Price)>(StringComparer.Ordinal);
        foreach (var cand in input.BondCandidates)
        {
            if (minAfterTaxYield is { } floor && (cand.AfterTaxYieldFraction is not { } at || at < floor))
            {
                skipped.Add(new PlanSkip(cand.Secid, $"доходность после налога ниже порога {floor:P2}"));
                continue;
            }

            var q = cand.Quote;
            if (q is null)
            {
                skipped.Add(new PlanSkip(cand.Secid, "нет торговых метаданных или стакана"));
                continue;
            }

            if (!q.BuyAvailable)
            {
                skipped.Add(new PlanSkip(cand.Secid, "покупка недоступна у брокера"));
                continue;
            }

            // Автоподбор — только рублёвые облигации: сумма и комиссия считаются в рублях, а у облигации с валютным номиналом рубли берутся лишь из
            // ориентира цены (оценка), заявка идёт в пунктах валютного номинала. Такие бумаги в авто-кандидаты не попадают (покупка — вручную).
            if (!string.Equals(q.Currency, "RUB", StringComparison.OrdinalIgnoreCase) || q.NominalRub is not > 0m)
            {
                skipped.Add(new PlanSkip(cand.Secid, "номинал облигации не в рублях — исключена из автоматического подбора"));
                continue;
            }

            if (!MarketOpen(input, q))
            {
                skipped.Add(new PlanSkip(cand.Secid, "рынок закрыт"));
                continue;
            }

            var p = LinePricer.TryPrice(q, PlanSides.Buy, out var why, input.Options);
            if (p is null)
            {
                skipped.Add(new PlanSkip(cand.Secid, why ?? "нет цены"));
                continue;
            }

            var amountPerLot = p.DirtyPerPiece * q.Lot;
            var feePerLot = input.Fees.Estimate(q, amountPerLot, q.Lot);
            byKey[cand.Secid] = (cand, q, p);
            candidates.Add(new CashAllocationCandidate
            {
                InstrumentId = null,
                Secid = cand.Secid,
                Name = cand.Name,
                Sector = cand.Sector,
                EffectiveYield = cand.YieldFraction,
                PricePerLotRub = amountPerLot + feePerLot,
                CleanPriceRub = p.PricePerPiece * q.Lot,
                AccruedRub = q.AccruedPerPiece * q.Lot,
                CommissionRub = feePerLot,
                LotSize = q.Lot,
                LotSizeIsAssumed = false,
                CurrentIssuerMarketValueRub = 0m,
                IsComparable = true,
            });
        }

        if (candidates.Count == 0) return [];

        var result = CashAllocationService.Allocate(
            budget,
            candidates,
            currentPortfolioValueRub: 0m,
            concentrationAxis: CashAllocationConcentrationAxis.Sector,
            maxSectorSharePercent: CashAllocationService.DefaultMaxSectorSharePercent,
            maxLotsPerCandidate: Math.Max(1, input.Options.MaxBondLotsPerCandidate));

        foreach (var skip in result.Skipped)
        {
            skipped.Add(new PlanSkip(skip.Secid ?? skip.Name ?? "?", $"аллокация: {skip.Reason}"));
        }

        var lines = new List<TradePlanLine>();
        var plannedByIssuer = new Dictionary<string, decimal>(StringComparer.Ordinal); // рубли, уже набранные в ЭТОМ плане по эмитенту
        foreach (var allocation in result.Allocations)
        {
            var (cand, q, p) = byKey[allocation.Secid!];
            var lots = (long)Math.Round(allocation.Quantity / q.Lot);
            var perLotRub = p.DirtyPerPiece * q.Lot;
            if (applyIssuerLimit && cand.IssuerKey is not null && input.BondsPortfolioValueRub > 0m)
            {
                // Лимит эмитента учитывает и уже удерживаемое, и купленное РАНЕЕ в этом же плане другими бумагами того же эмитента.
                var alreadyPlanned = plannedByIssuer.GetValueOrDefault(cand.IssuerKey);
                var maxByIssuer = Math.Floor(
                    (input.MaxIssuerSharePercent / 100m * input.BondsPortfolioValueRub - cand.IssuerHeldValueRub - alreadyPlanned) / perLotRub);
                if (maxByIssuer < lots) lots = (long)Math.Max(0, maxByIssuer);
                if (lots <= 0)
                {
                    skipped.Add(new PlanSkip(cand.Secid, $"лимит эмитента {input.MaxIssuerSharePercent:0.##}%"));
                    continue;
                }
            }

            if (cand.IssuerKey is not null) plannedByIssuer[cand.IssuerKey] = plannedByIssuer.GetValueOrDefault(cand.IssuerKey) + lots * perLotRub;

            var yieldText = cand.AfterTaxYieldFraction is { } y ? $", после налога {y:P2} годовых" : string.Empty;
            lines.Add(BuildLine(input, q, p, PlanSides.Buy, lots, $"{reasonPrefix}: кандидат {cand.Name}{yieldText}"));
        }

        return lines;
    }

    /// <summary>
    /// Налог продажи <paramref name="pieces"/> штук по средней цене входа (<see cref="SaleTaxEstimator"/>, плоские 13%, без сальдирования/ЛДВ; средняя цена вместо FIFO).
    /// Журнал неполон — оценка невозможна: берём ВЕРХНЮЮ оценку 13% с выручки и возвращаем <c>Known = false</c> (в план попадает предупреждение).
    /// </summary>
    public static (decimal Tax, bool Known) SaleTax(PolicyHolding holding, decimal netProceeds, decimal pieces)
    {
        decimal? invested = holding.AvgCostPerPiece is { } avg ? avg * pieces : null;
        var estimate = SaleTaxEstimator.Estimate(netProceeds, invested, holding.HasUnknownCost);
        return estimate is null
            ? (Math.Max(0m, netProceeds) * SaleTaxEstimator.NdflRate, false)
            : (estimate.TaxRub, true);
    }

    /// <summary>Строки продажи <paramref name="lots"/> лотов позиции: сумма, комиссия, налог, чистая выручка (валюта цены).</summary>
    public static (TradePlanLine Line, decimal Net, bool TaxKnown) BuildSell(
        PolicyInput input, PolicyHolding holding, PolicyQuote q, PricedQuote p, long lots, string reason)
    {
        var (amount, fee) = AmountAndFee(input, q, p, lots);
        var (tax, known) = SaleTax(holding, amount - fee, lots * q.Lot);
        var line = BuildLine(input, q, p, PlanSides.Sell, lots, reason, tax);
        return (line, amount - fee - tax, known);
    }

    /// <summary>Чистая выручка продажи N лотов в валюте портфеля (для подбора числа лотов).</summary>
    public static decimal NetOfSell(PolicyInput input, PolicyHolding holding, PolicyQuote q, PricedQuote p, long lots)
    {
        if (lots <= 0) return 0m;
        var (amount, fee) = AmountAndFee(input, q, p, lots);
        var (tax, _) = SaleTax(holding, amount - fee, lots * q.Lot);
        return (amount - fee - tax) * q.FxToPortfolio;
    }

    /// <summary>Наименьшее число лотов (до <paramref name="maxLots"/>), чья чистая выручка ≥ <paramref name="need"/>; не набирается — <paramref name="maxLots"/>.</summary>
    public static long LotsForNet(PolicyInput input, PolicyHolding holding, PolicyQuote q, PricedQuote p, decimal need, long maxLots)
    {
        if (maxLots <= 0 || need <= 0m) return 0;
        long lo = 1, hi = maxLots;
        if (NetOfSell(input, holding, q, p, hi) < need) return maxLots;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (NetOfSell(input, holding, q, p, mid) >= need) hi = mid;
            else lo = mid + 1;
        }

        return lo;
    }

    /// <summary>
    /// Статус плана по лимитам авто-подтверждения. <c>AutoApproved</c>, только если <paramref name="autoAllowed"/> и ВСЕ строки — покупки, каждая заявка
    /// ≤ <see cref="ExecutionLimitsOptions.AutoPerOrderRub"/> и Σ плана + уже авто-подтверждённое за сутки ≤ <see cref="ExecutionLimitsOptions.AutoPerDayRub"/>.
    /// Freedom: только при <see cref="ExecutionLimitsOptions.FreedomAutoUsd"/> &gt; 0 и заявке не выше него. Иначе <c>PendingApproval</c>.
    /// </summary>
    public static string DecideStatus(PolicyInput input, IReadOnlyList<TradePlanLine> lines, bool autoAllowed)
    {
        if (!autoAllowed || lines.Count == 0 || lines.Any(l => l.Side != PlanSides.Buy)) return PlanStatuses.PendingApproval;

        if (input.Broker == BrokerIds.Freedom)
        {
            var limit = input.Limits.FreedomAutoUsd;
            if (limit <= 0m) return PlanStatuses.PendingApproval;
            return lines.All(l => l.EstAmount <= limit) ? PlanStatuses.AutoApproved : PlanStatuses.PendingApproval;
        }

        if (!string.Equals(input.Currency, "RUB", StringComparison.OrdinalIgnoreCase)) return PlanStatuses.PendingApproval;
        if (lines.Any(l => l.EstAmount > input.Limits.AutoPerOrderRub)) return PlanStatuses.PendingApproval;
        var total = lines.Sum(l => l.EstAmount);
        return input.AutoApprovedTodayRub + total <= input.Limits.AutoPerDayRub ? PlanStatuses.AutoApproved : PlanStatuses.PendingApproval;
    }

    /// <summary>Срок жизни плана: TTL, но не позже закрытия ближайшей сессии рынков строк (если рынок открыт и часы учитываются).</summary>
    public static DateTime ExpiresAt(PolicyInput input, IEnumerable<TradePlanLine> lines)
    {
        var expires = input.NowUtc + input.Options.PendingTtl;
        if (input.IgnoreTradingHours) return expires;

        foreach (var line in lines)
        {
            var q = input.Quotes.FirstOrDefault(x => x.Broker == line.Broker && x.InstrumentKey == line.InstrumentKey)
                    ?? input.BondCandidates.Select(c => c.Quote).FirstOrDefault(x => x is not null && x.InstrumentKey == line.InstrumentKey);
            if (q is null) continue;
            var close = TradingHours.SessionCloseUtc(q.Market, input.NowUtc, input.Options);
            if (close is { } c && c > input.NowUtc && c < expires) expires = c;
        }

        return expires;
    }

    /// <summary>Собирает черновик плана: статус, срок, итоги (деньги до/после, комиссия, налог).</summary>
    public static PolicyOutcome Finish(
        PolicyInput input, string rule, string trigger, List<TradePlanLine> lines, bool autoAllowed, List<PlanSkip> skipped,
        List<string> notes, List<string>? warnings = null, decimal? requestedNet = null, decimal? shortfall = null)
    {
        if (lines.Count == 0)
        {
            notes.AddRange(skipped.Select(s => $"{s.Ticker}: {s.Reason}"));
            return new PolicyOutcome(null, notes);
        }

        for (var i = 0; i < lines.Count; i++) lines[i].LineNo = i + 1;

        var buy = lines.Where(l => l.Side == PlanSides.Buy).Sum(l => l.EstAmount * FxOf(input, l));
        var sell = lines.Where(l => l.Side == PlanSides.Sell).Sum(l => l.EstAmount * FxOf(input, l));
        var fee = lines.Sum(l => l.EstFee * FxOf(input, l));
        var reservedBuy = lines.Where(l => l.Side == PlanSides.Buy).Sum(l => (l.EstAmount + l.EstFee) * FxOf(input, l)); // валюта портфеля
        var tax = lines.Sum(l => l.EstTax * FxOf(input, l));
        var cashAfter = input.FreeCash - buy + sell - fee - tax;

        var summary = new TradePlanSummary
        {
            Portfolio = input.Portfolio,
            Currency = input.Currency,
            CashBefore = input.FreeCash,
            CashAfter = cashAfter,
            BuyTotal = buy,
            ReservedBuy = reservedBuy,
            SellTotal = sell,
            FeeTotal = fee,
            TaxTotal = tax,
            RequestedNet = requestedNet,
            NetToWithdraw = requestedNet is null ? null : cashAfter,
            Shortfall = shortfall,
            Warnings = warnings ?? [],
            Notes = notes,
            Skipped = skipped,
            PricesPossiblyStale = input.IgnoreTradingHours && lines.Any(l => !IsOpenFor(input, l)),
            ExecutionNote = "dry-run: заявки брокерам не отправляются",
        };

        var status = DecideStatus(input, lines, autoAllowed);
        return new PolicyOutcome(new TradePlanDraft(rule, trigger, status, ExpiresAt(input, lines), lines, summary), notes);
    }

    private static decimal FxOf(PolicyInput input, TradePlanLine line) =>
        input.Quotes.FirstOrDefault(q => q.Broker == line.Broker && q.InstrumentKey == line.InstrumentKey)?.FxToPortfolio
        ?? input.BondCandidates.Select(c => c.Quote).FirstOrDefault(q => q is not null && q.InstrumentKey == line.InstrumentKey)?.FxToPortfolio
        ?? 1m;

    private static bool IsOpenFor(PolicyInput input, TradePlanLine line)
    {
        var q = input.Quotes.FirstOrDefault(x => x.Broker == line.Broker && x.InstrumentKey == line.InstrumentKey)
                ?? input.BondCandidates.Select(c => c.Quote).FirstOrDefault(x => x is not null && x.InstrumentKey == line.InstrumentKey);
        return q is null || TradingHours.IsOpen(q.Market, input.NowUtc, input.Options);
    }
}

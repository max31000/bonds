using System.Text.Json;
using Bonds.Core.Credentials;
using Bonds.Core.Execution;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Bonds.Core.Policy;
using Bonds.Core.Telegram;
using Bonds.Infrastructure.Credentials;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bonds.Infrastructure.Execution;

/// <summary>Итог одного тика исполнителя (для логов и тестов).</summary>
public sealed class ExecutionTickResult
{
    public int OrdersPosted { get; set; }
    public int OrdersCancelled { get; set; }
    public int PlansStarted { get; set; }
    public int PlansFinished { get; set; }
    public List<string> Notes { get; } = [];
}

/// <summary>
/// Исполнитель планов T-Invest (задача 65) — один тик. Вызывает брокера ТОЛЬКО через <see cref="ITInvestOrderGateway"/>.
/// <para>
/// <b>Ворота.</b> Для каждого пользователя с исполнимыми планами сначала <see cref="IExecutionGateResolver"/> (env <c>Execution__Enabled</c>, режим <c>live</c>, проверенный
/// торговый токен, нет паузы) и сверка торгового токена у брокера (ровно один счёт, полный доступ, = основной счёт). Не прошло — заявки НЕ отправляются: висящие
/// снимаются, неначатые строки — <c>Failed</c> с причиной, планы закрываются. Ворота перепроверяются ещё раз перед каждой отправкой (и внутри шлюза).
/// </para>
/// <para>
/// <b>Порядок.</b> Планы <c>Approved</c>/<c>AutoApproved</c> с <c>dry_run = 0</c> (не просроченные) переводятся в <c>Submitting</c>. Строки — продажи раньше покупок, по одной:
/// следующая строка начинается, только когда предыдущая завершена. Перед отправкой — <see cref="PreflightChecks"/>; не прошло — строка <c>Failed</c> с причиной.
/// </para>
/// <para>
/// <b>Идемпотентность.</b> <c>client_order_id</c> (UUID) записывается в <c>trade_orders</c> ДО отправки брокеру и уходит брокеру как <c>order_id</c>. Заявка в статусе
/// <c>New</c> (ответ не получен, рестарт) только сверяется (<c>GetOrderState</c>, затем <c>GetOrders</c>); не нашлась за <c>UnconfirmedOrderTimeout</c> — <c>Failed</c>
/// «проверьте вручную», повторно НЕ отправляется никогда.
/// </para>
/// <para>
/// <b>Сопровождение.</b> Не исполнена за <c>RepriceAfter</c> — снятие и новая попытка ближе к противоположной стороне стакана в пределах дрейфа от УТВЕРЖДЁННОГО лимита
/// (<see cref="OrderPricing.PriceForAttempt"/>), не больше <c>MaxAttempts</c>, затем <c>Expired</c>. За <c>CancelBeforeClose</c> до закрытия сессии висящие снимаются.
/// Исполнение строки — сверка комиссии с моделью (<see cref="FeeReconciliation"/>), уведомление; план завершён — итог Done/PartiallyDone/Failed и синк портфеля.
/// Комиссия 0 сразу после исполнения при ненулевой модельной (брокер присылает её с задержкой) — сверка и уведомление откладываются до
/// <c>Execution:MaxFeeRechecks</c> тиков с перечитыванием состояния (<c>trade_orders.fee_rechecks</c>/<c>fee_checked_at</c>); план закрывается после сверки.
/// </para>
/// <para>
/// <b>Вне часов торгов</b> подтверждённый план ждёт в <c>Approved</c>/<c>AutoApproved</c> (не стартует), пока сессия не откроется и до <c>expires_at</c>
/// (ручным планам срок продлевается при подтверждении до конца ближайшей сессии — <c>PolicyService.ApproveAsync</c>).
/// </para>
/// <para>
/// <b>AutoApproved</b> перепроверяется в момент исполнения на ТЕКУЩИХ настройках: белый список и лимит на заявку (<see cref="AutoExecutionWhitelist"/>) при старте,
/// лимит на заявку по цене заявки и суточный лимит по ФАКТУ исполненного (<see cref="AutoDayUsage"/>) перед каждой отправкой.
/// </para>
/// <para>
/// <b>Снять нельзя</b> (нет торгового токена, токен на другой счёт, брокер отказал): одно уведомление на заявку «снимите её в терминале брокера» и отметка
/// <c>trade_orders.halt_alerted_at</c>; план ОСТАЁТСЯ <c>Submitting</c> (выбранный вариант: не новый статус — исполнимые фильтры, лимиты и отмена продолжают
/// работать как есть), сверка и попытка снять повторяются каждый тик, пока заявка у нас активна. В API это видно как <c>needsAttention</c> и баннер активных заявок.
/// </para>
/// </summary>
public sealed class PlanExecutionEngine
{
    private readonly ITradePlanRepository _plans;
    private readonly ITradeOrderRepository _orders;
    private readonly IInstrumentTradingMetaRepository _meta;
    private readonly IAccountRepository _accounts;
    private readonly IUserCredentialRepository _credentialRecords;
    private readonly ICapitalBasketRepository _baskets;
    private readonly ITInvestOrderGateway _gateway;
    private readonly IExecutionGateResolver _gate;
    private readonly IExecutionNotifier _notifier;
    private readonly IPortfolioSyncTrigger _sync;
    private readonly IOptionsMonitor<ExecutionOptions> _options;
    private readonly ExecutionLimitsOptions _limits;
    private readonly PolicyOptions _policy;
    private readonly ILogger<PlanExecutionEngine> _logger;

    public PlanExecutionEngine(
        ITradePlanRepository plans,
        ITradeOrderRepository orders,
        IInstrumentTradingMetaRepository meta,
        IAccountRepository accounts,
        IUserCredentialRepository credentialRecords,
        ICapitalBasketRepository baskets,
        ITInvestOrderGateway gateway,
        IExecutionGateResolver gate,
        IExecutionNotifier notifier,
        IPortfolioSyncTrigger sync,
        IOptionsMonitor<ExecutionOptions> options,
        IOptions<ExecutionLimitsOptions> limits,
        IOptions<PolicyOptions> policy,
        ILogger<PlanExecutionEngine> logger)
    {
        _plans = plans;
        _orders = orders;
        _meta = meta;
        _accounts = accounts;
        _credentialRecords = credentialRecords;
        _baskets = baskets;
        _gateway = gateway;
        _gate = gate;
        _notifier = notifier;
        _sync = sync;
        _options = options;
        _limits = limits.Value;
        _policy = policy.Value;
        _logger = logger;
    }

    private ExecutionOptions Opt => _options.CurrentValue;

    private sealed record Ctx(ulong UserId, ulong AccountId, string BrokerAccountId, DateTime NowUtc, ExecutionTickResult Result);

    public async Task<ExecutionTickResult> RunTickAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        var result = new ExecutionTickResult();
        foreach (var userId in await _plans.ListUserIdsWithExecutablePlansAsync())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await RunForUserAsync(userId, nowUtc, result, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Plan execution tick failed for user {UserId}", userId);
                result.Notes.Add($"user {userId}: {ex.GetType().Name}");
            }
        }

        return result;
    }

    /// <summary>Тик для одного пользователя (тот же путь, что в <see cref="RunTickAsync"/>; используется тестами на общей БД).</summary>
    public async Task<ExecutionTickResult> RunForUserAsync(ulong userId, DateTime nowUtc, CancellationToken ct = default)
    {
        var result = new ExecutionTickResult();
        await RunForUserAsync(userId, nowUtc, result, ct);
        return result;
    }

    private async Task RunForUserAsync(ulong userId, DateTime nowUtc, ExecutionTickResult result, CancellationToken ct)
    {
        var plans = await _plans.ListExecutableAsync(userId);
        if (plans.Count == 0) return;

        var primary = (await _accounts.GetByUserIdAndBrokerAsync(userId, Brokers.TInvest)).OrderBy(a => a.Id).FirstOrDefault();
        var gate = await _gate.ResolveAsync(userId);
        if (!gate.Live)
        {
            await HaltAsync(userId, primary, plans, string.Join("; ", gate.Reasons), nowUtc, result, ct);
            return;
        }

        if (string.IsNullOrWhiteSpace(primary?.BrokerAccountId))
        {
            await HaltAsync(userId, primary, plans, "Основной счёт T-Invest не привязан", nowUtc, result, ct);
            return;
        }

        // Сверка торгового токена у брокера на КАЖДОМ тике: токен мог быть перевыпущен на другой счёт/права, а read-токен — перепривязать основной счёт.
        IReadOnlyList<TInvestTradeAccount> tokenAccounts;
        try
        {
            tokenAccounts = await _gateway.GetAccountsAsync(userId, ct);
        }
        catch (TInvestGatewayException ex) when (ex.Kind == GatewayErrorKind.Transient)
        {
            _logger.LogWarning("Execution: T-Invest accounts check unavailable for user {UserId} ({Code}) — tick skipped", userId, ex.Code);
            return;
        }
        catch (TInvestGatewayException ex)
        {
            tokenAccounts = [];
            _logger.LogWarning("Execution: trade token rejected for user {UserId} ({Code})", userId, ex.Code);
        }

        var check = TInvestTradeCredentialValidator.Evaluate(tokenAccounts, primary!.BrokerAccountId!);
        if (!check.Ok)
        {
            var reason = "Торговый токен не прошёл проверку: " + check.Message;
            await _credentialRecords.UpdateStatusAsync(userId, CredentialProviders.TInvestTrade, CredentialStatus.Invalid, Truncate(check.Message, 255));
            await HaltAsync(userId, primary, plans, reason, nowUtc, result, ct);
            return;
        }

        var ctx = new Ctx(userId, primary.Id, primary.BrokerAccountId!, nowUtc, result);
        foreach (var plan in plans)
        {
            ct.ThrowIfCancellationRequested();
            if (plan.Status != PlanStatuses.Submitting && !await StartAsync(plan, ctx, ct)) continue;
            await ProcessPlanAsync(plan, ctx, ct);
        }
    }

    // ─── Старт плана ───────────────────────────────────────────────────────────────────────

    private static readonly string[] Startable = [PlanStatuses.Approved, PlanStatuses.AutoApproved];

    private async Task<bool> StartAsync(TradePlan plan, Ctx ctx, CancellationToken ct)
    {
        string? reject = null;
        var toStatus = PlanStatuses.Failed;
        if (plan.CancelRequested)
        {
            reject = "отменён пользователем до исполнения";
            toStatus = PlanStatuses.Rejected;
        }
        else if (plan.ExpiresAt <= ctx.NowUtc)
        {
            reject = "срок действия истёк до исполнения";
            toStatus = PlanStatuses.Expired;
        }
        else if (plan.Lines.Count == 0)
        {
            reject = "в плане нет строк";
        }
        else if (plan.Lines.Any(l => !string.Equals(l.Broker, Brokers.TInvest, StringComparison.OrdinalIgnoreCase)))
        {
            reject = "исполнитель работает только с T-Invest";
        }
        else if (plan.Lines.Any(l => l.AccountId != ctx.AccountId))
        {
            reject = "строки плана относятся не к основному счёту T-Invest";
        }
        else if (plan.Lines.Any(l => l.QuantityLots <= 0))
        {
            reject = "некорректное количество лотов";
        }
        else if (plan.DecidedVia == DecidedVia.Auto
                 && AutoExecutionWhitelist.Check(plan, await _baskets.GetByUserAsync(ctx.UserId), _limits.AutoPerOrderRub) is { } notAllowed)
        {
            reject = "авто-исполнение не разрешено: " + notAllowed;
        }

        if (reject is not null)
        {
            if (await _plans.SetExecutionStatusAsync(ctx.UserId, plan.Id, Startable, toStatus, WithNote(plan.SummaryJson, "не исполнен: " + reject)))
                await _notifier.NotifyAsync(ctx.UserId, new BotMessage(ExecutionMessages.PlanNotStarted(plan, reject)), ct);
            return false;
        }

        // Вне часов торгов (или в окне перед закрытием) план не стартует, а ждёт открытия сессии в своём статусе — до expires_at (проверено выше).
        var metas = await MetasAsync(plan);
        if (plan.Lines.Any(l => IsClosingWindow(l, metas.GetValueOrDefault(l.InstrumentKey), ctx.NowUtc)))
        {
            ctx.Result.Notes.Add($"plan {plan.Id}: waiting for trading session");
            return false;
        }

        if (!await _plans.SetExecutionStatusAsync(ctx.UserId, plan.Id, Startable, PlanStatuses.Submitting, WithNote(plan.SummaryJson, "исполняется")))
            return false;
        plan.Status = PlanStatuses.Submitting;
        ctx.Result.PlansStarted++;
        _logger.LogInformation("Execution: plan {PlanId} started ({Lines} line(s))", plan.Id, plan.Lines.Count);
        return true;
    }

    // ─── Исполнение плана ──────────────────────────────────────────────────────────────────

    private async Task ProcessPlanAsync(TradePlan plan, Ctx ctx, CancellationToken ct)
    {
        var orders = (await _orders.ListByPlanAsync(plan.Id)).ToList();
        if (plan.CancelRequested)
        {
            if (await StopLinesAsync(plan, orders, TradeOrderErrorCodes.UserCancel, "отменено пользователем", ctx, ct))
                await FinalizeAsync(plan, orders, ctx, ct);
            return;
        }

        var metas = await MetasAsync(plan);

        foreach (var line in OrderPricing.ExecutionOrder(plan.Lines))
        {
            if (LineState(line, orders).Terminal)
            {
                if (FeePending(line, orders)) await RecheckFeeAsync(plan, line, orders, ctx, ct);
                continue;
            }

            var terminal = await AdvanceLineAsync(plan, line, orders, metas.GetValueOrDefault(line.InstrumentKey), ctx, ct);
            if (!terminal) return; // строка в работе — следующая ждёт (по одной строке, продажи раньше покупок)
        }

        await FinalizeAsync(plan, orders, ctx, ct);
    }

    private async Task<Dictionary<string, InstrumentTradingMeta>> MetasAsync(TradePlan plan) =>
        (await _meta.GetByKeysAsync(Brokers.TInvest, plan.Lines.Select(l => l.InstrumentKey).Distinct(StringComparer.OrdinalIgnoreCase).ToList()))
        .GroupBy(m => m.InstrumentKey, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

    private static LineExecutionState LineState(TradePlanLine line, IEnumerable<TradeOrder> orders) =>
        LineExecutionState.From(line, orders.Where(o => o.LineNo == line.LineNo).ToList());

    /// <summary>Итоговая сверка комиссии строки отложена (комиссия ещё не пришла) — строка исполнена, но план не закрывается, пока сверка не пройдёт.</summary>
    private static bool FeePending(TradePlanLine line, IEnumerable<TradeOrder> orders) =>
        orders.Where(o => o.LineNo == line.LineNo).OrderByDescending(o => o.Attempt).FirstOrDefault() is { FeeRechecks: > 0, FeeCheckedAt: null };

    /// <summary>Продвигает строку на шаг. true — строка завершена (можно переходить к следующей).</summary>
    private async Task<bool> AdvanceLineAsync(TradePlan plan, TradePlanLine line, List<TradeOrder> orders, InstrumentTradingMeta? meta, Ctx ctx, CancellationToken ct)
    {
        var state = LineState(line, orders);
        var closing = IsClosingWindow(line, meta, ctx.NowUtc);

        if (state.Active is { } active)
        {
            if (await RefreshAsync(active, line, ctx, ct) != RefreshOutcome.Updated) return false;
            switch (active.Status)
            {
                case TradeOrderStatuses.New:
                    return false; // ответа брокера нет — только ждём сверки
                case TradeOrderStatuses.Filled:
                    break;
                case TradeOrderStatuses.Rejected or TradeOrderStatuses.Failed:
                    await OnLineTerminalAsync(plan, line, orders, active.ErrorMessage ?? "заявка отклонена брокером", ctx, ct);
                    return true;
                case TradeOrderStatuses.Cancelled:
                    // Снята не нами (например, брокером в конце дня): закрытие — окончательно, иначе — перевыставление, пока есть попытки.
                    if (!await ClassifyExternalCancelAsync(active, closing, ctx)) { await OnLineTerminalAsync(plan, line, orders, active.ErrorMessage ?? "заявка снята", ctx, ct); return true; }
                    break;
                default:
                    if (closing)
                    {
                        if (await CancelAsync(active, line, TradeOrderStatuses.Cancelled, TradeOrderErrorCodes.SessionClose, "снята перед закрытием сессии", ctx, ct) != CancelOutcome.Done)
                            return false;
                        if (active.Status != TradeOrderStatuses.Filled)
                        {
                            await OnLineTerminalAsync(plan, line, orders, "снята перед закрытием сессии", ctx, ct);
                            return true;
                        }

                        break;
                    }

                    if (ctx.NowUtc - active.CreatedAt < Opt.RepriceAfter) return false;

                    var last = active.Attempt >= Opt.MaxAttempts;
                    if (await CancelAsync(
                            active, line, last ? TradeOrderStatuses.Expired : TradeOrderStatuses.Cancelled, last ? TradeOrderErrorCodes.Expired : TradeOrderErrorCodes.Reprice,
                            last ? "не исполнена за отведённые попытки" : "перевыставление", ctx, ct) != CancelOutcome.Done)
                        return false;
                    if (last && active.Status != TradeOrderStatuses.Filled)
                    {
                        await OnLineTerminalAsync(plan, line, orders, $"не исполнена за {Opt.MaxAttempts} попытки — остаток снят", ctx, ct);
                        return true;
                    }

                    break;
            }

            state = LineState(line, orders);
            if (state.RemainingLots == 0)
            {
                await OnLineTerminalAsync(plan, line, orders, null, ctx, ct);
                return true;
            }
        }

        return await SubmitNextAttemptAsync(plan, line, orders, meta, closing, ctx, ct);
    }

    private async Task<bool> SubmitNextAttemptAsync(
        TradePlan plan, TradePlanLine line, List<TradeOrder> orders, InstrumentTradingMeta? meta, bool closing, Ctx ctx, CancellationToken ct)
    {
        var state = LineState(line, orders);
        if (state.RemainingLots == 0) return true;
        var attempt = (state.Last?.Attempt ?? 0) + 1;
        if (attempt > Opt.MaxAttempts)
        {
            await RecordFailureAsync(plan, line, orders, attempt, TradeOrderStatuses.Expired, TradeOrderErrorCodes.Expired, "исчерпаны попытки", ctx, ct);
            return true;
        }

        if (meta is null || string.IsNullOrWhiteSpace(meta.Uid))
        {
            await RecordFailureAsync(plan, line, orders, attempt, TradeOrderStatuses.Failed, TradeOrderErrorCodes.Preflight, "нет торговых метаданных бумаги (uid)", ctx, ct);
            return true;
        }

        var step = OrderPricing.StepOf(line, meta);
        if (step is null)
        {
            await RecordFailureAsync(plan, line, orders, attempt, TradeOrderStatuses.Failed, TradeOrderErrorCodes.Preflight, "неизвестен шаг цены бумаги", ctx, ct);
            return true;
        }

        // Ворота ещё раз — непосредственно перед отправкой (пауза могла включиться посреди тика).
        var gate = await _gate.ResolveAsync(ctx.UserId);
        if (!gate.Live)
        {
            await RecordFailureAsync(plan, line, orders, attempt, TradeOrderStatuses.Failed, TradeOrderErrorCodes.Halted, string.Join("; ", gate.Reasons), ctx, ct);
            return true;
        }

        GatewayOrderBook book;
        GatewayTradingStatus trading;
        GatewayPositions positions;
        try
        {
            book = await _gateway.GetOrderBookAsync(ctx.UserId, meta.Uid!, ct);
            trading = await _gateway.GetTradingStatusAsync(ctx.UserId, meta.Uid!, ct);
            positions = await _gateway.GetPositionsAsync(ctx.UserId, ctx.BrokerAccountId, ct);
        }
        catch (TInvestGatewayException ex) when (ex.Kind == GatewayErrorKind.Transient)
        {
            _logger.LogWarning("Execution: market data unavailable for plan {PlanId} line {LineNo} ({Code})", plan.Id, line.LineNo, ex.Code);
            return false;
        }
        catch (TInvestGatewayException ex)
        {
            await RecordFailureAsync(plan, line, orders, attempt, TradeOrderStatuses.Failed, TradeOrderErrorCodes.Preflight, ex.Message, ctx, ct);
            return true;
        }

        var isBond = OrderPricing.IsPointsPriced(line);
        var approved = OrderPricing.ApprovedLimit(line);
        var drift = Opt.DriftFor(isBond);
        var price = OrderPricing.PriceForAttempt(line.Side, approved, book.Bid, book.Ask, step.Value, drift, attempt, Opt.MaxAttempts, state.Last?.LimitPrice);
        var lots = state.RemainingLots;

        // AutoApproved: суточный лимит по ФАКТУ — исполненное других планов дня (лоты × средняя цена) + оценка ещё не исполненного, плюс этот план так же.
        decimal? autoUsed = null;
        decimal? autoPerOrder = null;
        var autoPlanBuys = 0m;
        if (plan.DecidedVia == DecidedVia.Auto)
        {
            var dayPlans = await _plans.ListAutoDayPlansAsync(ctx.UserId, MoscowDayStartUtc(ctx.NowUtc));
            var dayOrders = await _orders.ListByPlansAsync(dayPlans.Where(p => p.Id != plan.Id).Select(p => p.Id).ToList());
            autoUsed = AutoDayUsage.SpentRub(dayPlans, dayOrders, plan.Id);
            autoPlanBuys = AutoDayUsage.SpentRub([plan], orders);
            autoPerOrder = _limits.AutoPerOrderRub;
        }

        var failure = PreflightChecks.Evaluate(new PreflightInput
        {
            Side = line.Side,
            ApprovedLimit = approved,
            OrderPrice = price,
            Lots = lots,
            LotSize = line.LotSize,
            MaxDriftFraction = drift,
            NowUtc = ctx.NowUtc,
            MaxQuoteAge = Opt.MaxQuoteAge,
            Bid = book.Bid,
            Ask = book.Ask,
            QuoteTimeUtc = book.TimestampUtc,
            NormalTrading = trading.NormalTrading,
            LimitOrderAvailable = trading.LimitOrderAvailable,
            ApiTradeAvailable = trading.ApiTradeAvailable && meta.ApiTradeAvailable,
            SideAvailable = line.Side == PlanSides.Sell ? meta.SellAvailable : meta.BuyAvailable,
            AvailableMoney = positions.MoneyAvailable.TryGetValue(NormalizeCurrency(line.Currency), out var money) ? money : null,
            RequiredMoney = OrderPricing.RequiredMoney(line, price, lots),
            AvailablePieces = positions.SecuritiesAvailablePieces.TryGetValue(meta.Uid!, out var pieces) ? pieces : null,
            AutoUsedTodayRub = autoUsed,
            AutoPlanBuysRub = autoPlanBuys,
            AutoPerDayRub = _limits.AutoPerDayRub,
            AutoPerOrderRub = autoPerOrder,
            OrderAmount = OrderPricing.OrderAmount(line, price, lots),
            Paused = gate.Paused,
            SessionOpenForNewOrders = !closing,
        });
        if (failure is not null)
        {
            await RecordFailureAsync(plan, line, orders, attempt, TradeOrderStatuses.Failed, failure.Code, failure.Message, ctx, ct);
            return true;
        }

        // Идемпотентность: заявка с client_order_id записана ДО отправки. Сбой записи — исключение, отправки не будет.
        var order = new TradeOrder
        {
            PlanId = plan.Id,
            LineNo = line.LineNo,
            Attempt = attempt,
            ClientOrderId = Guid.NewGuid().ToString("D"),
            Status = TradeOrderStatuses.New,
            LimitPrice = price,
            PriceUnit = OrderPricing.PriceUnitOf(line),
            LotsRequested = lots,
            Currency = NormalizeCurrency(line.Currency),
            CreatedAt = ctx.NowUtc,
            UpdatedAt = ctx.NowUtc,
        };
        order.Id = await _orders.InsertAsync(order);
        orders.Add(order);

        try
        {
            var posted = await _gateway.PostLimitOrderAsync(
                ctx.UserId, new GatewayLimitOrder(ctx.BrokerAccountId, meta.Uid!, line.Side, lots, price, order.PriceUnit, order.ClientOrderId), ct);
            ctx.Result.OrdersPosted++;
            OrderStateMapping.Apply(order, posted, line.LotSize, ctx.NowUtc);
            await _orders.UpdateAsync(order);
        }
        catch (TInvestGatewayException ex) when (ex.Kind == GatewayErrorKind.Rejected)
        {
            // Определённый отказ — заявки у брокера нет. gate_closed (ворота закрылись) и price_grid (цена не легла в Quotation) — до брокера не дошло: Failed.
            var notSent = ex.Code is "gate_closed" or TradeOrderErrorCodes.PriceGrid;
            order.Status = notSent ? TradeOrderStatuses.Failed : TradeOrderStatuses.Rejected;
            order.ErrorCode = ex.Code switch
            {
                "gate_closed" => TradeOrderErrorCodes.Halted,
                TradeOrderErrorCodes.PriceGrid => TradeOrderErrorCodes.PriceGrid,
                _ => TradeOrderErrorCodes.BrokerRejected,
            };
            order.ErrorMessage = ex.Message;
            order.UpdatedAt = ctx.NowUtc;
            await _orders.UpdateAsync(order);
            await OnLineTerminalAsync(plan, line, orders, ex.Message, ctx, ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Исход неизвестен (сеть/таймаут): заявка остаётся New и будет только СВЕРЕНА на следующих тиках, повторной отправки нет.
            _logger.LogWarning("Execution: order submission outcome unknown for order {ClientOrderId} ({ErrorType}) — will reconcile", order.ClientOrderId, ex.GetType().Name);
            return false;
        }

        await _notifier.NotifyAsync(ctx.UserId, ExecutionMessages.Submitted(plan, line, order), ct);
        if (order.Status is TradeOrderStatuses.Rejected)
        {
            await OnLineTerminalAsync(plan, line, orders, order.ErrorMessage ?? "заявка отклонена брокером", ctx, ct);
            return true;
        }

        if (LineState(line, orders).RemainingLots == 0)
        {
            await OnLineTerminalAsync(plan, line, orders, null, ctx, ct);
            return true;
        }

        return false;
    }

    // ─── Сверка и снятие ───────────────────────────────────────────────────────────────────

    private enum RefreshOutcome
    {
        /// <summary>Состояние обновлено (или заявка закрыта как неподтверждённая).</summary>
        Updated,

        /// <summary>Брокер заявку пока не знает, таймаут не вышел — ждём.</summary>
        Pending,

        /// <summary>Сбой сети/перегрузка — повтор на следующем тике.</summary>
        Unavailable,

        /// <summary>Определённый отказ (нет токена, нет прав на счёт) — сверить нельзя, пока не исправят токен.</summary>
        Impossible,
    }

    private enum CancelOutcome
    {
        Done,
        Retry,

        /// <summary>Снять нельзя: нет токена/счёта, токен на другой счёт или брокер отказал снять активную заявку.</summary>
        Impossible,
    }

    /// <summary>Обновляет заявку по брокеру. Не <see cref="RefreshOutcome.Updated"/> — состояние неизвестно, ждём следующего тика.</summary>
    private async Task<RefreshOutcome> RefreshAsync(TradeOrder order, TradePlanLine line, Ctx ctx, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(ctx.BrokerAccountId)) return RefreshOutcome.Impossible;
        GatewayOrderState? state;
        try
        {
            state = await _gateway.GetOrderStateAsync(ctx.UserId, ctx.BrokerAccountId, order.ClientOrderId, ct);
            if (state is null && order.Status == TradeOrderStatuses.New)
                state = (await _gateway.GetActiveOrdersAsync(ctx.UserId, ctx.BrokerAccountId, ct))
                    .FirstOrDefault(o => string.Equals(o.ClientOrderId, order.ClientOrderId, StringComparison.OrdinalIgnoreCase));
        }
        catch (TInvestGatewayException ex)
        {
            _logger.LogWarning("Execution: order state unavailable for {ClientOrderId} ({Code})", order.ClientOrderId, ex.Code);
            return ex.Kind == GatewayErrorKind.Transient ? RefreshOutcome.Unavailable : RefreshOutcome.Impossible;
        }

        if (state is null)
        {
            var since = order.Status == TradeOrderStatuses.New ? order.CreatedAt : order.UpdatedAt;
            if (ctx.NowUtc - since < Opt.UnconfirmedOrderTimeout) return RefreshOutcome.Pending;

            // Брокер заявку не знает: отправки не было или она потеряна. Повторно НЕ отправляем — закрываем строку и просим проверить вручную.
            order.Status = TradeOrderStatuses.Failed;
            order.ErrorCode = TradeOrderErrorCodes.UnknownState;
            order.ErrorMessage = "Состояние заявки не подтвердилось у брокера — повторно не отправлялась, проверьте терминал";
            order.UpdatedAt = ctx.NowUtc;
            await _orders.UpdateAsync(order);
            return RefreshOutcome.Updated;
        }

        OrderStateMapping.Apply(order, state, line.LotSize, ctx.NowUtc);
        await _orders.UpdateAsync(order);
        return RefreshOutcome.Updated;
    }

    /// <summary>Снятие брокером без нашей команды. true — строка продолжается перевыставлением; false — окончательно.</summary>
    private async Task<bool> ClassifyExternalCancelAsync(TradeOrder order, bool closing, Ctx ctx)
    {
        if (!closing && order.Attempt < Opt.MaxAttempts && order.ErrorCode is null or TradeOrderErrorCodes.Reprice)
        {
            order.ErrorCode = TradeOrderErrorCodes.Reprice;
            order.ErrorMessage ??= "снята брокером — перевыставление";
        }
        else
        {
            order.ErrorCode ??= closing ? TradeOrderErrorCodes.SessionClose : TradeOrderErrorCodes.Expired;
            order.ErrorMessage ??= "снята брокером";
        }

        order.UpdatedAt = ctx.NowUtc;
        await _orders.UpdateAsync(order);
        return order.ErrorCode == TradeOrderErrorCodes.Reprice;
    }

    /// <summary>
    /// Снимает заявку и фиксирует итог (<paramref name="finalStatus"/> + код). Перед фиксацией состояние перечитывается: если заявка успела исполниться — остаётся
    /// <c>Filled</c>. <see cref="CancelOutcome.Retry"/> — сбой сети или брокер ещё не подтвердил снятие (повтор на следующем тике);
    /// <see cref="CancelOutcome.Impossible"/> — снять нельзя (нет счёта/токена, чужой счёт, брокер отказал снять активную заявку): одно уведомление пользователю
    /// (<see cref="AlertCancelImpossibleAsync"/>), попытки продолжаются каждый тик.
    /// </summary>
    private async Task<CancelOutcome> CancelAsync(TradeOrder order, TradePlanLine line, string finalStatus, string code, string message, Ctx ctx, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(ctx.BrokerAccountId)) return await AlertCancelImpossibleAsync(order, line, ctx, ct);

        var refused = false;
        try
        {
            await _gateway.CancelOrderAsync(ctx.UserId, ctx.BrokerAccountId, order.ClientOrderId, ct);
            ctx.Result.OrdersCancelled++;
        }
        catch (TInvestGatewayException ex) when (ex.Kind == GatewayErrorKind.Transient)
        {
            _logger.LogWarning("Execution: cancel of {ClientOrderId} failed ({Code}) — will retry", order.ClientOrderId, ex.Code);
            return CancelOutcome.Retry;
        }
        catch (TInvestGatewayException ex)
        {
            // Отказ снять (уже исполнена/снята — или токена нет/чужой счёт) — истину покажет сверка ниже.
            refused = true;
            _logger.LogInformation("Execution: cancel of {ClientOrderId} refused ({Code})", order.ClientOrderId, ex.Code);
        }

        try
        {
            if (await _gateway.GetOrderStateAsync(ctx.UserId, ctx.BrokerAccountId, order.ClientOrderId, ct) is { } state)
                OrderStateMapping.Apply(order, state, line.LotSize, ctx.NowUtc);
        }
        catch (TInvestGatewayException ex) when (ex.Kind == GatewayErrorKind.Transient)
        {
            _logger.LogWarning("Execution: post-cancel state of {ClientOrderId} unavailable ({Code}) — will retry", order.ClientOrderId, ex.Code);
            return CancelOutcome.Retry;
        }
        catch (TInvestGatewayException ex)
        {
            _logger.LogWarning("Execution: post-cancel state of {ClientOrderId} refused ({Code})", order.ClientOrderId, ex.Code);
            return await AlertCancelImpossibleAsync(order, line, ctx, ct);
        }

        if (order.Status == TradeOrderStatuses.Filled)
        {
            await _orders.UpdateAsync(order);
            return CancelOutcome.Done;
        }

        if (TradeOrderStatuses.IsActive(order.Status) && order.Status != TradeOrderStatuses.New)
        {
            // Брокер ещё показывает заявку активной: после принятого снятия — ждём подтверждения; после отказа снять — снять нельзя.
            await _orders.UpdateAsync(order);
            return refused ? await AlertCancelImpossibleAsync(order, line, ctx, ct) : CancelOutcome.Retry;
        }

        order.Status = finalStatus;
        order.ErrorCode = code;
        order.ErrorMessage = message;
        order.UpdatedAt = ctx.NowUtc;
        await _orders.UpdateAsync(order);
        return CancelOutcome.Done;
    }

    /// <summary>
    /// Снять заявку автоматически нельзя: ОДНО уведомление на заявку «снимите её в терминале брокера» (<c>halt_alerted_at</c>), без повторов. Заявка остаётся
    /// активной у нас, сверка/снятие повторяются каждый тик.
    /// </summary>
    private async Task<CancelOutcome> AlertCancelImpossibleAsync(TradeOrder order, TradePlanLine line, Ctx ctx, CancellationToken ct)
    {
        _logger.LogWarning("Execution: order {ClientOrderId} cannot be cancelled automatically", order.ClientOrderId);
        if (order.HaltAlertedAt is not null) return CancelOutcome.Impossible;
        order.HaltAlertedAt = ctx.NowUtc;
        await _orders.UpdateAsync(order);
        await _notifier.NotifyAsync(ctx.UserId, new BotMessage(ExecutionMessages.CancelImpossible(line.Ticker, Math.Max(0, order.LotsRequested - order.LotsFilled))), ct);
        return CancelOutcome.Impossible;
    }

    // ─── Итоги строк и планов ──────────────────────────────────────────────────────────────

    private async Task RecordFailureAsync(
        TradePlan plan, TradePlanLine line, List<TradeOrder> orders, int attempt, string status, string code, string message, Ctx ctx, CancellationToken ct)
    {
        var state = LineState(line, orders);
        var approved = OrderPricing.ApprovedLimit(line);
        var order = new TradeOrder
        {
            PlanId = plan.Id,
            LineNo = line.LineNo,
            Attempt = attempt,
            ClientOrderId = Guid.NewGuid().ToString("D"),
            Status = status,
            LimitPrice = approved,
            PriceUnit = OrderPricing.PriceUnitOf(line),
            LotsRequested = state.RemainingLots,
            Currency = NormalizeCurrency(line.Currency),
            ErrorCode = code,
            ErrorMessage = message,
            CreatedAt = ctx.NowUtc,
            UpdatedAt = ctx.NowUtc,
        };
        order.Id = await _orders.InsertAsync(order);
        orders.Add(order);
        await OnLineTerminalAsync(plan, line, orders, message, ctx, ct);
    }

    /// <summary>Строка завершена: исполненное — уведомление (цена, лоты, комиссия) и сверка комиссии с моделью; неисполненный остаток — уведомление с причиной.</summary>
    private async Task OnLineTerminalAsync(TradePlan plan, TradePlanLine line, List<TradeOrder> orders, string? reason, Ctx ctx, CancellationToken ct)
    {
        var lineOrders = orders.Where(o => o.LineNo == line.LineNo).ToList();
        var filled = lineOrders.Sum(o => o.LotsFilled);
        var currency = NormalizeCurrency(line.Currency);
        var lastOrder = lineOrders.OrderByDescending(o => o.Attempt).FirstOrDefault();
        if (filled > 0)
        {
            var commission = lineOrders.Sum(o => o.ExecutedCommission ?? 0m);

            // Комиссия 0 сразу после исполнения при ненулевой модельной — брокер ещё не прислал её: откладываем сверку и уведомление (перечитаем на следующих тиках).
            if (commission == 0m && lastOrder is { FeeCheckedAt: null } && lastOrder.FeeRechecks < Opt.MaxFeeRechecks
                && FeeReconciliation.IsMismatch(line, filled, 0m, Opt.FeeMismatchThreshold))
            {
                lastOrder.FeeRechecks++;
                lastOrder.UpdatedAt = ctx.NowUtc;
                await _orders.UpdateAsync(lastOrder);
                return;
            }

            if (lastOrder is not null)
            {
                lastOrder.FeeCheckedAt = ctx.NowUtc;
                await _orders.UpdateAsync(lastOrder);
            }

            var weighted = lineOrders.Where(o => o.LotsFilled > 0 && o.AvgFillPrice is not null).ToList();
            decimal? avg = weighted.Count == 0 ? null : weighted.Sum(o => o.AvgFillPrice!.Value * o.LotsFilled) / weighted.Sum(o => o.LotsFilled);
            await _notifier.NotifyAsync(ctx.UserId, new BotMessage(ExecutionMessages.Filled(plan, line, filled, avg, commission, currency)), ct);

            if (FeeReconciliation.IsMismatch(line, filled, commission, Opt.FeeMismatchThreshold))
            {
                var expected = FeeReconciliation.ExpectedFee(line, filled);
                _logger.LogWarning("Execution: fee mismatch on plan {PlanId} line {LineNo}: expected {Expected}, actual {Actual}", plan.Id, line.LineNo, expected, commission);
                await _notifier.NotifyAsync(ctx.UserId, new BotMessage(ExecutionMessages.FeeMismatch(plan, line, expected, commission, currency)), ct);
            }
        }

        if (filled < line.QuantityLots && reason is not null)
        {
            var last = lastOrder;
            var text = last?.Status is TradeOrderStatuses.Cancelled or TradeOrderStatuses.Expired
                ? ExecutionMessages.Cancelled(plan, line, reason)
                : ExecutionMessages.Failed(plan, line, reason);
            await _notifier.NotifyAsync(ctx.UserId, new BotMessage(text), ct);
        }
    }

    /// <summary>Отложенная сверка комиссии: перечитать комиссию исполненных заявок строки (только её) и повторить итог строки.</summary>
    private async Task RecheckFeeAsync(TradePlan plan, TradePlanLine line, List<TradeOrder> orders, Ctx ctx, CancellationToken ct)
    {
        var lineOrders = orders.Where(o => o.LineNo == line.LineNo).ToList();
        if (!string.IsNullOrEmpty(ctx.BrokerAccountId))
        {
            foreach (var o in lineOrders.Where(o => o.LotsFilled > 0 && (o.ExecutedCommission ?? 0m) == 0m))
            {
                try
                {
                    if (await _gateway.GetOrderStateAsync(ctx.UserId, ctx.BrokerAccountId, o.ClientOrderId, ct) is { ExecutedCommission: { } fee } && fee != 0m)
                    {
                        o.ExecutedCommission = fee;
                        o.UpdatedAt = ctx.NowUtc;
                        await _orders.UpdateAsync(o);
                    }
                }
                catch (TInvestGatewayException ex)
                {
                    _logger.LogInformation("Execution: commission re-read of {ClientOrderId} unavailable ({Code})", o.ClientOrderId, ex.Code);
                }
            }
        }

        var filled = lineOrders.Sum(o => o.LotsFilled);
        var last = lineOrders.OrderByDescending(o => o.Attempt).FirstOrDefault();
        await OnLineTerminalAsync(plan, line, orders, filled < line.QuantityLots ? last?.ErrorMessage ?? "исполнено не полностью" : null, ctx, ct);
    }

    private async Task FinalizeAsync(TradePlan plan, List<TradeOrder> orders, Ctx ctx, CancellationToken ct)
    {
        var states = plan.Lines.Select(l => (Line: l, State: LineState(l, orders))).ToList();
        if (states.Any(s => !s.State.Terminal) || plan.Lines.Any(l => FeePending(l, orders))) return;

        var outcome = PlanExecutionOutcome.Resolve(states.Select(s => (s.Line.QuantityLots, s.State.FilledLots)).ToList());
        var note = outcome switch
        {
            PlanStatuses.Done => "исполнен",
            PlanStatuses.PartiallyDone => "исполнен частично",
            _ => "не исполнен",
        };
        if (!await _plans.SetExecutionStatusAsync(ctx.UserId, plan.Id, [PlanStatuses.Submitting], outcome, WithNote(plan.SummaryJson, note))) return;
        plan.Status = outcome;
        ctx.Result.PlansFinished++;
        _logger.LogInformation("Execution: plan {PlanId} finished: {Status}", plan.Id, outcome);
        await _notifier.NotifyAsync(ctx.UserId, new BotMessage(ExecutionMessages.PlanFinished(plan, outcome)), ct);
        if (states.Any(s => s.State.FilledLots > 0)) _sync.Request();
    }

    /// <summary>
    /// Ворота закрыты (или токен не прошёл сверку): НИЧЕГО не отправляем. Неначатые планы → Failed с причиной; у исполняемых висящие заявки снимаются (снятие — не
    /// отправка, оно уменьшает риск), неначатые строки → Failed, план закрывается итогом. Снять нельзя (нет токена, чужой счёт, брокер отказал) — одно уведомление
    /// на заявку «снимите её в терминале брокера», план остаётся Submitting и попытки повторяются каждый тик.
    /// </summary>
    private async Task HaltAsync(ulong userId, Account? primary, IReadOnlyList<TradePlan> plans, string reason, DateTime nowUtc, ExecutionTickResult result, CancellationToken ct)
    {
        foreach (var plan in plans)
        {
            if (plan.Status != PlanStatuses.Submitting)
            {
                if (await _plans.SetExecutionStatusAsync(userId, plan.Id, Startable, PlanStatuses.Failed, WithNote(plan.SummaryJson, "не исполнен: " + reason)))
                    await _notifier.NotifyAsync(userId, new BotMessage(ExecutionMessages.PlanNotStarted(plan, reason)), ct);
                continue;
            }

            var orders = (await _orders.ListByPlanAsync(plan.Id)).ToList();
            var ctx = new Ctx(userId, primary?.Id ?? 0, primary?.BrokerAccountId ?? string.Empty, nowUtc, result);
            if (await StopLinesAsync(plan, orders, TradeOrderErrorCodes.Halted, reason, ctx, ct)) await FinalizeAsync(plan, orders, ctx, ct);
        }
    }

    /// <summary>Снимает активные заявки и закрывает незавершённые строки кодом <paramref name="code"/>. false — что-то снять не удалось (повтор на следующем тике).</summary>
    private async Task<bool> StopLinesAsync(TradePlan plan, List<TradeOrder> orders, string code, string reason, Ctx ctx, CancellationToken ct)
    {
        var allStopped = true;
        foreach (var line in OrderPricing.ExecutionOrder(plan.Lines))
        {
            var state = LineState(line, orders);
            if (state.Terminal)
            {
                if (FeePending(line, orders)) await RecheckFeeAsync(plan, line, orders, ctx, ct);
                continue;
            }

            if (state.Active is { } active)
            {
                if (active.Status == TradeOrderStatuses.New)
                {
                    var refreshed = await RefreshAsync(active, line, ctx, ct);
                    if (refreshed != RefreshOutcome.Updated)
                    {
                        // Заявка могла дойти до брокера, а сверить её нельзя — пусть пользователь проверит терминал (одно уведомление).
                        if (refreshed == RefreshOutcome.Impossible) await AlertCancelImpossibleAsync(active, line, ctx, ct);
                        allStopped = false;
                        continue;
                    }
                }

                if (TradeOrderStatuses.IsActive(active.Status))
                {
                    if (await CancelAsync(active, line, TradeOrderStatuses.Cancelled, code, reason, ctx, ct) != CancelOutcome.Done)
                    {
                        _logger.LogWarning("Execution: could not cancel order {ClientOrderId} of plan {PlanId} while stopping", active.ClientOrderId, plan.Id);
                        allStopped = false;
                        continue;
                    }
                }

                if (LineState(line, orders).Terminal)
                {
                    await OnLineTerminalAsync(plan, line, orders, reason, ctx, ct);
                    continue;
                }
            }

            await RecordFailureAsync(plan, line, orders, (LineState(line, orders).Last?.Attempt ?? 0) + 1,
                code == TradeOrderErrorCodes.UserCancel ? TradeOrderStatuses.Cancelled : TradeOrderStatuses.Failed, code, reason, ctx, ct);
        }

        return allStopped;
    }

    // ─── Вспомогательное ───────────────────────────────────────────────────────────────────

    private bool IsClosingWindow(TradePlanLine line, InstrumentTradingMeta? meta, DateTime nowUtc)
    {
        var market = TradingHours.MarketOf(Brokers.TInvest, line.Ticker, meta?.ClassCode);
        if (!TradingHours.IsOpen(market, nowUtc, _policy)) return true;
        var close = TradingHours.SessionCloseUtc(market, nowUtc, _policy);
        return close is null || nowUtc >= close.Value - Opt.CancelBeforeClose;
    }

    private static DateTime MoscowDayStartUtc(DateTime nowUtc) =>
        DateTime.SpecifyKind(TradingHours.ToMoscow(nowUtc).Date - TimeSpan.FromHours(3), DateTimeKind.Utc);

    private static string NormalizeCurrency(string currency)
    {
        var upper = currency.Trim().ToUpperInvariant();
        return upper == "RUR" ? "RUB" : upper;
    }

    private static string? Truncate(string? value, int max) => value is null || value.Length <= max ? value : value[..max];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static string? WithNote(string? summaryJson, string note)
    {
        if (string.IsNullOrWhiteSpace(summaryJson)) return JsonSerializer.Serialize(new TradePlanSummary { ExecutionNote = note }, Json);
        try
        {
            var summary = JsonSerializer.Deserialize<TradePlanSummary>(summaryJson, Json);
            return summary is null ? summaryJson : JsonSerializer.Serialize(summary with { ExecutionNote = note }, Json);
        }
        catch (JsonException)
        {
            return summaryJson;
        }
    }
}

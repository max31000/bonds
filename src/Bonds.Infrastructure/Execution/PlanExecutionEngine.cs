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
/// </para>
/// </summary>
public sealed class PlanExecutionEngine
{
    private readonly ITradePlanRepository _plans;
    private readonly ITradeOrderRepository _orders;
    private readonly IInstrumentTradingMetaRepository _meta;
    private readonly IAccountRepository _accounts;
    private readonly IUserCredentialRepository _credentialRecords;
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

        if (reject is not null)
        {
            if (await _plans.SetExecutionStatusAsync(ctx.UserId, plan.Id, Startable, toStatus, WithNote(plan.SummaryJson, "не исполнен: " + reject)))
                await _notifier.NotifyAsync(ctx.UserId, new BotMessage(ExecutionMessages.PlanNotStarted(plan, reject)), ct);
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

        var metas = (await _meta.GetByKeysAsync(Brokers.TInvest, plan.Lines.Select(l => l.InstrumentKey).Distinct(StringComparer.OrdinalIgnoreCase).ToList()))
            .GroupBy(m => m.InstrumentKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var line in OrderPricing.ExecutionOrder(plan.Lines))
        {
            if (LineState(line, orders).Terminal) continue;
            var terminal = await AdvanceLineAsync(plan, line, orders, metas.GetValueOrDefault(line.InstrumentKey), ctx, ct);
            if (!terminal) return; // строка в работе — следующая ждёт (по одной строке, продажи раньше покупок)
        }

        await FinalizeAsync(plan, orders, ctx, ct);
    }

    private static LineExecutionState LineState(TradePlanLine line, IEnumerable<TradeOrder> orders) =>
        LineExecutionState.From(line, orders.Where(o => o.LineNo == line.LineNo).ToList());

    /// <summary>Продвигает строку на шаг. true — строка завершена (можно переходить к следующей).</summary>
    private async Task<bool> AdvanceLineAsync(TradePlan plan, TradePlanLine line, List<TradeOrder> orders, InstrumentTradingMeta? meta, Ctx ctx, CancellationToken ct)
    {
        var state = LineState(line, orders);
        var closing = IsClosingWindow(line, meta, ctx.NowUtc);

        if (state.Active is { } active)
        {
            if (!await RefreshAsync(active, ctx, ct)) return false;
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
                        if (!await CancelAsync(active, TradeOrderStatuses.Cancelled, TradeOrderErrorCodes.SessionClose, "снята перед закрытием сессии", ctx, ct)) return false;
                        if (active.Status != TradeOrderStatuses.Filled)
                        {
                            await OnLineTerminalAsync(plan, line, orders, "снята перед закрытием сессии", ctx, ct);
                            return true;
                        }

                        break;
                    }

                    if (ctx.NowUtc - active.CreatedAt < Opt.RepriceAfter) return false;

                    var last = active.Attempt >= Opt.MaxAttempts;
                    if (!await CancelAsync(
                            active, last ? TradeOrderStatuses.Expired : TradeOrderStatuses.Cancelled, last ? TradeOrderErrorCodes.Expired : TradeOrderErrorCodes.Reprice,
                            last ? "не исполнена за отведённые попытки" : "перевыставление", ctx, ct))
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

        decimal? autoUsed = null;
        var autoPlanBuys = 0m;
        if (plan.DecidedVia == DecidedVia.Auto)
        {
            autoUsed = await _plans.SumAutoApprovedRubSinceAsync(ctx.UserId, MoscowDayStartUtc(ctx.NowUtc), plan.Id);
            autoPlanBuys = plan.Lines.Where(l => l.Side == PlanSides.Buy && string.Equals(l.Currency, "RUB", StringComparison.OrdinalIgnoreCase)).Sum(l => l.EstAmount);
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
            Apply(order, posted, ctx.NowUtc);
            await _orders.UpdateAsync(order);
        }
        catch (TInvestGatewayException ex) when (ex.Kind == GatewayErrorKind.Rejected)
        {
            order.Status = ex.Code == "gate_closed" ? TradeOrderStatuses.Failed : TradeOrderStatuses.Rejected;
            order.ErrorCode = ex.Code == "gate_closed" ? TradeOrderErrorCodes.Halted : TradeOrderErrorCodes.BrokerRejected;
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

    /// <summary>Обновляет заявку по брокеру. false — состояние неизвестно (сбой сети или ещё не подтвердилась) — ждём следующего тика.</summary>
    private async Task<bool> RefreshAsync(TradeOrder order, Ctx ctx, CancellationToken ct)
    {
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
            return false;
        }

        if (state is null)
        {
            var since = order.Status == TradeOrderStatuses.New ? order.CreatedAt : order.UpdatedAt;
            if (ctx.NowUtc - since < Opt.UnconfirmedOrderTimeout) return false;

            // Брокер заявку не знает: отправки не было или она потеряна. Повторно НЕ отправляем — закрываем строку и просим проверить вручную.
            order.Status = TradeOrderStatuses.Failed;
            order.ErrorCode = TradeOrderErrorCodes.UnknownState;
            order.ErrorMessage = "Состояние заявки не подтвердилось у брокера — повторно не отправлялась, проверьте терминал";
            order.UpdatedAt = ctx.NowUtc;
            await _orders.UpdateAsync(order);
            return true;
        }

        Apply(order, state, ctx.NowUtc);
        await _orders.UpdateAsync(order);
        return true;
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
    /// <c>Filled</c>. false — снять не удалось (сбой сети), повтор на следующем тике.
    /// </summary>
    private async Task<bool> CancelAsync(TradeOrder order, string finalStatus, string code, string message, Ctx ctx, CancellationToken ct)
    {
        try
        {
            await _gateway.CancelOrderAsync(ctx.UserId, ctx.BrokerAccountId, order.ClientOrderId, ct);
            ctx.Result.OrdersCancelled++;
        }
        catch (TInvestGatewayException ex) when (ex.Kind == GatewayErrorKind.Transient)
        {
            _logger.LogWarning("Execution: cancel of {ClientOrderId} failed ({Code}) — will retry", order.ClientOrderId, ex.Code);
            return false;
        }
        catch (TInvestGatewayException ex)
        {
            // Отказ снять (уже исполнена/снята) — истину покажет сверка ниже.
            _logger.LogInformation("Execution: cancel of {ClientOrderId} refused ({Code})", order.ClientOrderId, ex.Code);
        }

        try
        {
            if (await _gateway.GetOrderStateAsync(ctx.UserId, ctx.BrokerAccountId, order.ClientOrderId, ct) is { } state) Apply(order, state, ctx.NowUtc);
        }
        catch (TInvestGatewayException ex)
        {
            _logger.LogWarning("Execution: post-cancel state of {ClientOrderId} unavailable ({Code}) — will retry", order.ClientOrderId, ex.Code);
            return false;
        }

        if (order.Status == TradeOrderStatuses.Filled)
        {
            await _orders.UpdateAsync(order);
            return true;
        }

        if (TradeOrderStatuses.IsActive(order.Status) && order.Status != TradeOrderStatuses.New)
        {
            // Брокер ещё показывает заявку активной — дождёмся подтверждения снятия.
            await _orders.UpdateAsync(order);
            return false;
        }

        order.Status = finalStatus;
        order.ErrorCode = code;
        order.ErrorMessage = message;
        order.UpdatedAt = ctx.NowUtc;
        await _orders.UpdateAsync(order);
        return true;
    }

    private static void Apply(TradeOrder order, GatewayOrderState state, DateTime nowUtc)
    {
        if (!string.IsNullOrEmpty(state.BrokerOrderId)) order.BrokerOrderId = state.BrokerOrderId;
        order.Status = state.Status;
        order.LotsFilled = Math.Max(order.LotsFilled, state.LotsExecuted);
        if (state.AvgFillPrice is { } avg) order.AvgFillPrice = avg;
        if (state.ExecutedCommission is { } fee) order.ExecutedCommission = fee;
        if (state.Status == TradeOrderStatuses.Rejected)
        {
            order.ErrorCode = TradeOrderErrorCodes.BrokerRejected;
            order.ErrorMessage = state.Message ?? "Брокер отклонил заявку";
        }

        order.UpdatedAt = nowUtc;
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
        if (filled > 0)
        {
            var commission = lineOrders.Sum(o => o.ExecutedCommission ?? 0m);
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
            var last = lineOrders.OrderByDescending(o => o.Attempt).FirstOrDefault();
            var text = last?.Status is TradeOrderStatuses.Cancelled or TradeOrderStatuses.Expired
                ? ExecutionMessages.Cancelled(plan, line, reason)
                : ExecutionMessages.Failed(plan, line, reason);
            await _notifier.NotifyAsync(ctx.UserId, new BotMessage(text), ct);
        }
    }

    private async Task FinalizeAsync(TradePlan plan, List<TradeOrder> orders, Ctx ctx, CancellationToken ct)
    {
        var states = plan.Lines.Select(l => (Line: l, State: LineState(l, orders))).ToList();
        if (states.Any(s => !s.State.Terminal)) return;

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
    /// отправка, оно уменьшает риск), неначатые строки → Failed, план закрывается итогом. Без торгового токена снять нельзя — план остаётся Submitting до следующего тика.
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
            if (state.Terminal) continue;
            if (state.Active is { } active)
            {
                if (active.Status == TradeOrderStatuses.New && !await RefreshAsync(active, ctx, ct)) { allStopped = false; continue; }
                if (TradeOrderStatuses.IsActive(active.Status))
                {
                    if (string.IsNullOrEmpty(ctx.BrokerAccountId) || !await CancelAsync(active, TradeOrderStatuses.Cancelled, code, reason, ctx, ct))
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

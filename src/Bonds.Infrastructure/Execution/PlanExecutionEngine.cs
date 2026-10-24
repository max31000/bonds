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
/// Исполнитель планов T-Invest (задача 65) и Freedom (задача 66) — один тик. Вызывает брокеров ТОЛЬКО через их торговые шлюзы (<see cref="ITInvestOrderGateway"/>,
/// <see cref="IFreedomOrderGateway"/>) — через адаптеры <see cref="IExecutionVenue"/>.
/// <para>
/// <b>Ворота по брокеру (задача 66).</b> На тик для каждого брокера из строк планов открывается <see cref="VenueSession"/> (ворота брокера + сверка торгового ключа).
/// План, у которого закрыты ворота ВСЕХ его брокеров, останавливается как раньше; в смешанном плане строки брокера с закрытыми воротами закрываются (висящие
/// снимаются), строки брокера с открытыми — исполняются. Freedom — только планы, подтверждённые ВРУЧНУЮ (<c>Approved</c>, не <c>auto</c>).
/// </para>
/// <para>
/// <b>Часы смешанного плана.</b> План стартует, только когда для КАЖДОЙ его строки с открытыми воротами брокера сессия открыта и до закрытия больше
/// <c>CancelBeforeClose</c>, — то есть в ПЕРЕСЕЧЕНИИ окон рынков строк (по умолчанию, МСК, с <c>CancelBeforeClose</c> = 10 мин: T-Invest MOEX 10:00–18:40 и
/// Freedom .EU 11:00–18:30 → 11:00–18:20; T-Invest MOEX и Freedom .US 17:30–23:00 → 17:30–18:30). Пересечения нет (план подтверждён поздно) — план ждёт до
/// <c>expires_at</c> и истекает. Строки брокера с закрытыми воротами в пересечение не входят. После старта каждая строка сверяет часы своего рынка сама (вне
/// сессии висящая заявка снимается, новая не отправляется).
/// </para>
/// <para>
/// <b>Параметры сопровождения — по брокеру</b> (<see cref="IExecutionVenue.Limits"/>): T-Invest — <c>MaxAttempts</c>/<c>RepriceAfter</c>/<c>MaxFeeRechecks</c>,
/// Freedom — <c>FreedomMaxAttempts</c> (2)/<c>FreedomRepriceAfter</c> (10 мин)/<c>FreedomMaxFeeRechecks</c> (90 тиков): у Freedom фикс $1.2 за каждую заявку с исполнением.
/// </para>
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
/// <b>Идемпотентность.</b> <c>client_order_id</c> записывается в <c>trade_orders</c> ДО отправки брокеру (T-Invest — UUID, уходит как <c>order_id</c>; Freedom —
/// числовой <c>user_order_id</c>). Заявка в статусе <c>New</c> (ответ не получен, рестарт) только сверяется (T-Invest — <c>GetOrderState</c>, затем <c>GetOrders</c>;
/// Freedom — список заявок по <c>userOrderId</c>, затем по реквизитам с уведомлением в бот); не нашлась за <c>UnconfirmedOrderTimeout</c> — <c>Failed</c>
/// «проверьте вручную», повторно НЕ отправляется никогда.
/// </para>
/// <para>
/// <b>Сопровождение.</b> Не исполнена за <c>RepriceAfter</c> — снятие и новая попытка ближе к противоположной стороне стакана в пределах дрейфа от УТВЕРЖДЁННОГО лимита
/// (<see cref="OrderPricing.PriceForAttempt"/>), не больше <c>MaxAttempts</c>, затем <c>Expired</c>. За <c>CancelBeforeClose</c> до закрытия сессии висящие снимаются.
/// Исполнение строки — сверка комиссии с моделью (<see cref="FeeReconciliation"/>), уведомление; план завершён — итог Done/PartiallyDone/Failed и синк портфеля.
/// Комиссия 0 сразу после исполнения при ненулевой модельной (брокер присылает её с задержкой) — сверка и уведомление откладываются до
/// <c>Execution:MaxFeeRechecks</c> тиков с перечитыванием состояния (<c>trade_orders.fee_rechecks</c>/<c>fee_checked_at</c>); план закрывается после сверки.
/// Freedom: окно исчерпано, а комиссия по всем сделкам так и не пришла — строка закрывается с пометкой «комиссия не сверена», БЕЗ сравнения со сметой.
/// Комиссия в другой валюте, чем строка (Freedom: USD при сделке в EUR) — со сметой не сравнивается (информационное уведомление), курс не применяется.
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
    private readonly Dictionary<string, IExecutionVenue> _venues = new(StringComparer.OrdinalIgnoreCase);

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
        ILogger<PlanExecutionEngine> logger,
        IFreedomOrderGateway? freedomGateway = null)
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
        _venues[Brokers.TInvest] = new TInvestExecutionVenue(gateway, gate, accounts, credentialRecords, logger);
        if (freedomGateway is not null) _venues[Brokers.Freedom] = new FreedomExecutionVenue(freedomGateway, gate, accounts, credentialRecords, orders, logger);
    }

    private ExecutionOptions Opt => _options.CurrentValue;

    private sealed record Ctx(ulong UserId, IReadOnlyDictionary<string, VenueSession> Sessions, DateTime NowUtc, ExecutionTickResult Result)
    {
        public VenueSession Session(TradePlanLine line) =>
            Sessions.TryGetValue(ExecutionText.NormalizeBroker(line.Broker), out var s)
                ? s
                : new VenueSession { Broker = line.Broker, HaltReason = "исполнитель работает только с T-Invest и Freedom" };
    }

    private IExecutionVenue? VenueOf(string broker) => _venues.GetValueOrDefault(ExecutionText.NormalizeBroker(broker));

    /// <summary>Попытки/перевыставление/сверка комиссии брокера строки (неизвестный брокер — общие значения).</summary>
    private VenueLimits LimitsOf(TradePlanLine line) =>
        VenueOf(line.Broker)?.Limits(Opt) ?? new VenueLimits(Opt.MaxAttempts, Opt.RepriceAfter, Opt.MaxFeeRechecks);

    private static IReadOnlyList<string> BrokersOf(TradePlan plan) =>
        plan.Lines.Select(l => ExecutionText.NormalizeBroker(l.Broker)).Distinct(StringComparer.Ordinal).ToList();

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

        var sessions = new Dictionary<string, VenueSession>(StringComparer.Ordinal);
        foreach (var broker in plans.SelectMany(BrokersOf).Distinct(StringComparer.Ordinal))
        {
            sessions[broker] = VenueOf(broker) is { } venue
                ? await venue.OpenAsync(userId, ct)
                : new VenueSession { Broker = broker, HaltReason = "исполнитель работает только с T-Invest и Freedom" };
        }

        var ctx = new Ctx(userId, sessions, nowUtc, result);
        foreach (var plan in plans)
        {
            ct.ThrowIfCancellationRequested();
            var brokers = BrokersOf(plan);
            if (brokers.Any(b => sessions[b].Unavailable))
            {
                result.Notes.Add($"plan {plan.Id}: broker check unavailable — skipped");
                continue; // проверку у брокера сделать не удалось (сеть) — ни отправок, ни остановки до следующего тика
            }

            var halted = brokers.Where(b => sessions[b].HaltReason is not null).ToList();
            if (brokers.Count > 0 && halted.Count == brokers.Count)
            {
                await HaltPlanAsync(plan, HaltReasonOf(halted.Select(b => sessions[b]).ToList()), ctx, ct);
                continue;
            }

            if (plan.Status != PlanStatuses.Submitting && !await StartAsync(plan, ctx, ct)) continue;
            await ProcessPlanAsync(plan, ctx, ct);
        }
    }

    /// <summary>Причина остановки: один брокер — его причина как есть; несколько — с префиксом брокера.</summary>
    private static string HaltReasonOf(IReadOnlyList<VenueSession> halted) =>
        halted.Count == 1 ? halted[0].HaltReason! : string.Join("; ", halted.Select(h => $"{h.Broker}: {h.HaltReason}"));

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
        else if (plan.Lines.Any(l => VenueOf(l.Broker) is null))
        {
            reject = "исполнитель работает только с T-Invest и Freedom";
        }
        else if (plan.Lines.Any(l => string.Equals(l.Broker, Brokers.TInvest, StringComparison.OrdinalIgnoreCase) && l.AccountId != ctx.Session(l).AccountId))
        {
            reject = "строки плана относятся не к основному счёту T-Invest";
        }
        else if (plan.Lines.Any(l => string.Equals(l.Broker, Brokers.Freedom, StringComparison.OrdinalIgnoreCase) && l.AccountId != ctx.Session(l).AccountId))
        {
            reject = "строки плана относятся не к счёту Freedom торгового ключа";
        }
        else if (plan.Lines.Any(l => string.Equals(l.Broker, Brokers.Freedom, StringComparison.OrdinalIgnoreCase))
                 && (plan.Status != PlanStatuses.Approved || plan.DecidedVia == DecidedVia.Auto))
        {
            reject = "Freedom исполняется только после ручного подтверждения (авто-исполнение Freedom выключено)";
        }
        else if (plan.Lines.FirstOrDefault(l => string.Equals(l.Broker, Brokers.Freedom, StringComparison.OrdinalIgnoreCase)
                                                && FreedomOrderRules.TickerRejection(l.Ticker) is not null) is { } badTicker)
        {
            reject = FreedomOrderRules.TickerRejection(badTicker.Ticker);
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
        if (plan.Lines.Any(l => ctx.Session(l).Live && IsClosingWindow(l, MetaOf(metas, l), ctx.NowUtc)))
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

            // Ворота брокера этой строки закрыты (смешанный план, задача 66) — строку останавливаем, остальные продолжают.
            if (ctx.Session(line).HaltReason is { } halt)
            {
                if (!await StopLineAsync(plan, line, orders, TradeOrderErrorCodes.Halted, halt, ctx, ct)) return;
                continue;
            }

            var terminal = await AdvanceLineAsync(plan, line, orders, MetaOf(metas, line), ctx, ct);
            if (!terminal) return; // строка в работе — следующая ждёт (по одной строке, продажи раньше покупок)
        }

        await FinalizeAsync(plan, orders, ctx, ct);
    }

    /// <summary>Торговые метаданные строк плана по (брокер, ключ бумаги).</summary>
    private async Task<Dictionary<string, InstrumentTradingMeta>> MetasAsync(TradePlan plan)
    {
        var result = new Dictionary<string, InstrumentTradingMeta>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in plan.Lines.GroupBy(l => ExecutionText.NormalizeBroker(l.Broker)))
        {
            var keys = group.Select(l => l.InstrumentKey).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var m in await _meta.GetByKeysAsync(group.Key, keys) ?? [])
                result.TryAdd(group.Key + "|" + m.InstrumentKey, m);
        }

        return result;
    }

    private static InstrumentTradingMeta? MetaOf(Dictionary<string, InstrumentTradingMeta> metas, TradePlanLine line) =>
        metas.GetValueOrDefault(ExecutionText.NormalizeBroker(line.Broker) + "|" + line.InstrumentKey);

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
        var limits = LimitsOf(line);

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
                    if (!await ClassifyExternalCancelAsync(active, closing, limits, ctx)) { await OnLineTerminalAsync(plan, line, orders, active.ErrorMessage ?? "заявка снята", ctx, ct); return true; }
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

                    if (ctx.NowUtc - active.CreatedAt < limits.RepriceAfter) return false;

                    var last = active.Attempt >= limits.MaxAttempts;
                    if (await CancelAsync(
                            active, line, last ? TradeOrderStatuses.Expired : TradeOrderStatuses.Cancelled, last ? TradeOrderErrorCodes.Expired : TradeOrderErrorCodes.Reprice,
                            last ? "не исполнена за отведённые попытки" : "перевыставление", ctx, ct) != CancelOutcome.Done)
                        return false;
                    if (last && active.Status != TradeOrderStatuses.Filled)
                    {
                        await OnLineTerminalAsync(plan, line, orders, $"не исполнена за {limits.MaxAttempts} попытки — остаток снят", ctx, ct);
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
        var limits = LimitsOf(line);
        if (attempt > limits.MaxAttempts)
        {
            await RecordFailureAsync(plan, line, orders, attempt, TradeOrderStatuses.Expired, TradeOrderErrorCodes.Expired, "исчерпаны попытки", ctx, ct);
            return true;
        }

        var venue = VenueOf(line.Broker)!;
        var session = ctx.Session(line);

        // Отказы до любой сети (Freedom: .ITS и не-.US/.EU тикеры, лотность, авто-подтверждение).
        if (venue.LineRejection(plan, line) is { } rejection)
        {
            await RecordFailureAsync(plan, line, orders, attempt, TradeOrderStatuses.Failed, TradeOrderErrorCodes.Preflight, rejection, ctx, ct);
            return true;
        }

        var instrumentId = meta is null ? null : venue.InstrumentId(line, meta);
        if (meta is null || instrumentId is null)
        {
            await RecordFailureAsync(plan, line, orders, attempt, TradeOrderStatuses.Failed, TradeOrderErrorCodes.Preflight,
                venue.Broker == Brokers.TInvest ? "нет торговых метаданных бумаги (uid)" : "нет торговых метаданных бумаги", ctx, ct);
            return true;
        }

        var step = OrderPricing.StepOf(line, meta);
        if (step is null)
        {
            await RecordFailureAsync(plan, line, orders, attempt, TradeOrderStatuses.Failed, TradeOrderErrorCodes.Preflight, "неизвестен шаг цены бумаги", ctx, ct);
            return true;
        }

        // Ворота брокера ещё раз — непосредственно перед отправкой (пауза могла включиться посреди тика).
        var gate = await venue.GateAsync(ctx.UserId);
        if (!gate.Live)
        {
            await RecordFailureAsync(plan, line, orders, attempt, TradeOrderStatuses.Failed, TradeOrderErrorCodes.Halted, string.Join("; ", gate.Reasons), ctx, ct);
            return true;
        }

        VenueMarket market;
        try
        {
            market = await venue.MarketAsync(session, ctx.UserId, line, meta, instrumentId, ctx.NowUtc, ct);
        }
        catch (ExecutionGatewayException ex) when (ex.Kind == GatewayErrorKind.Transient)
        {
            _logger.LogWarning("Execution: market data unavailable for plan {PlanId} line {LineNo} ({Code})", plan.Id, line.LineNo, ex.Code);
            return false;
        }
        catch (ExecutionGatewayException ex)
        {
            await RecordFailureAsync(plan, line, orders, attempt, TradeOrderStatuses.Failed, TradeOrderErrorCodes.Preflight, ex.Message, ctx, ct);
            return true;
        }

        var isBond = OrderPricing.IsPointsPriced(line);
        var approved = OrderPricing.ApprovedLimit(line);
        var drift = Opt.DriftFor(isBond);
        var price = OrderPricing.PriceForAttempt(line.Side, approved, market.Bid, market.Ask, step.Value, drift, attempt, limits.MaxAttempts, state.Last?.LimitPrice);
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
            Bid = market.Bid,
            Ask = market.Ask,
            QuoteTimeUtc = market.QuoteTimeUtc,
            NormalTrading = market.NormalTrading,
            LimitOrderAvailable = market.LimitOrderAvailable,
            ApiTradeAvailable = market.ApiTradeAvailable,
            SideAvailable = line.Side == PlanSides.Sell ? meta.SellAvailable : meta.BuyAvailable,
            AvailableMoney = market.AvailableMoney,
            RequiredMoney = OrderPricing.RequiredMoney(line, price, lots),
            AvailablePieces = market.AvailablePieces,
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
            ClientOrderId = venue.NewClientOrderId(),
            Broker = venue.Broker,
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
            var posted = await venue.PostAsync(session, ctx.UserId, line, instrumentId, order, ct);
            ctx.Result.OrdersPosted++;
            OrderStateMapping.Apply(order, posted, line.LotSize, ctx.NowUtc);
            await _orders.UpdateAsync(order);
        }
        catch (ExecutionGatewayException ex) when (ex.Kind == GatewayErrorKind.Rejected)
        {
            // Определённый отказ — заявки у брокера нет. gate_closed (ворота закрылись), price_grid (цена не легла в Quotation) и отказы шлюза Freedom до сети
            // (its_forbidden, ticker_forbidden, margin_forbidden, no_trade_key) — до брокера не дошло: Failed.
            var preNetwork = ex.Code is "its_forbidden" or "ticker_forbidden" or "margin_forbidden" or "no_trade_key";
            var notSent = ex.Code is "gate_closed" or TradeOrderErrorCodes.PriceGrid || preNetwork;
            order.Status = notSent ? TradeOrderStatuses.Failed : TradeOrderStatuses.Rejected;
            order.ErrorCode = ex.Code switch
            {
                "gate_closed" => TradeOrderErrorCodes.Halted,
                TradeOrderErrorCodes.PriceGrid => TradeOrderErrorCodes.PriceGrid,
                _ when preNetwork => TradeOrderErrorCodes.Preflight,
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
        var session = ctx.Session(line);
        if (!session.CanManageOrders || VenueOf(line.Broker) is not { } venue) return RefreshOutcome.Impossible;
        GatewayOrderState? state;
        try
        {
            state = await venue.StateAsync(session, ctx.UserId, order, line, order.Status == TradeOrderStatuses.New, ct);
        }
        catch (ExecutionGatewayException ex)
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

        var adopted = state.MatchedByAttributes && string.IsNullOrEmpty(order.BrokerOrderId);
        OrderStateMapping.Apply(order, state, line.LotSize, ctx.NowUtc);
        await _orders.UpdateAsync(order);
        if (adopted)
        {
            // Опознана не по номеру, а по реквизитам — пусть пользователь убедится в терминале, что это заявка сервиса, а не ручная.
            await _notifier.NotifyAsync(
                ctx.UserId, new BotMessage(ExecutionMessages.OrderAdoptedByAttributes(line.Ticker, order.LotsRequested, order.LimitPrice, order.Currency)), ct);
        }

        return RefreshOutcome.Updated;
    }

    /// <summary>Снятие брокером без нашей команды. true — строка продолжается перевыставлением; false — окончательно.</summary>
    private async Task<bool> ClassifyExternalCancelAsync(TradeOrder order, bool closing, VenueLimits limits, Ctx ctx)
    {
        if (!closing && order.Attempt < limits.MaxAttempts && order.ErrorCode is null or TradeOrderErrorCodes.Reprice)
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
        var session = ctx.Session(line);
        if (!session.CanManageOrders || VenueOf(line.Broker) is not { } venue) return await AlertCancelImpossibleAsync(order, line, ctx, ct);

        var refused = false;
        try
        {
            await venue.CancelAsync(session, ctx.UserId, order, ct);
            ctx.Result.OrdersCancelled++;
        }
        catch (ExecutionGatewayException ex) when (ex.Kind == GatewayErrorKind.Transient)
        {
            _logger.LogWarning("Execution: cancel of {ClientOrderId} failed ({Code}) — will retry", order.ClientOrderId, ex.Code);
            return CancelOutcome.Retry;
        }
        catch (ExecutionGatewayException ex)
        {
            // Отказ снять (уже исполнена/снята — или токена нет/чужой счёт) — истину покажет сверка ниже.
            refused = true;
            _logger.LogInformation("Execution: cancel of {ClientOrderId} refused ({Code})", order.ClientOrderId, ex.Code);
        }

        try
        {
            if (await venue.StateAsync(session, ctx.UserId, order, line, false, ct) is { } state)
                OrderStateMapping.Apply(order, state, line.LotSize, ctx.NowUtc);
        }
        catch (ExecutionGatewayException ex) when (ex.Kind == GatewayErrorKind.Transient)
        {
            _logger.LogWarning("Execution: post-cancel state of {ClientOrderId} unavailable ({Code}) — will retry", order.ClientOrderId, ex.Code);
            return CancelOutcome.Retry;
        }
        catch (ExecutionGatewayException ex)
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
            ClientOrderId = Guid.NewGuid().ToString("D"), // запись причины — брокеру не отправляется никогда
            Broker = ExecutionText.NormalizeBroker(line.Broker),
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
            // Freedom: комиссия только в выписке — перечитываем её при каждом завершении строки (и каждом отложенном тике), пока не придут все строки комиссии.
            var venue = VenueOf(line.Broker);
            VenueFees? fees = null;
            if (venue is { FeesFromStatement: true } && lastOrder is { FeeCheckedAt: null })
            {
                fees = await venue.RefreshFeesAsync(ctx.Session(line), ctx.UserId, line, lineOrders, ctx.NowUtc, ct);
                foreach (var o in lineOrders.Where(o => o.LotsFilled > 0)) await _orders.UpdateAsync(o);
            }

            var commission = lineOrders.Sum(o => o.ExecutedCommission ?? 0m);

            // Комиссия ещё не пришла: T-Invest — 0 сразу после исполнения при ненулевой модельной; Freedom — нет строк комиссии по всем сделкам. Откладываем сверку и
            // уведомление (перечитаем на следующих тиках), не больше MaxFeeRechecks брокера (Freedom — FreedomMaxFeeRechecks).
            var maxRechecks = LimitsOf(line).MaxFeeRechecks;
            var feeMissing = venue is { FeesFromStatement: true }
                ? fees is not { Complete: true }
                : commission == 0m && FeeReconciliation.IsMismatch(line, filled, 0m, Opt.FeeMismatchThreshold);
            if (feeMissing && lastOrder is { FeeCheckedAt: null } && lastOrder.FeeRechecks < maxRechecks)
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

            // Окно сверки исчерпано, а выписка так и не дала комиссию по всем сделкам (Freedom) — «комиссия не сверена», сравнения со сметой нет (ложное расхождение).
            var notReconciled = venue is { FeesFromStatement: true } && fees is not { Complete: true };

            // Валюта комиссии: у Freedom обычно USD даже для сделки в EUR. Одна валюта, совпадающая с валютой строки, — сверка со сметой; иначе — без сравнения.
            var feeCurrencies = lineOrders.Where(o => o.LotsFilled > 0 && o.ExecutedCommission is not null)
                .Select(o => NormalizeCurrency(o.EffectiveCommissionCurrency)).Distinct(StringComparer.Ordinal).ToList();
            var commissionCurrency = feeCurrencies.Count == 1 ? feeCurrencies[0] : feeCurrencies.Count == 0 ? currency : TradeOrder.MixedCommissionCurrency;
            var sameCurrency = string.Equals(commissionCurrency, currency, StringComparison.Ordinal);

            var weighted = lineOrders.Where(o => o.LotsFilled > 0 && o.AvgFillPrice is not null).ToList();
            decimal? avg = weighted.Count == 0 ? null : weighted.Sum(o => o.AvgFillPrice!.Value * o.LotsFilled) / weighted.Sum(o => o.LotsFilled);
            await _notifier.NotifyAsync(
                ctx.UserId, new BotMessage(ExecutionMessages.Filled(plan, line, filled, avg, commission, currency, commissionCurrency, notReconciled)), ct);

            if (notReconciled)
            {
                _logger.LogWarning("Execution: fee of plan {PlanId} line {LineNo} not reconciled — statement incomplete after {Rechecks} rechecks", plan.Id, line.LineNo, maxRechecks);
            }
            else if (!sameCurrency)
            {
                _logger.LogInformation(
                    "Execution: fee of plan {PlanId} line {LineNo} is in {FeeCurrency}, line in {Currency} — not compared", plan.Id, line.LineNo, commissionCurrency, currency);
                await _notifier.NotifyAsync(
                    ctx.UserId, new BotMessage(ExecutionMessages.FeeNotCompared(plan, line, commission, commissionCurrency, currency, fees?.Note)), ct);
            }
            else
            {
                // Модель: Freedom — по ФАКТИЧЕСКОМУ маршруту из выписки (FreedomFeeModel), иначе — оценка плана (est_fee × доля исполненного).
                var expected = fees?.ExpectedFee ?? FeeReconciliation.ExpectedFee(line, filled);
                if (Math.Abs(commission - expected) > Opt.FeeMismatchThreshold)
                {
                    _logger.LogWarning("Execution: fee mismatch on plan {PlanId} line {LineNo}: expected {Expected}, actual {Actual}", plan.Id, line.LineNo, expected, commission);
                    await _notifier.NotifyAsync(ctx.UserId, new BotMessage(ExecutionMessages.FeeMismatch(plan, line, expected, commission, currency, fees?.Note)), ct);
                }
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
        if (VenueOf(line.Broker) is { FeesFromStatement: false } venue)
        {
            await venue.RefreshFeesAsync(ctx.Session(line), ctx.UserId, line, lineOrders, ctx.NowUtc, ct);
            foreach (var o in lineOrders.Where(o => o.LotsFilled > 0)) await _orders.UpdateAsync(o);
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
    /// Ворота ВСЕХ брокеров плана закрыты (или ключ не прошёл сверку): НИЧЕГО не отправляем. Неначатый план → Failed с причиной; у исполняемого висящие заявки
    /// снимаются (снятие — не отправка, оно уменьшает риск), неначатые строки → Failed, план закрывается итогом. Снять нельзя (нет ключа, чужой счёт, брокер отказал) —
    /// одно уведомление на заявку «снимите её в терминале брокера», план остаётся Submitting и попытки повторяются каждый тик.
    /// </summary>
    private async Task HaltPlanAsync(TradePlan plan, string reason, Ctx ctx, CancellationToken ct)
    {
        if (plan.Status != PlanStatuses.Submitting)
        {
            if (await _plans.SetExecutionStatusAsync(ctx.UserId, plan.Id, Startable, PlanStatuses.Failed, WithNote(plan.SummaryJson, "не исполнен: " + reason)))
                await _notifier.NotifyAsync(ctx.UserId, new BotMessage(ExecutionMessages.PlanNotStarted(plan, reason)), ct);
            return;
        }

        var orders = (await _orders.ListByPlanAsync(plan.Id)).ToList();
        if (await StopLinesAsync(plan, orders, TradeOrderErrorCodes.Halted, reason, ctx, ct)) await FinalizeAsync(plan, orders, ctx, ct);
    }

    /// <summary>Снимает активные заявки и закрывает незавершённые строки кодом <paramref name="code"/>. false — что-то снять не удалось (повтор на следующем тике).</summary>
    private async Task<bool> StopLinesAsync(TradePlan plan, List<TradeOrder> orders, string code, string reason, Ctx ctx, CancellationToken ct)
    {
        var allStopped = true;
        foreach (var line in OrderPricing.ExecutionOrder(plan.Lines))
        {
            if (!await StopLineAsync(plan, line, orders, code, reason, ctx, ct)) allStopped = false;
        }

        return allStopped;
    }

    /// <summary>Останавливает одну строку: снимает её активную заявку и закрывает строку кодом <paramref name="code"/>. false — снять/сверить не удалось (повтор на следующем тике).</summary>
    private async Task<bool> StopLineAsync(TradePlan plan, TradePlanLine line, List<TradeOrder> orders, string code, string reason, Ctx ctx, CancellationToken ct)
    {
        var state = LineState(line, orders);
        if (state.Terminal)
        {
            if (FeePending(line, orders)) await RecheckFeeAsync(plan, line, orders, ctx, ct);
            return true;
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
                    return false;
                }
            }

            if (TradeOrderStatuses.IsActive(active.Status))
            {
                if (await CancelAsync(active, line, TradeOrderStatuses.Cancelled, code, reason, ctx, ct) != CancelOutcome.Done)
                {
                    _logger.LogWarning("Execution: could not cancel order {ClientOrderId} of plan {PlanId} while stopping", active.ClientOrderId, plan.Id);
                    return false;
                }
            }

            if (LineState(line, orders).Terminal)
            {
                await OnLineTerminalAsync(plan, line, orders, reason, ctx, ct);
                return true;
            }
        }

        await RecordFailureAsync(plan, line, orders, (LineState(line, orders).Last?.Attempt ?? 0) + 1,
            code == TradeOrderErrorCodes.UserCancel ? TradeOrderStatuses.Cancelled : TradeOrderStatuses.Failed, code, reason, ctx, ct);
        return true;
    }

    // ─── Вспомогательное ───────────────────────────────────────────────────────────────────

    private bool IsClosingWindow(TradePlanLine line, InstrumentTradingMeta? meta, DateTime nowUtc)
    {
        var market = VenueOf(line.Broker)?.MarketOf(line, meta) ?? TradingHours.MarketOf(line.Broker, line.Ticker, meta?.ClassCode);
        if (!TradingHours.IsOpen(market, nowUtc, _policy)) return true;
        var close = TradingHours.SessionCloseUtc(market, nowUtc, _policy);
        return close is null || nowUtc >= close.Value - Opt.CancelBeforeClose;
    }

    private static DateTime MoscowDayStartUtc(DateTime nowUtc) =>
        DateTime.SpecifyKind(TradingHours.ToMoscow(nowUtc).Date - TimeSpan.FromHours(3), DateTimeKind.Utc);

    private static string NormalizeCurrency(string currency) => ExecutionText.NormalizeCurrency(currency);

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

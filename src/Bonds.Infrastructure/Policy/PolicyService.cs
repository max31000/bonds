using System.Text.Json;
using Bonds.Core.Capital;
using Bonds.Core.Execution;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Policy;
using Bonds.Core.Telegram;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Bonds.Infrastructure.Execution;

namespace Bonds.Infrastructure.Policy;

/// <summary>Параметры ручных планов и предпросмотра (<c>POST /api/plans/*</c>). Суммы — в валюте портфеля, <see cref="Fraction"/> — ДОЛЯ портфеля (0.1 = 10%).</summary>
public sealed record PlanParams(
    decimal? Amount = null, decimal? Fraction = null, string? Portfolio = null, IReadOnlyList<string>? IncludeBaskets = null, string? Code = null);

/// <summary>Ошибка в параметрах запроса плана (API отдаёт 422).</summary>
public sealed class PolicyValidationException : Exception
{
    public PolicyValidationException(string message) : base(message) { }
}

public enum PlanDecisionKind
{
    Ok,
    NotFound,

    /// <summary>План уже решён или находится в статусе, из которого решение невозможно.</summary>
    Conflict,

    /// <summary>План просрочен (стал Expired) — решение не принято.</summary>
    Expired,
}

public sealed record PlanDecisionResult(PlanDecisionKind Kind, TradePlan? Plan);

/// <summary>
/// Построение, сохранение и решения по планам (задача 63). Здесь брокеров не вызывают: <c>approve</c> переводит план в <c>Approved</c>. <c>dry_run</c> плана
/// (задача 65) = НЕ живой режим пользователя в момент сохранения (<see cref="IExecutionGateResolver"/>: env + режим live + проверенный торговый токен + нет паузы)
/// по КАЖДОМУ брокеру строк плана (задача 66: T-Invest — <c>tinvest_trade</c>, Freedom — <c>freedom_trade</c>), ИЛИ в плане есть строки брокера без исполнителя,
/// ИЛИ это <c>AutoApproved</c>-план со строками Freedom (Freedom исполняется только после ручного подтверждения). dry-run план после approve помечается
/// «не исполнен: dry-run»; план с <c>dry_run = 0</c> исполняет <c>PlanExecutionEngine</c>, который перепроверяет ворота сам.
/// <para>
/// Ручные запросы (inflow/withdraw/exit-basket) и предпросмотр игнорируют часы торгов, но помечают цены «возможно устарели»; план, созданный вручную, всегда
/// <c>PendingApproval</c> (решение по нему принимает пользователь, а не лимиты авто-подтверждения).
/// </para>
/// </summary>
public sealed class PolicyService
{
    public const string DryRunNote = "не исполнен: dry-run";
    public const string QueuedNote = "передан исполнителю";
    public const string LiveNote = "исполнение включено: после подтверждения заявки уйдут брокеру (T-Invest или Freedom — по строкам плана)";
    public const string NotExecutedExpiredNote = "не исполнен: срок действия истёк";
    public const string HoldOnlyNote = "удержание под вывод: заявок нет, деньги отложены до «Деньги выведены» (/withdrawn)";

    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly PolicyInputBuilder _builder;
    private readonly ITradePlanRepository _plans;
    private readonly IExecutionGateResolver _gate;
    private readonly PolicyOptions _policy;
    private readonly IOptionsMonitor<ExecutionOptions> _execution;
    private readonly ILogger<PolicyService> _logger;

    public PolicyService(
        PolicyInputBuilder builder, ITradePlanRepository plans, IExecutionGateResolver gate, IOptions<PolicyOptions> policy, IOptionsMonitor<ExecutionOptions> execution,
        ILogger<PolicyService> logger)
    {
        _builder = builder;
        _plans = plans;
        _gate = gate;
        _policy = policy.Value;
        _execution = execution;
        _logger = logger;
    }

    /// <summary>Строит план по правилу БЕЗ сохранения (предпросмотр). Любое время суток; цены помечаются «возможно устарели» вне часов торгов.</summary>
    public async Task<PolicyOutcome> PreviewAsync(ulong userId, string rule, PlanParams p, DateTime nowUtc, CancellationToken ct = default) =>
        await EvaluateAsync(userId, rule, p, nowUtc, ignoreHours: true, ct);

    /// <summary>Строит и СОХРАНЯЕТ ручной план (всегда PendingApproval). Null — правило не дало плана (причины — в <see cref="PolicyOutcome.Notes"/>).</summary>
    public async Task<(TradePlan? Plan, PolicyOutcome Outcome)> CreateManualAsync(
        ulong userId, string rule, PlanParams p, DateTime nowUtc, CancellationToken ct = default)
    {
        var outcome = await EvaluateAsync(userId, rule, p, nowUtc, ignoreHours: true, ct);
        if (outcome.Plan is null) return (null, outcome);

        var plan = await SaveAsync(userId, outcome.Plan, nowUtc, forcePending: true);
        return (plan, outcome);
    }

    /// <summary>
    /// Сохраняет черновик правила: статус по лимитам (AutoApproved получает <c>decided_at</c>/<c>decided_via=auto</c>); <c>dry_run</c> — по воротам исполнения
    /// пользователя (dry-run, если хоть одно условие не выполнено или в плане есть строки не T-Invest).
    /// </summary>
    public async Task<TradePlan> SaveAsync(ulong userId, TradePlanDraft draft, DateTime nowUtc, bool forcePending)
    {
        var status = forcePending && draft.Status == PlanStatuses.AutoApproved ? PlanStatuses.PendingApproval : draft.Status;
        var auto = status == PlanStatuses.AutoApproved;
        var gate = await CombinedGateAsync(userId, draft.Lines);
        var dryRun = !gate.Live || draft.Lines.Count == 0 || (auto && draft.Lines.Any(l => IsBroker(l, Bonds.Core.Models.Brokers.Freedom)));
        var plan = new TradePlan
        {
            UserId = userId,
            Rule = draft.Rule,
            Trigger = draft.Trigger.Length > 255 ? draft.Trigger[..255] : draft.Trigger,
            Status = status,
            DryRun = dryRun,
            CreatedAt = nowUtc,
            ExpiresAt = draft.ExpiresAtUtc,
            DecidedAt = auto ? nowUtc : null,
            DecidedVia = auto ? DecidedVia.Auto : null,
            BotNonce = BotCallback.NewNonce(),
            SummaryJson = JsonSerializer.Serialize(dryRun ? draft.Summary : draft.Summary with { ExecutionNote = LiveNote }, JsonOptions),
            Lines = draft.Lines.ToList(),
        };
        plan.Id = await _plans.InsertAsync(plan);
        foreach (var line in plan.Lines) line.PlanId = plan.Id;
        _logger.LogInformation("Trade plan {PlanId} created: rule {Rule}, status {Status}, {Lines} line(s), dry-run {DryRun}", plan.Id, plan.Rule, plan.Status, plan.Lines.Count, plan.DryRun);
        return plan;
    }

    private static readonly string[] ExecutableBrokers = [Bonds.Core.Models.Brokers.TInvest, Bonds.Core.Models.Brokers.Freedom];

    private static bool IsBroker(TradePlanLine line, string broker) => string.Equals(line.Broker?.Trim(), broker, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Ворота плана = ворота КАЖДОГО его брокера (задача 66): живой — только если открыты все; причины — с префиксом брокера, когда брокеров несколько.
    /// Строка брокера без исполнителя — закрыто. Без строк — ворота T-Invest (для заметки).
    /// </summary>
    private async Task<ExecutionGateResult> CombinedGateAsync(ulong userId, IReadOnlyCollection<TradePlanLine> lines)
    {
        var brokers = lines.Select(l => (l.Broker ?? string.Empty).Trim().ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToList();
        if (brokers.Count == 0) return await _gate.ResolveAsync(userId);
        var results = new List<(string Broker, ExecutionGateResult Gate)>();
        foreach (var b in brokers)
        {
            var g = ExecutableBrokers.Contains(b, StringComparer.Ordinal)
                ? await _gate.ResolveForBrokerAsync(userId, b)
                : ExecutionGate.Evaluate(false, null, false, false, "Исполнение для брокера " + b + " не настроено");
            results.Add((b, g));
        }

        if (results.Count == 1) return results[0].Gate;
        var reasons = results.SelectMany(r => r.Gate.Reasons.Select(x => $"{r.Broker}: {x}")).ToList();
        var first = results[0].Gate;
        return new ExecutionGateResult(reasons.Count == 0, first.GlobalEnabled, first.Mode, results.All(r => r.Gate.TradeCredentialOk), first.Paused, reasons);
    }

    public async Task<PlanDecisionResult> ApproveAsync(ulong userId, ulong id, string via, DateTime nowUtc)
    {
        var plan = await _plans.GetAsync(userId, id);
        if (plan is null) return new PlanDecisionResult(PlanDecisionKind.NotFound, null);
        if (plan.Status is not (PlanStatuses.PendingApproval or PlanStatuses.Draft)) return new PlanDecisionResult(PlanDecisionKind.Conflict, plan);

        if (plan.ExpiresAt <= nowUtc)
        {
            await _plans.TransitionAsync(userId, id, [PlanStatuses.PendingApproval, PlanStatuses.Draft], PlanStatuses.Expired, null, nowUtc, null);
            return new PlanDecisionResult(PlanDecisionKind.Expired, await _plans.GetAsync(userId, id));
        }

        // План «только удержание» (задача 67, A2: вывод целиком из свободных денег): заявок нет — подтверждение = «выведу эти деньги», план сразу Done,
        // удержание (summary.cashHold) действует WithdrawHoldTtl рабочих дней или до /withdrawn («Деньги выведены»).
        if (plan.Lines.Count == 0 && WithdrawHold.AmountOf(plan) > 0m)
        {
            var held = await _plans.TransitionAsync(
                userId, id, [PlanStatuses.PendingApproval, PlanStatuses.Draft], PlanStatuses.Done, via, nowUtc, WithNote(plan.SummaryJson, HoldOnlyNote));
            return new PlanDecisionResult(held ? PlanDecisionKind.Ok : PlanDecisionKind.Conflict, await _plans.GetAsync(userId, id));
        }

        // Живой план (dry_run = 0): срок продлевается до конца БЛИЖАЙШЕЙ сессии, в которой ещё можно выставить заявки (подтверждение вечером — исполнение завтра
        // утром, а не «истёк до исполнения»); заметка — по ТЕКУЩИМ воротам (они могли закрыться после построения плана).
        DateTime? extendTo = null;
        string note;
        if (plan.DryRun)
        {
            note = DryRunNote;
        }
        else
        {
            var gate = await CombinedGateAsync(userId, plan.Lines);
            extendTo = ExecutionDeadline(plan, nowUtc);
            note = !gate.Live
                ? "ждёт: исполнение сейчас закрыто — " + string.Join("; ", gate.Reasons)
                : extendTo is { } until && !plan.Lines.All(l => TradingHours.IsOpen(MarketOf(l), nowUtc, _policy))
                    ? QueuedNote + string.Create(CultureInfo.InvariantCulture, $" — ждёт открытия торгов, срок до {TradingHours.ToMoscow(until):dd.MM HH:mm} МСК")
                    : QueuedNote;
        }

        var summaryJson = WithNote(plan.SummaryJson, note);
        var ok = await _plans.TransitionAsync(userId, id, [PlanStatuses.PendingApproval, PlanStatuses.Draft], PlanStatuses.Approved, via, nowUtc, summaryJson);
        if (ok && extendTo is { } deadline && deadline > plan.ExpiresAt) await _plans.ExtendExpiryAsync(userId, id, deadline);
        var current = await _plans.GetAsync(userId, id);
        return new PlanDecisionResult(ok ? PlanDecisionKind.Ok : PlanDecisionKind.Conflict, current);
    }

    /// <summary>Отклонить/отменить план. Допустимо из PendingApproval, Draft и AutoApproved (авто-план можно отменить до исполнения — в dry-run он не исполняется вовсе).</summary>
    public async Task<PlanDecisionResult> RejectAsync(ulong userId, ulong id, string via, DateTime nowUtc)
    {
        var plan = await _plans.GetAsync(userId, id);
        if (plan is null) return new PlanDecisionResult(PlanDecisionKind.NotFound, null);
        if (plan.Status is not (PlanStatuses.PendingApproval or PlanStatuses.Draft or PlanStatuses.AutoApproved)) return new PlanDecisionResult(PlanDecisionKind.Conflict, plan);

        var ok = await _plans.TransitionAsync(
            userId, id, [PlanStatuses.PendingApproval, PlanStatuses.Draft, PlanStatuses.AutoApproved], PlanStatuses.Rejected, via, nowUtc, null);
        return new PlanDecisionResult(ok ? PlanDecisionKind.Ok : PlanDecisionKind.Conflict, await _plans.GetAsync(userId, id));
    }

    /// <summary>Дописывает заметку режима в <c>summary_json</c> (при битом JSON возвращает исходную строку).</summary>
    private static PolicyMarket MarketOf(TradePlanLine line) => TradingHours.MarketOf(line.Broker, line.Ticker, null);

    /// <summary>
    /// Конец ближайшей сессии, в которой ещё можно выставить заявки по ВСЕМ строкам (раньше всех закрывающийся рынок; рынок строки — по брокеру и тикеру, класс
    /// бумаги здесь неизвестен → T-Invest считается Мосбиржей, консервативно). Запас — <c>Execution:CancelBeforeClose</c>. null — без ограничений часов.
    /// </summary>
    private DateTime? ExecutionDeadline(TradePlan plan, DateTime nowUtc)
    {
        DateTime? deadline = null;
        foreach (var line in plan.Lines)
        {
            var close = TradingHours.NextSessionCloseUtc(MarketOf(line), nowUtc, _policy, _execution.CurrentValue.CancelBeforeClose);
            if (close is { } c && (deadline is null || c < deadline)) deadline = c;
        }

        return deadline;
    }

    internal static string? WithNote(string? summaryJson, string note)
    {
        if (string.IsNullOrWhiteSpace(summaryJson)) return JsonSerializer.Serialize(new TradePlanSummary { ExecutionNote = note }, JsonOptions);
        try
        {
            var summary = JsonSerializer.Deserialize<TradePlanSummary>(summaryJson, JsonOptions);
            return summary is null ? summaryJson : JsonSerializer.Serialize(summary with { ExecutionNote = note }, JsonOptions);
        }
        catch (JsonException)
        {
            return summaryJson;
        }
    }

    // ─── Вычисление правил по запросу ──────────────────────────────────────────────────────

    private async Task<PolicyOutcome> EvaluateAsync(ulong userId, string rule, PlanParams p, DateTime nowUtc, bool ignoreHours, CancellationToken ct)
    {
        var inputs = await _builder.BuildAsync(userId, nowUtc, ignoreHours, ct);

        PolicyInput Require(string portfolio) =>
            inputs.TryGetValue(portfolio, out var input)
                ? input
                : throw new PolicyValidationException(portfolio == CapitalPortfolios.Usd ? "Нет счёта Freedom" : "Нет счёта T-Invest");

        switch (rule)
        {
            case PlanRules.P1Sweep:
                return SweepRule.Evaluate(Require(CapitalPortfolios.Rub));

            case PlanRules.P2Inflow:
            {
                var portfolio = NormalizePortfolio(p.Portfolio);
                var input = Require(portfolio);
                var amount = p.Amount ?? input.FreeCash;
                if (amount <= 0m) throw new PolicyValidationException("amount должен быть положительным");
                if (PolicyDataGate.Problem(input) is null && input.Baskets.Any(b => b.FillRule == BasketFillRules.Engine))
                    input = await _builder.WithBondDataAsync(userId, input, ct);
                return InflowRule.Evaluate(input, amount, $"Распределение суммы {amount:0.##} {input.Currency}");
            }

            case PlanRules.P3bLiqToBonds:
                return LiquidityToBondsRule.Evaluate(await _builder.WithBondDataAsync(userId, Require(CapitalPortfolios.Rub), ct));

            case PlanRules.P4Rebalance:
            {
                var input = Require(NormalizePortfolio(p.Portfolio));
                if (input.Baskets.Any(b => b.FillRule == BasketFillRules.Engine)) input = await _builder.WithBondDataAsync(userId, input, ct);
                return RebalanceRule.Evaluate(input);
            }

            case PlanRules.P5Withdraw:
            {
                if ((p.Amount is null) == (p.Fraction is null)) throw new PolicyValidationException("Укажите ровно одно из: amount, fraction");
                if (p.Amount is <= 0m) throw new PolicyValidationException("amount должен быть положительным");
                if (p.Fraction is not (> 0m and <= 1m) && p.Fraction is not null) throw new PolicyValidationException("fraction должен быть в диапазоне (0, 1]");
                return WithdrawRule.Evaluate(Require(NormalizePortfolio(p.Portfolio)), p.Amount, p.Fraction, p.IncludeBaskets);
            }

            case PlanRules.P7FreedomTopUp:
                return FreedomTopUpRule.Evaluate(Require(CapitalPortfolios.Usd));

            case PlanRules.Manual:
            {
                if (string.IsNullOrWhiteSpace(p.Code)) throw new PolicyValidationException("code корзины обязателен");
                var portfolio = inputs.Values.FirstOrDefault(i => i.BasketByCode(p.Code) is not null);
                if (portfolio is null) throw new PolicyValidationException($"Корзина «{p.Code}» не найдена");
                var basket = portfolio.BasketByCode(p.Code)!;
                if (basket.TargetWeight is not null)
                    throw new PolicyValidationException("Выйти можно только из корзины без цели (временной); для корзин с целью используйте вывод или ребаланс");
                return ExitBasketRule.Evaluate(portfolio, p.Code);
            }

            default:
                throw new PolicyValidationException($"Неизвестное правило «{rule}»");
        }
    }

    private static string NormalizePortfolio(string? portfolio)
    {
        if (string.IsNullOrWhiteSpace(portfolio)) return CapitalPortfolios.Rub;
        var normalized = portfolio.Trim().ToLowerInvariant();
        return CapitalPortfolios.All.Contains(normalized) ? normalized : throw new PolicyValidationException("portfolio должен быть 'rub' или 'usd'");
    }
}

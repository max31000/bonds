using System.Text.Json;
using Bonds.Core.Capital;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Policy;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using BrokerIds = Bonds.Core.Models.Brokers;

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
/// Построение, сохранение и решения по планам (задача 63). ПЛАНЫ — ТОЛЬКО ДАННЫЕ: <c>approve</c> переводит план в <c>Approved</c> и помечает «не исполнен: dry-run»;
/// никаких вызовов брокеров нет и быть не должно (исполнитель — фаза 3). <c>dry_run</c> всегда 1 (<see cref="ExecutionOptions.EffectiveDryRun"/>).
/// <para>
/// Ручные запросы (inflow/withdraw/exit-basket) и предпросмотр игнорируют часы торгов, но помечают цены «возможно устарели»; план, созданный вручную, всегда
/// <c>PendingApproval</c> (решение по нему принимает пользователь, а не лимиты авто-подтверждения).
/// </para>
/// </summary>
public sealed class PolicyService
{
    public const string DryRunNote = "не исполнен: dry-run";

    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly PolicyInputBuilder _builder;
    private readonly ITradePlanRepository _plans;
    private readonly ExecutionOptions _execution;
    private readonly ILogger<PolicyService> _logger;

    public PolicyService(PolicyInputBuilder builder, ITradePlanRepository plans, IOptions<ExecutionOptions> execution, ILogger<PolicyService> logger)
    {
        _builder = builder;
        _plans = plans;
        _execution = execution.Value;
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

    /// <summary>Сохраняет черновик правила: статус по лимитам (AutoApproved получает <c>decided_at</c>/<c>decided_via=auto</c>), <c>dry_run</c> = true всегда.</summary>
    public async Task<TradePlan> SaveAsync(ulong userId, TradePlanDraft draft, DateTime nowUtc, bool forcePending)
    {
        var status = forcePending && draft.Status == PlanStatuses.AutoApproved ? PlanStatuses.PendingApproval : draft.Status;
        var auto = status == PlanStatuses.AutoApproved;
        var plan = new TradePlan
        {
            UserId = userId,
            Rule = draft.Rule,
            Trigger = draft.Trigger.Length > 255 ? draft.Trigger[..255] : draft.Trigger,
            Status = status,
            DryRun = _execution.EffectiveDryRun,
            CreatedAt = nowUtc,
            ExpiresAt = draft.ExpiresAtUtc,
            DecidedAt = auto ? nowUtc : null,
            DecidedVia = auto ? DecidedVia.Auto : null,
            SummaryJson = JsonSerializer.Serialize(draft.Summary, JsonOptions),
            Lines = draft.Lines.ToList(),
        };
        plan.Id = await _plans.InsertAsync(plan);
        foreach (var line in plan.Lines) line.PlanId = plan.Id;
        _logger.LogInformation("Trade plan {PlanId} created: rule {Rule}, status {Status}, {Lines} line(s), dry-run", plan.Id, plan.Rule, plan.Status, plan.Lines.Count);
        return plan;
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

        var summaryJson = WithNote(plan.SummaryJson, DryRunNote);
        var ok = await _plans.TransitionAsync(userId, id, [PlanStatuses.PendingApproval, PlanStatuses.Draft], PlanStatuses.Approved, via, nowUtc, summaryJson);
        var current = await _plans.GetAsync(userId, id);
        return new PlanDecisionResult(ok ? PlanDecisionKind.Ok : PlanDecisionKind.Conflict, current);
    }

    public async Task<PlanDecisionResult> RejectAsync(ulong userId, ulong id, string via, DateTime nowUtc)
    {
        var plan = await _plans.GetAsync(userId, id);
        if (plan is null) return new PlanDecisionResult(PlanDecisionKind.NotFound, null);
        if (plan.Status is not (PlanStatuses.PendingApproval or PlanStatuses.Draft)) return new PlanDecisionResult(PlanDecisionKind.Conflict, plan);

        var ok = await _plans.TransitionAsync(userId, id, [PlanStatuses.PendingApproval, PlanStatuses.Draft], PlanStatuses.Rejected, via, nowUtc, null);
        return new PlanDecisionResult(ok ? PlanDecisionKind.Ok : PlanDecisionKind.Conflict, await _plans.GetAsync(userId, id));
    }

    /// <summary>Дописывает заметку режима в <c>summary_json</c> (при битом JSON возвращает исходную строку).</summary>
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
                if (input.Baskets.Any(b => b.FillRule == BasketFillRules.Engine)) input = await _builder.WithBondDataAsync(userId, input, ct);
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

    internal static string BrokerOfPortfolio(string portfolio) => portfolio == CapitalPortfolios.Usd ? BrokerIds.Freedom : BrokerIds.TInvest;
}

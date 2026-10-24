using System.Text.Json;
using Bonds.Api.Auth;
using Bonds.Api.Endpoints.Dtos;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Policy;
using Bonds.Infrastructure.Policy;
using Bonds.Core.Execution;
using Bonds.Infrastructure.Execution;

namespace Bonds.Api.Endpoints;

/// <summary>
/// Планы сделок (задача 63). Этот слой брокеров не вызывает: <c>approve</c> переводит план в <c>Approved</c>; dry-run план не исполняется, план с <c>dry_run = 0</c>
/// (живой режим пользователя на момент создания) исполняет фоновый исполнитель (задача 65). <c>cancel-execution</c> лишь ставит флаг снятия висящих заявок.
/// Данные только своего пользователя; суммы — в валюте портфеля плана, доли — ДОЛИ.
/// </summary>
public static class PlansEndpoints
{
    internal const string LiveDisclaimer =
        "Исполнение включено: подтверждённые планы отправляются брокеру лимитными заявками. План — оценка по стакану на момент построения (комиссия и налог приблизительны), " +
        "не инвестиционная рекомендация.";

    internal const string Disclaimer =
        "Режим наблюдения: сделки не отправляются. План — оценка по стакану на момент построения (комиссия и налог приблизительны: налог по средней цене входа, " +
        "плоские 13%, без сальдирования и ЛДВ), не инвестиционная рекомендация.";

    public static void MapPlansEndpoints(this WebApplication app)
    {
        app.MapGet("/api/plans", ListPlans);
        app.MapGet("/api/plans/{id:long}", GetPlan);
        app.MapPost("/api/plans/{id:long}/approve", Approve);
        app.MapPost("/api/plans/{id:long}/reject", Reject);
        app.MapPost("/api/plans/inflow", CreateInflow);
        app.MapPost("/api/plans/withdraw", CreateWithdraw);
        app.MapPost("/api/plans/exit-basket", CreateExitBasket);
        app.MapPost("/api/plans/preview", Preview);
        app.MapPost("/api/plans/{id:long}/cancel-execution", CancelExecution);
        app.MapPost("/api/plans/cash-hold/release", ReleaseCashHold);
    }

    private static async Task<IResult> ListPlans(
        string? status, int? limit, ICurrentUser currentUser, ITradePlanRepository repo, ITradeOrderRepository orders, IExecutionGateResolver gate)
    {
        if (status is not null && !PlanStatuses.All.Contains(status)) return AnalyticsSupport.ValidationError($"status: допустимо {string.Join(", ", PlanStatuses.All)}.");
        if (await currentUser.GetUserIdAsync() is not { } userId)
            return Results.Ok(new PlansResponseDto { Items = [], DryRun = true, Disclaimer = Disclaimer, Brokers = new PlansBrokerLiveDto { TInvest = false, Freedom = false } });

        var plans = await repo.ListAsync(userId, status, limit ?? 100);
        var byPlan = (await orders.ListByPlansAsync(plans.Select(p => p.Id).ToList())).ToLookup(o => o.PlanId);
        var tinvestLive = (await gate.ResolveAsync(userId)).Live;
        var freedomLive = (await gate.ResolveForBrokerAsync(userId, Bonds.Core.Models.Brokers.Freedom)).Live;
        var dryRun = !tinvestLive && !freedomLive;
        return Results.Ok(new PlansResponseDto
        {
            Items = plans.Select(p => ToDto(p, byPlan[p.Id].ToList())).ToList(),
            DryRun = dryRun,
            Brokers = new PlansBrokerLiveDto { TInvest = tinvestLive, Freedom = freedomLive },
            ActiveOrders = (await orders.ListActiveByUserAsync(userId)).Count,
            Disclaimer = dryRun ? Disclaimer : LiveDisclaimer,
        });
    }

    private static async Task<IResult> GetPlan(long id, ICurrentUser currentUser, ITradePlanRepository repo, ITradeOrderRepository orders)
    {
        if (await currentUser.GetUserIdAsync() is not { } userId) return Results.NotFound();
        var plan = await repo.GetAsync(userId, (ulong)id);
        return plan is null ? Results.NotFound() : Results.Ok(ToDto(plan, await orders.ListByPlanAsync(plan.Id)));
    }

    /// <summary>Снять висящие заявки плана (как кнопка «Отменить» в боте): флаг для исполнителя, сам эндпоинт брокера не вызывает. 409 — план не исполняется.</summary>
    private static async Task<IResult> CancelExecution(long id, ICurrentUser currentUser, ITradePlanRepository repo, ITradeOrderRepository orders)
    {
        if (await currentUser.GetUserIdAsync() is not { } userId) return Results.NotFound();
        var plan = await repo.GetAsync(userId, (ulong)id);
        if (plan is null) return Results.NotFound();
        if (!await repo.RequestCancelAsync(userId, plan.Id))
            return Results.Json(new { error = "План не исполняется — отменять нечего.", type = "PlanConflict" }, statusCode: StatusCodes.Status409Conflict);
        var current = await repo.GetAsync(userId, plan.Id);
        return Results.Ok(ToDto(current!, await orders.ListByPlanAsync(plan.Id)));
    }

    /// <summary>
    /// «Деньги выведены» (задача 67, #3; то же, что <c>/withdrawn</c> в боте): снимает удержание денег под вывод у всех планов вывода/выхода пользователя —
    /// P1 и остальные правила снова видят эти деньги свободными. Брокеров не вызывает. Ответ: <c>{ released: N }</c>.
    /// </summary>
    private static async Task<IResult> ReleaseCashHold(ICurrentUser currentUser, ITradePlanRepository repo)
    {
        if (await currentUser.GetUserIdAsync() is not { } userId) return Results.NotFound();
        return Results.Ok(new { released = await repo.ReleaseCashHoldsAsync(userId, DateTime.UtcNow) });
    }

    private static Task<IResult> Approve(long id, ICurrentUser currentUser, PolicyService service) =>
        DecideAsync(id, currentUser, (userId, planId) => service.ApproveAsync(userId, planId, DecidedVia.Web, DateTime.UtcNow));

    private static Task<IResult> Reject(long id, ICurrentUser currentUser, PolicyService service) =>
        DecideAsync(id, currentUser, (userId, planId) => service.RejectAsync(userId, planId, DecidedVia.Web, DateTime.UtcNow));

    private static async Task<IResult> DecideAsync(long id, ICurrentUser currentUser, Func<ulong, ulong, Task<PlanDecisionResult>> decide)
    {
        if (await currentUser.GetUserIdAsync() is not { } userId) return Results.NotFound();
        var result = await decide(userId, (ulong)id);
        return result.Kind switch
        {
            PlanDecisionKind.Ok => Results.Ok(ToDto(result.Plan!, [])),
            PlanDecisionKind.NotFound => Results.NotFound(),
            PlanDecisionKind.Expired => Results.Json(
                new { error = "План просрочен и больше не может быть подтверждён.", type = "PlanExpired", plan = result.Plan is null ? null : ToDto(result.Plan, []) },
                statusCode: StatusCodes.Status409Conflict),
            _ => Results.Json(
                new { error = "План уже решён или находится в статусе, из которого решение невозможно.", type = "PlanConflict", plan = result.Plan is null ? null : ToDto(result.Plan, []) },
                statusCode: StatusCodes.Status409Conflict),
        };
    }

    private static Task<IResult> CreateInflow(PlanInflowRequest? request, ICurrentUser currentUser, PolicyService service, IExecutionGateResolver execution) =>
        CreateAsync(currentUser, service, execution, PlanRules.P2Inflow, new PlanParams(Amount: request?.Amount, Portfolio: request?.Portfolio), requireAmount: true);

    private static Task<IResult> CreateWithdraw(PlanWithdrawRequest? request, ICurrentUser currentUser, PolicyService service, IExecutionGateResolver execution) =>
        CreateAsync(
            currentUser, service, execution, PlanRules.P5Withdraw,
            new PlanParams(Amount: request?.Amount, Fraction: request?.Fraction, Portfolio: request?.Portfolio, IncludeBaskets: request?.IncludeBaskets));

    private static Task<IResult> CreateExitBasket(PlanExitBasketRequest? request, ICurrentUser currentUser, PolicyService service, IExecutionGateResolver execution) =>
        CreateAsync(currentUser, service, execution, PlanRules.Manual, new PlanParams(Code: request?.Code));

    private static async Task<IResult> CreateAsync(
        ICurrentUser currentUser, PolicyService service, IExecutionGateResolver execution, string rule, PlanParams parameters, bool requireAmount = false)
    {
        if (requireAmount && parameters.Amount is not > 0m) return AnalyticsSupport.ValidationError("amount должен быть положительным.");
        if (await currentUser.GetUserIdAsync() is not { } userId) return Results.NotFound();

        try
        {
            var (plan, outcome) = await service.CreateManualAsync(userId, rule, parameters, DateTime.UtcNow);
            return Results.Ok(new PlanResultDto
            {
                Plan = plan is null ? null : ToDto(plan, []),
                Notes = outcome.Notes,
                DryRun = plan?.DryRun ?? (await execution.ResolveAsync(userId)).DryRun,
                Disclaimer = (plan?.DryRun ?? true) ? Disclaimer : LiveDisclaimer,
            });
        }
        catch (PolicyValidationException ex)
        {
            return AnalyticsSupport.ValidationError(ex.Message);
        }
    }

    private static async Task<IResult> Preview(PlanPreviewRequest? request, ICurrentUser currentUser, PolicyService service, IExecutionGateResolver execution)
    {
        if (request?.Rule is not { Length: > 0 } rule || !PlanRules.All.Contains(rule)) return AnalyticsSupport.ValidationError($"rule: допустимо {string.Join(", ", PlanRules.All)}.");
        if (await currentUser.GetUserIdAsync() is not { } userId) return Results.NotFound();

        var p = request.Params;
        try
        {
            var outcome = await service.PreviewAsync(
                userId, rule, new PlanParams(p?.Amount, p?.Fraction, p?.Portfolio, p?.IncludeBaskets, p?.Code), DateTime.UtcNow);
            return Results.Ok(new PlanResultDto
            {
                Plan = outcome.Plan is null ? null : ToDto(outcome.Plan),
                Notes = outcome.Notes,
                DryRun = (await execution.ResolveAsync(userId)).DryRun,
                Disclaimer = Disclaimer,
            });
        }
        catch (PolicyValidationException ex)
        {
            return AnalyticsSupport.ValidationError(ex.Message);
        }
    }

    // ─── Маппинг ───────────────────────────────────────────────────────────────────────────

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    internal static PlanDto ToDto(TradePlan plan, IReadOnlyCollection<TradeOrder> orders) => new()
    {
        Id = plan.Id,
        Rule = plan.Rule,
        Trigger = plan.Trigger,
        Status = plan.Status,
        DryRun = plan.DryRun,
        CreatedAt = Utc(plan.CreatedAt),
        ExpiresAt = Utc(plan.ExpiresAt),
        DecidedAt = plan.DecidedAt is { } d ? Utc(d) : null,
        DecidedVia = plan.DecidedVia,
        Summary = ParseSummary(plan.SummaryJson),
        Lines = plan.Lines.Select(l => ToDto(l, orders.Where(o => o.LineNo == l.LineNo).OrderBy(o => o.Attempt).ToList())).ToList(),
        NeedsAttention = orders.Any(NeedsAttention),
        CashHoldReleasedAt = plan.CashHoldReleasedAt is { } released ? DateTime.SpecifyKind(released, DateTimeKind.Utc) : null,
    };

    /// <summary>Заявка активна, а исполнитель уже сообщил, что снять её автоматически нельзя (<c>halt_alerted_at</c>).</summary>
    private static bool NeedsAttention(TradeOrder o) => o.HaltAlertedAt is not null && TradeOrderStatuses.IsActive(o.Status);

    /// <summary>Предпросмотр (черновик без id).</summary>
    internal static PlanDto ToDto(TradePlanDraft draft) => new()
    {
        Id = 0,
        Rule = draft.Rule,
        Trigger = draft.Trigger,
        Status = draft.Status,
        DryRun = true,
        CreatedAt = DateTime.UtcNow,
        ExpiresAt = Utc(draft.ExpiresAtUtc),
        Summary = draft.Summary,
        Lines = draft.Lines.Select(l => ToDto(l, [])).ToList(),
    };

    private static PlanLineDto ToDto(TradePlanLine l, IReadOnlyList<TradeOrder> orders)
    {
        var state = LineExecutionState.From(l, orders);
        return new PlanLineDto
        {
            LineNo = l.LineNo,
            Broker = l.Broker,
            AccountId = l.AccountId,
            InstrumentKey = l.InstrumentKey,
            Ticker = l.Ticker,
            Side = l.Side,
            QuantityLots = l.QuantityLots,
            LotSize = l.LotSize,
            QuantityPieces = l.QuantityPieces,
            LimitPrice = l.LimitPrice,
            LimitPricePoints = l.LimitPricePoints,
            EstAmount = l.EstAmount,
            EstFee = l.EstFee,
            EstTax = l.EstTax,
            Currency = l.Currency,
            Reason = l.Reason,
            ExecutionStatus = state.Status,
            FilledLots = state.FilledLots,
            Orders = orders.Select(o => new PlanOrderDto
            {
                Attempt = o.Attempt,
                Status = o.Status,
                LimitPrice = o.LimitPrice,
                PriceUnit = o.PriceUnit,
                LotsRequested = o.LotsRequested,
                LotsFilled = o.LotsFilled,
                AvgFillPrice = o.AvgFillPrice,
                ExecutedCommission = o.ExecutedCommission,
                CommissionCurrency = o.EffectiveCommissionCurrency,
                Currency = o.Currency,
                ErrorCode = o.ErrorCode,
                ErrorMessage = o.ErrorMessage,
                CreatedAt = Utc(o.CreatedAt),
                UpdatedAt = Utc(o.UpdatedAt),
                NeedsAttention = NeedsAttention(o),
            }).ToList(),
        };
    }

    private static TradePlanSummary? ParseSummary(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<TradePlanSummary>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

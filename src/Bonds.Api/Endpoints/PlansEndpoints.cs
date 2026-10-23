using System.Text.Json;
using Bonds.Api.Auth;
using Bonds.Api.Endpoints.Dtos;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Policy;
using Bonds.Infrastructure.Policy;
using Microsoft.Extensions.Options;

namespace Bonds.Api.Endpoints;

/// <summary>
/// Планы сделок (задача 63). <b>Режим наблюдения: заявки брокерам НЕ отправляются</b> — планы только данные (<c>dry_run</c> всегда true), <c>approve</c> лишь
/// переводит план в <c>Approved</c> с пометкой «не исполнен: dry-run». Никаких вызовов размещения заявок в этом слое нет и быть не должно (исполнитель — фаза 3).
/// Данные только своего пользователя; суммы — в валюте портфеля плана, доли — ДОЛИ.
/// </summary>
public static class PlansEndpoints
{
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
    }

    private static async Task<IResult> ListPlans(
        string? status, int? limit, ICurrentUser currentUser, ITradePlanRepository repo, IOptions<ExecutionOptions> execution)
    {
        if (status is not null && !PlanStatuses.All.Contains(status)) return AnalyticsSupport.ValidationError($"status: допустимо {string.Join(", ", PlanStatuses.All)}.");
        if (await currentUser.GetUserIdAsync() is not { } userId) return Results.Ok(new PlansResponseDto { Items = [], DryRun = true, Disclaimer = Disclaimer });

        var plans = await repo.ListAsync(userId, status, limit ?? 100);
        return Results.Ok(new PlansResponseDto { Items = plans.Select(ToDto).ToList(), DryRun = execution.Value.EffectiveDryRun, Disclaimer = Disclaimer });
    }

    private static async Task<IResult> GetPlan(long id, ICurrentUser currentUser, ITradePlanRepository repo)
    {
        if (await currentUser.GetUserIdAsync() is not { } userId) return Results.NotFound();
        var plan = await repo.GetAsync(userId, (ulong)id);
        return plan is null ? Results.NotFound() : Results.Ok(ToDto(plan));
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
            PlanDecisionKind.Ok => Results.Ok(ToDto(result.Plan!)),
            PlanDecisionKind.NotFound => Results.NotFound(),
            PlanDecisionKind.Expired => Results.Json(
                new { error = "План просрочен и больше не может быть подтверждён.", type = "PlanExpired", plan = result.Plan is null ? null : ToDto(result.Plan) },
                statusCode: StatusCodes.Status409Conflict),
            _ => Results.Json(
                new { error = "План уже решён или находится в статусе, из которого решение невозможно.", type = "PlanConflict", plan = result.Plan is null ? null : ToDto(result.Plan) },
                statusCode: StatusCodes.Status409Conflict),
        };
    }

    private static Task<IResult> CreateInflow(PlanInflowRequest? request, ICurrentUser currentUser, PolicyService service, IOptions<ExecutionOptions> execution) =>
        CreateAsync(currentUser, service, execution, PlanRules.P2Inflow, new PlanParams(Amount: request?.Amount, Portfolio: request?.Portfolio), requireAmount: true);

    private static Task<IResult> CreateWithdraw(PlanWithdrawRequest? request, ICurrentUser currentUser, PolicyService service, IOptions<ExecutionOptions> execution) =>
        CreateAsync(
            currentUser, service, execution, PlanRules.P5Withdraw,
            new PlanParams(Amount: request?.Amount, Fraction: request?.Fraction, Portfolio: request?.Portfolio, IncludeBaskets: request?.IncludeBaskets));

    private static Task<IResult> CreateExitBasket(PlanExitBasketRequest? request, ICurrentUser currentUser, PolicyService service, IOptions<ExecutionOptions> execution) =>
        CreateAsync(currentUser, service, execution, PlanRules.Manual, new PlanParams(Code: request?.Code));

    private static async Task<IResult> CreateAsync(
        ICurrentUser currentUser, PolicyService service, IOptions<ExecutionOptions> execution, string rule, PlanParams parameters, bool requireAmount = false)
    {
        if (requireAmount && parameters.Amount is not > 0m) return AnalyticsSupport.ValidationError("amount должен быть положительным.");
        if (await currentUser.GetUserIdAsync() is not { } userId) return Results.NotFound();

        try
        {
            var (plan, outcome) = await service.CreateManualAsync(userId, rule, parameters, DateTime.UtcNow);
            return Results.Ok(new PlanResultDto
            {
                Plan = plan is null ? null : ToDto(plan),
                Notes = outcome.Notes,
                DryRun = execution.Value.EffectiveDryRun,
                Disclaimer = Disclaimer,
            });
        }
        catch (PolicyValidationException ex)
        {
            return AnalyticsSupport.ValidationError(ex.Message);
        }
    }

    private static async Task<IResult> Preview(PlanPreviewRequest? request, ICurrentUser currentUser, PolicyService service, IOptions<ExecutionOptions> execution)
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
                DryRun = execution.Value.EffectiveDryRun,
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

    internal static PlanDto ToDto(TradePlan plan) => new()
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
        Lines = plan.Lines.Select(ToDto).ToList(),
    };

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
        Lines = draft.Lines.Select(ToDto).ToList(),
    };

    private static PlanLineDto ToDto(TradePlanLine l) => new()
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
    };

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

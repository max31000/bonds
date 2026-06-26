using System.Security.Claims;
using Bonds.Core.CashFlow;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;

namespace Bonds.Api.Endpoints;

/// <summary>
/// GET /api/cashflow — календарь поступлений: по месяцам и позициям, брутто/налог/нетто,
/// даты освобождения тела (plan/08, spec §7.4/§9). Данные читаются из уже спроецированного
/// <c>IProjectedCashFlowRepository</c> (этап 06 пишет их при каждом цикле синка, этап 07) —
/// этот эндпоинт не пересчитывает проекцию заново, только агрегирует уже сохранённые потоки
/// через чистый <see cref="CashFlowAggregator"/> (самостоятельное решение: пересчёт on-demand
/// был бы дороже и дублировал бы CashFlowProjectionOrchestrator без необходимости — свежесть
/// данных гарантирует ежедневный цикл синка, этап 07).
/// </summary>
public static class CashFlowEndpoints
{
    public static void MapCashFlowEndpoints(this WebApplication app)
    {
        app.MapGet("/api/cashflow", GetCashFlow);
    }

    private static async Task<IResult> GetCashFlow(
        ClaimsPrincipal principal,
        IAccountRepository accountRepo,
        IProjectedCashFlowRepository projectedCashFlows,
        IInstrumentRepository instrumentRepo,
        DateOnly? from,
        DateOnly? to)
    {
        var accountId = await PositionsEndpoints.ResolveAccountIdAsync(principal, accountRepo);
        if (accountId is null)
        {
            return Results.Ok(new CashFlowResponseDto
            {
                ByMonth = [],
                ByPosition = [],
                PrincipalReleases = [],
                Disclaimer = Disclaimers.Metrics,
            });
        }

        var flows = (await projectedCashFlows.GetByAccountIdAsync(accountId.Value, from, to)).ToList();

        var byPositionAgg = CashFlowAggregator.ByPosition(flows).ToList();
        var instrumentIds = byPositionAgg.Select(p => p.InstrumentId).Distinct().ToList();
        var instruments = new Dictionary<ulong, Instrument>();
        foreach (var instrumentId in instrumentIds)
        {
            var instrument = await instrumentRepo.GetByIdAsync(instrumentId);
            if (instrument is not null) instruments[instrumentId] = instrument;
        }

        var dto = new CashFlowResponseDto
        {
            ByMonth = CashFlowAggregator.ByMonth(flows).Select(m => new MonthlyCashFlowDto
            {
                Month = m.Month,
                GrossRub = m.GrossRub,
                TaxRub = m.TaxRub,
                NetRub = m.NetRub,
                CouponGrossRub = m.CouponGrossRub,
                PrincipalGrossRub = m.PrincipalGrossRub,
                HasEstimatedFlows = m.HasEstimatedFlows,
            }).ToList(),
            ByPosition = byPositionAgg.Select(p =>
            {
                instruments.TryGetValue(p.InstrumentId, out var instr);
                return new PositionCashFlowDto
                {
                    PositionId = p.PositionId,
                    InstrumentId = p.InstrumentId,
                    Name = instr?.Name,
                    Issuer = instr?.Issuer,
                    GrossRub = p.GrossRub,
                    TaxRub = p.TaxRub,
                    NetRub = p.NetRub,
                    HasEstimatedFlows = p.HasEstimatedFlows,
                };
            }).ToList(),
            PrincipalReleases = CashFlowAggregator.PrincipalReleases(flows).Select(r => new PrincipalReleaseDto
            {
                Date = r.Date,
                PositionId = r.PositionId,
                InstrumentId = r.InstrumentId,
                FlowType = r.FlowType.ToString(),
                AmountRub = r.AmountRub,
                IsEstimated = r.IsEstimated,
            }).ToList(),
            Disclaimer = Disclaimers.Metrics,
        };

        return Results.Ok(dto);
    }
}

public sealed record CashFlowResponseDto
{
    public required IReadOnlyList<MonthlyCashFlowDto> ByMonth { get; init; }
    public required IReadOnlyList<PositionCashFlowDto> ByPosition { get; init; }
    public required IReadOnlyList<PrincipalReleaseDto> PrincipalReleases { get; init; }
    public required string Disclaimer { get; init; }
}

public sealed record MonthlyCashFlowDto
{
    public required DateOnly Month { get; init; }
    public required decimal GrossRub { get; init; }
    public required decimal TaxRub { get; init; }
    public required decimal NetRub { get; init; }
    public required decimal CouponGrossRub { get; init; }
    public required decimal PrincipalGrossRub { get; init; }
    public required bool HasEstimatedFlows { get; init; }
}

public sealed record PositionCashFlowDto
{
    public required ulong PositionId { get; init; }
    public required ulong InstrumentId { get; init; }
    public string? Name { get; init; }
    public string? Issuer { get; init; }
    public required decimal GrossRub { get; init; }
    public required decimal TaxRub { get; init; }
    public required decimal NetRub { get; init; }
    public required bool HasEstimatedFlows { get; init; }
}

public sealed record PrincipalReleaseDto
{
    public required DateOnly Date { get; init; }
    public required ulong PositionId { get; init; }
    public required ulong InstrumentId { get; init; }
    public required string FlowType { get; init; }
    public required decimal AmountRub { get; init; }
    public required bool IsEstimated { get; init; }
}

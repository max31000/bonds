using Bonds.Api.Auth;
using Bonds.Api.Endpoints.Dtos;
using Bonds.Api.Middleware;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Bonds.Core.Time;
using Bonds.Infrastructure.Analytics;

namespace Bonds.Api.Endpoints;

/// <summary>
/// Счёт Freedom (задача 60): <c>GET /api/brokers/freedom/holdings</c> и <c>/operations</c>. Данные — из отдельного домена <c>broker_*</c>
/// (в валюте строки), рубли считаются на чтении (<see cref="BrokerHoldingsBuilder"/>). Счёт резолвится по пользователю запроса
/// (изоляция пользователей); нет счёта Freedom — 404.
/// </summary>
public static class BrokerHoldingsEndpoints
{
    internal const string BrokerDisclaimer =
        "Оценка по данным брокера и курсам ЦБ РФ/MOEX; P&L считается по средней цене входа и не равен налоговой базе. " +
        "Не инвестиционная рекомендация.";

    public static void MapBrokerHoldingsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/brokers/freedom/holdings", GetHoldings);
        app.MapGet("/api/brokers/freedom/operations", GetOperations);
    }

    private static async Task<IResult> GetHoldings(
        ICurrentUser currentUser,
        IAccountRepository accountRepo,
        BrokerHoldingsBuilder builder,
        CancellationToken ct)
    {
        var account = await PositionsEndpoints.ResolveBrokerAccountAsync(currentUser, accountRepo, Brokers.Freedom)
                      ?? throw new NotFoundException("Счёт Freedom не подключён");

        var asOf = BusinessClock.MoscowToday();
        var holdings = await builder.BuildAsync(account.Id, asOf, ct);
        var fees = await builder.GetFeesPaidUsdAsync(account.Id, asOf, ct);

        return Results.Ok(new BrokerHoldingsResponseDto
        {
            Positions = holdings.Positions
                .OrderByDescending(p => p.MarketValueRub ?? 0m)
                .Select(p => new BrokerPositionDto
                {
                    Ticker = p.Position.Ticker,
                    Name = p.Position.Name,
                    AssetClass = p.Position.AssetClass,
                    Currency = p.Position.Currency,
                    Quantity = p.Position.Quantity,
                    AvgPrice = p.Position.AvgPrice,
                    LastPrice = p.Position.LastPrice,
                    MarketValue = p.MarketValue,
                    UnrealizedPnl = p.UnrealizedPnl,
                    UnrealizedPnlFraction = p.UnrealizedPnlFraction,
                    MarketValueRub = p.MarketValueRub,
                }).ToList(),
            Cash = holdings.Cash
                .Select(c => new BrokerCashDto { Currency = c.Cash.Currency, Amount = c.Cash.Amount, AmountRub = c.AmountRub })
                .ToList(),
            TotalValueRub = holdings.TotalValueRub,
            TotalValueUsd = holdings.TotalValueUsd,
            SyncedAt = holdings.SyncedAtUtc,
            DataIncomplete = holdings.DataIncomplete,
            Fees = new BrokerFeesDto { FeesPaidUsd = fees.FeesPaidUsd, DataIncomplete = fees.DataIncomplete },
            Disclaimer = BrokerDisclaimer,
        });
    }

    private static async Task<IResult> GetOperations(
        DateOnly? from,
        DateOnly? to,
        ICurrentUser currentUser,
        IAccountRepository accountRepo,
        IBrokerOperationRepository operations)
    {
        if (from is { } f && to is { } t && f > t)
            return AnalyticsSupport.ValidationError("from не может быть позже to.");

        var account = await PositionsEndpoints.ResolveBrokerAccountAsync(currentUser, accountRepo, Brokers.Freedom)
                      ?? throw new NotFoundException("Счёт Freedom не подключён");

        var rows = await operations.GetByAccountAsync(account.Id, from, to);
        return Results.Ok(new BrokerOperationsResponseDto
        {
            Operations = rows.Select(o => new BrokerOperationDto
            {
                ExternalId = o.ExternalId,
                Date = o.Date,
                Type = o.Type,
                Ticker = o.Ticker,
                Quantity = o.Quantity,
                Price = o.Price,
                Amount = o.Amount,
                Currency = o.Currency,
                RawType = o.RawType,
                Comment = o.Comment,
            }).ToList(),
        });
    }
}

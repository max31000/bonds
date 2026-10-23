using Bonds.Api.Auth;
using Bonds.Api.Endpoints.Dtos;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Bonds.Core.Time;
using Bonds.Infrastructure.Analytics;

namespace Bonds.Api.Endpoints;

/// <summary>
/// Деньги и торговые метаданные (задача 61, только чтение). <c>GET /api/cash</c> — свободные деньги по брокерам и валютам (рубли без валюты —
/// <c>cashRubOnly</c>) и последние пополнения/выводы ТОЛЬКО счетов пользователя запроса. <c>GET /api/trading-meta?broker&amp;keys</c> — свойства бумаг
/// (лот, шаг цены, флаги торговли): общие для пользователей, личных данных не содержат.
/// </summary>
public static class CashEndpoints
{
    internal const string CashDisclaimer =
        "Остатки и движения денег — по данным брокеров на момент последней синхронизации; валюта пересчитана по курсам bonds. " +
        "Не инвестиционная рекомендация.";

    /// <summary>Не больше стольких ключей в одном запросе <c>keys</c>.</summary>
    internal const int MaxKeys = 200;

    private static readonly HashSet<string> KnownBrokers = new(StringComparer.OrdinalIgnoreCase) { Brokers.TInvest, Brokers.Freedom };

    public static void MapCashEndpoints(this WebApplication app)
    {
        app.MapGet("/api/cash", GetCash);
        app.MapGet("/api/trading-meta", GetTradingMeta);
    }

    private static async Task<IResult> GetCash(ICurrentUser currentUser, CashOverviewBuilder builder, CancellationToken ct)
    {
        if (await currentUser.GetUserIdAsync() is not { } userId)
            return Results.Ok(new CashOverviewDto { Brokers = [], Disclaimer = CashDisclaimer });

        var overview = await builder.BuildAsync(userId, BusinessClock.MoscowToday(), ct);
        return Results.Ok(new CashOverviewDto
        {
            Brokers = overview.Select(b => new BrokerCashSummaryDto
            {
                Broker = b.Broker,
                Balances = b.Balances.Select(x => new CashBalanceDto { Currency = x.Currency, Amount = x.Amount, AmountRub = x.AmountRub }).ToList(),
                CashRubOnly = b.CashRubOnly,
                TotalRub = b.TotalRub,
                SyncedAt = b.SyncedAtUtc,
                DataIncomplete = b.DataIncomplete,
                RecentMovements = b.RecentMovements
                    .Select(m => new CashMovementDto { Date = m.Date, Type = m.Type, Amount = m.Amount, Currency = m.Currency, AmountRub = m.AmountRub })
                    .ToList(),
            }).ToList(),
            Disclaimer = CashDisclaimer,
        });
    }

    private static async Task<IResult> GetTradingMeta(string? broker, string? keys, IInstrumentTradingMetaRepository repository)
    {
        if (string.IsNullOrWhiteSpace(broker) || !KnownBrokers.Contains(broker.Trim()))
            return AnalyticsSupport.ValidationError("broker обязателен: tinvest или freedom.");

        var normalizedBroker = broker.Trim().ToLowerInvariant();
        IReadOnlyList<InstrumentTradingMeta> rows;
        if (string.IsNullOrWhiteSpace(keys))
        {
            rows = await repository.GetByBrokerAsync(normalizedBroker);
        }
        else
        {
            var list = keys.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (list.Count > MaxKeys) return AnalyticsSupport.ValidationError($"keys: не больше {MaxKeys} значений.");
            rows = await repository.GetByKeysAsync(normalizedBroker, list);
        }

        return Results.Ok(new TradingMetaResponseDto
        {
            Items = rows.Select(m => new TradingMetaDto
            {
                Broker = m.Broker, InstrumentKey = m.InstrumentKey, Ticker = m.Ticker, Uid = m.Uid, Figi = m.Figi, ClassCode = m.ClassCode,
                Lot = m.Lot, MinPriceIncrement = m.MinPriceIncrement, MinPriceIncrementPoints = m.MinPriceIncrementPoints, Currency = m.Currency,
                ApiTradeAvailable = m.ApiTradeAvailable, BuyAvailable = m.BuyAvailable, SellAvailable = m.SellAvailable,
                PaysDistributions = m.PaysDistributions, ItsTwin = m.ItsTwin, UpdatedAt = DateTime.SpecifyKind(m.UpdatedAt, DateTimeKind.Utc),
            }).ToList(),
        });
    }
}

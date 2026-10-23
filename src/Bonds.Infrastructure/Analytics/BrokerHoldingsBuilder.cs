using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Bonds.Infrastructure.Sync;

namespace Bonds.Infrastructure.Analytics;

/// <summary>Позиция брокера с оценкой. Деньги <see cref="Position"/>/<see cref="UnrealizedPnl"/> — в валюте позиции; <see cref="MarketValueRub"/> — РУБЛИ.</summary>
/// <param name="UnrealizedPnl">Стоимость − средняя цена × количество, в ВАЛЮТЕ позиции.</param>
/// <param name="UnrealizedPnlFraction">P&amp;L к стоимости входа, ДОЛЯ (0.05 = +5%); null — цены входа нет.</param>
/// <param name="MarketValueRub">Стоимость в рублях по курсу bonds; null — курса нет (позиция не вносит вклад в итог, <c>DataIncomplete</c>).</param>
public sealed record BrokerPositionValued(
    BrokerPosition Position,
    decimal MarketValue,
    decimal UnrealizedPnl,
    decimal? UnrealizedPnlFraction,
    decimal? MarketValueRub);

/// <summary>Остаток денег; <see cref="AmountRub"/> — РУБЛИ (null — курса нет).</summary>
public sealed record BrokerCashValued(BrokerCash Cash, decimal? AmountRub);

/// <summary>Оценка счёта брокера. <see cref="TotalValueRub"/> — Σ позиций и денег с известным курсом (при <see cref="DataIncomplete"/> занижен).</summary>
public sealed record BrokerHoldings(
    IReadOnlyList<BrokerPositionValued> Positions,
    IReadOnlyList<BrokerCashValued> Cash,
    decimal TotalValueRub,
    decimal? TotalValueUsd,
    DateTime? SyncedAtUtc,
    bool DataIncomplete);

/// <summary>Σ комиссий брокера в USD. <see cref="FeesPaidUsd"/> — ПОЛОЖИТЕЛЬНОЕ число = сколько уплачено (минус Σ amount операций Fee).</summary>
public sealed record BrokerFees(decimal FeesPaidUsd, bool DataIncomplete);

/// <summary>
/// Задача 60: оценка брокерских (не T-Invest) счетов в рублях и долларах. <b>Единицы:</b> в БД всё в валюте строки; пересчёт в рубли —
/// ТОЛЬКО здесь, на чтении, через <see cref="ICurrencyRateLookup"/> (рубли за 1 единицу валюты; поле <c>currval</c> брокера не используется).
/// RUB = 1. Нет курса — стоимость null и <c>DataIncomplete</c>, а не нуль и не «последнее известное».
/// </summary>
public sealed class BrokerHoldingsBuilder
{
    private const string Rub = "RUB";
    private const string Usd = "USD";

    private readonly IBrokerPositionRepository _positions;
    private readonly IBrokerCashRepository _cash;
    private readonly IBrokerOperationRepository _operations;
    private readonly IAccountRepository _accounts;
    private readonly ICurrencyRateLookup _rates;

    public BrokerHoldingsBuilder(
        IBrokerPositionRepository positions,
        IBrokerCashRepository cash,
        IBrokerOperationRepository operations,
        IAccountRepository accounts,
        ICurrencyRateLookup rates)
    {
        _positions = positions;
        _cash = cash;
        _operations = operations;
        _accounts = accounts;
        _rates = rates;
    }

    public async Task<BrokerHoldings> BuildAsync(ulong accountId, DateOnly asOf, CancellationToken ct = default)
    {
        var positions = await _positions.GetByAccountAsync(accountId);
        var cash = await _cash.GetByAccountAsync(accountId);

        var incomplete = false;
        var valuedPositions = new List<BrokerPositionValued>();
        foreach (var p in positions)
        {
            // Стоимость по оценке брокера; нет её (0), а цена есть — цена × количество.
            var marketValue = p.MarketValue != 0m ? p.MarketValue : p.LastPrice * p.Quantity;
            var cost = p.AvgPrice * p.Quantity;
            var pnl = marketValue - cost;
            var rate = await RateAsync(p.Currency, asOf, ct);
            if (rate is null) incomplete = true;

            valuedPositions.Add(new BrokerPositionValued(
                p,
                marketValue,
                pnl,
                cost > 0m ? pnl / cost : null,
                rate is { } r ? marketValue * r : null));
        }

        var valuedCash = new List<BrokerCashValued>();
        foreach (var c in cash)
        {
            var rate = await RateAsync(c.Currency, asOf, ct);
            if (rate is null) incomplete = true;
            valuedCash.Add(new BrokerCashValued(c, rate is { } r ? c.Amount * r : null));
        }

        var totalRub = valuedPositions.Sum(p => p.MarketValueRub ?? 0m) + valuedCash.Sum(c => c.AmountRub ?? 0m);
        var usdRate = await RateAsync(Usd, asOf, ct);
        var syncedAt = positions.Select(p => (DateTime?)p.SyncedAt).Concat(cash.Select(c => (DateTime?)c.SyncedAt)).Max();

        return new BrokerHoldings(
            valuedPositions,
            valuedCash,
            totalRub,
            usdRate is > 0m ? totalRub / usdRate : null,
            syncedAt is { } s ? DateTime.SpecifyKind(s, DateTimeKind.Utc) : null,
            incomplete);
    }

    /// <summary>Стоимость всех не-T-Invest счетов пользователя, РУБЛИ, для сводки портфеля. 0 при их отсутствии.</summary>
    public async Task<(decimal ValueRub, bool DataIncomplete)> GetOtherBrokersValueAsync(ulong userId, DateOnly asOf, CancellationToken ct = default)
    {
        var accounts = (await _accounts.GetByUserIdAsync(userId)).Where(a => a.Broker != Brokers.TInvest).ToList();
        var total = 0m;
        var incomplete = false;
        foreach (var account in accounts)
        {
            var holdings = await BuildAsync(account.Id, asOf, ct);
            total += holdings.TotalValueRub;
            incomplete |= holdings.DataIncomplete;
        }

        return (total, incomplete);
    }

    /// <summary>
    /// Σ комиссий (операции Fee) за всё время в USD. Не-USD комиссия переводится в USD через рубли по курсу дня операции
    /// (<c>amount × RUB/ед. ÷ RUB/USD</c>); нет курса на дату — берётся курс на <paramref name="asOf"/>; нет и его — операция пропускается,
    /// <c>DataIncomplete</c>.
    /// </summary>
    public async Task<BrokerFees> GetFeesPaidUsdAsync(ulong accountId, DateOnly asOf, CancellationToken ct = default)
    {
        var fees = await _operations.GetByTypeAsync(accountId, BrokerOperationType.Fee);
        var sumUsd = 0m;
        var incomplete = false;
        foreach (var fee in fees)
        {
            var currency = fee.Currency.ToUpperInvariant();
            if (currency == Usd)
            {
                sumUsd += fee.Amount;
                continue;
            }

            var date = DateOnly.FromDateTime(fee.Date);
            var unit = await RateAsync(currency, date, ct) ?? await RateAsync(currency, asOf, ct);
            var usd = await RateAsync(Usd, date, ct) ?? await RateAsync(Usd, asOf, ct);
            if (unit is null || usd is not > 0m)
            {
                incomplete = true;
                continue;
            }

            sumUsd += fee.Amount * unit.Value / usd.Value;
        }

        return new BrokerFees(-sumUsd, incomplete);
    }

    private async Task<decimal?> RateAsync(string currency, DateOnly date, CancellationToken ct) =>
        string.Equals(currency, Rub, StringComparison.OrdinalIgnoreCase)
            ? 1m
            : await _rates.GetRubPerUnitAsync(currency, date, ct);
}

using Bonds.Core.Brokers;
using Bonds.Core.Currencies;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Bonds.Infrastructure.Sync;

namespace Bonds.Infrastructure.Analytics;

/// <summary>Остаток денег в одной валюте. <see cref="Amount"/> — в СВОЕЙ валюте, <see cref="AmountRub"/> — РУБЛИ (null — курса нет).</summary>
public sealed record CashBalance(string Currency, decimal Amount, decimal? AmountRub);

/// <summary>Пополнение/вывод. <see cref="Amount"/> — в своей валюте СО ЗНАКОМ брокера, <see cref="AmountRub"/> — РУБЛИ (null — курса на дату нет).</summary>
public sealed record CashMovementView(DateTime Date, CashMovementType Type, decimal Amount, string Currency, decimal? AmountRub);

/// <summary>
/// Деньги одного брокерского счёта. <see cref="CashRubOnly"/> — свободные рубли БЕЗ валюты (для T-Invest: <c>max(0, cashRub − валюта в рублях)</c>,
/// для Freedom — остаток в RUB); null — рублёвого остатка нет/баланс не синкался. <see cref="TotalRub"/> — все деньги в рублях (для T-Invest —
/// брокерский <c>TotalAmountCurrencies</c>, уже включающий валюту; для Freedom — Σ остатков с известным курсом), null — баланса нет.
/// </summary>
public sealed record BrokerCashOverview(
    string Broker,
    ulong AccountId,
    IReadOnlyList<CashBalance> Balances,
    decimal? CashRubOnly,
    decimal? TotalRub,
    DateTime? SyncedAtUtc,
    bool DataIncomplete,
    IReadOnlyList<CashMovementView> RecentMovements);

/// <summary>
/// Задача 61: «Деньги на счетах» пользователя по брокерам — для <c>GET /api/cash</c>. <b>Единицы:</b> суммы валют — в своей валюте, рубли — отдельным
/// полем. T-Invest: рубли без валюты по правилу <see cref="CashBreakdown.CashRubOnly"/> (брокерский <c>cash_rub</c> уже включает валюту — её
/// нельзя прибавлять повторно, только выделять); валюта — позиции валютного домена (ЕДИНИЦЫ валюты × единая точка курса
/// <see cref="IncomeLedgerBuilder.ResolveCurrencyPriceAsync"/>). Freedom: остатки <c>broker_cash</c> × курс <see cref="ICurrencyRateLookup"/>.
/// Данные только своего пользователя: счета берутся по <c>userId</c>.
/// </summary>
public sealed class CashOverviewBuilder
{
    /// <summary>Сколько последних пополнений и выводов отдаём на счёт.</summary>
    public const int RecentMovementsLimit = 10;

    private readonly IAccountRepository _accounts;
    private readonly IAccountCashMovementRepository _movements;
    private readonly ICurrencyPositionRepository _currencyPositions;
    private readonly ICurrencyRepository _currencies;
    private readonly IncomeLedgerBuilder _income;
    private readonly IBrokerCashRepository _brokerCash;
    private readonly IBrokerOperationRepository _brokerOperations;
    private readonly ICurrencyRateLookup _rates;

    public CashOverviewBuilder(
        IAccountRepository accounts,
        IAccountCashMovementRepository movements,
        ICurrencyPositionRepository currencyPositions,
        ICurrencyRepository currencies,
        IncomeLedgerBuilder income,
        IBrokerCashRepository brokerCash,
        IBrokerOperationRepository brokerOperations,
        ICurrencyRateLookup rates)
    {
        _accounts = accounts;
        _movements = movements;
        _currencyPositions = currencyPositions;
        _currencies = currencies;
        _income = income;
        _brokerCash = brokerCash;
        _brokerOperations = brokerOperations;
        _rates = rates;
    }

    /// <summary>Брокеры пользователя: T-Invest (основной счёт), затем Freedom — только те, чей счёт есть.</summary>
    public async Task<IReadOnlyList<BrokerCashOverview>> BuildAsync(ulong userId, DateOnly asOf, CancellationToken ct = default)
    {
        var accounts = (await _accounts.GetByUserIdAsync(userId)).OrderBy(a => a.Id).ToList();
        var result = new List<BrokerCashOverview>();

        var tinvest = accounts.FirstOrDefault(a => a.Broker == Brokers.TInvest);
        if (tinvest is not null) result.Add(await BuildTInvestAsync(tinvest, ct));

        var freedom = accounts.FirstOrDefault(a => a.Broker == Brokers.Freedom);
        if (freedom is not null) result.Add(await BuildFreedomAsync(freedom, asOf, ct));

        return result;
    }

    private async Task<BrokerCashOverview> BuildTInvestAsync(Account account, CancellationToken ct)
    {
        var incomplete = false;
        var balances = new List<CashBalance>();
        var currencyValueRub = 0m;
        foreach (var position in (await _currencyPositions.GetByAccountAsync(account.Id)).Where(p => p.Quantity > 0m))
        {
            ct.ThrowIfCancellationRequested();
            var currency = await _currencies.GetByIdAsync(position.CurrencyId);
            if (currency is null) continue;

            var price = await _income.ResolveCurrencyPriceAsync(position, position.CurrencyId, ct);
            decimal? rub = price is { } p ? p * position.Quantity : null;
            if (rub is null) incomplete = true;
            else currencyValueRub += rub.Value;
            balances.Add(new CashBalance(currency.IsoCode.ToUpperInvariant(), position.Quantity, rub));
        }

        decimal? cashRubOnly = null;
        if (account.CashRub is { } cashRub)
        {
            cashRubOnly = CashBreakdown.CashRubOnly(cashRub, currencyValueRub);
            balances.Insert(0, new CashBalance("RUB", cashRubOnly.Value, cashRubOnly.Value));
        }

        var recent = (await _movements.GetRecentAsync(account.Id, RecentMovementsLimit))
            .Select(m => new CashMovementView(m.Date, m.Type, m.Amount, m.Currency, m.AmountRub))
            .ToList();

        return new BrokerCashOverview(
            Brokers.TInvest, account.Id, balances, cashRubOnly, account.CashRub,
            account.BalanceSyncedAt is { } s ? DateTime.SpecifyKind(s, DateTimeKind.Utc) : null, incomplete, recent);
    }

    private async Task<BrokerCashOverview> BuildFreedomAsync(Account account, DateOnly asOf, CancellationToken ct)
    {
        var incomplete = false;
        var balances = new List<CashBalance>();
        DateTime? syncedAt = null;
        foreach (var cash in (await _brokerCash.GetByAccountAsync(account.Id)).OrderBy(c => c.Currency))
        {
            ct.ThrowIfCancellationRequested();
            var currency = cash.Currency.ToUpperInvariant();
            var rate = await RateAsync(currency, asOf, ct);
            if (rate is null) incomplete = true;
            balances.Add(new CashBalance(currency, cash.Amount, rate is { } r ? cash.Amount * r : null));
            if (syncedAt is null || cash.SyncedAt > syncedAt) syncedAt = cash.SyncedAt;
        }

        var rubRow = balances.FirstOrDefault(b => b.Currency == "RUB");
        decimal? total = balances.Count == 0 ? null : balances.Sum(b => b.AmountRub ?? 0m);

        var deposits = await _brokerOperations.GetByTypeAsync(account.Id, BrokerOperationType.Deposit);
        var withdrawals = await _brokerOperations.GetByTypeAsync(account.Id, BrokerOperationType.Withdrawal);
        var recent = new List<CashMovementView>();
        foreach (var op in deposits.Concat(withdrawals).OrderByDescending(o => o.Date).ThenByDescending(o => o.Id).Take(RecentMovementsLimit))
        {
            var currency = BrokerAssetClassifier.NormalizeCurrency(op.Currency) ?? op.Currency.ToUpperInvariant();
            var rate = await RateAsync(currency, DateOnly.FromDateTime(op.Date), ct);
            recent.Add(new CashMovementView(
                op.Date,
                op.Type == BrokerOperationType.Withdrawal ? CashMovementType.Withdrawal : CashMovementType.Deposit,
                op.Amount, currency, rate is { } r ? op.Amount * r : null));
        }

        return new BrokerCashOverview(
            Brokers.Freedom, account.Id, balances, rubRow?.Amount, total,
            syncedAt is { } s ? DateTime.SpecifyKind(s, DateTimeKind.Utc) : null, incomplete, recent);
    }

    /// <summary>Рубли за 1 единицу валюты на дату; RUB = 1; нет курса — null.</summary>
    private async Task<decimal?> RateAsync(string currency, DateOnly date, CancellationToken ct) =>
        currency == "RUB" ? 1m : await _rates.GetRubPerUnitAsync(currency, date, ct);
}

using Bonds.Core.Capital;
using Bonds.Core.Currencies;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Bonds.Infrastructure.Sync;

namespace Bonds.Infrastructure.Analytics;

/// <summary>
/// Один из двух самостоятельных портфелей. <see cref="Drift"/> — расчёт <see cref="BasketDriftService"/> (null — счёта брокера нет:
/// позиций и денег нет, считать нечего). <see cref="FreeCash"/> и все суммы — в валюте портфеля (<see cref="Currency"/>).
/// <see cref="TargetsSum"/> — Σ целей (ДОЛЯ, 0 при отсутствии целей), по ней UI предупреждает, что цели не сходятся к 100%.
/// </summary>
public sealed record CapitalPortfolioOverview(
    string Portfolio,
    string Currency,
    bool HasAccount,
    IReadOnlyList<CapitalBasket> Baskets,
    BasketDriftResult Drift,
    decimal FreeCash,
    decimal TargetsSum,
    bool DataIncomplete,
    DateTime? SyncedAtUtc)
{
    /// <summary>
    /// Задача 67 (#7): ключи позиций без цены/курса (как <c>CapitalPosition.Key</c>) и <see cref="SystemBaskets.Cash"/>, если не определена сумма свободных денег.
    /// По ним правила понимают, какой корзины касается неполнота (<c>PolicyInput.IncompleteBaskets</c>).
    /// </summary>
    public IReadOnlyList<string> IncompleteKeys { get; init; } = [];
}

/// <summary>
/// Задача 62: собирает позиции каждого портфеля в его ВАЛЮТЕ и считает отклонения корзин. Только существующие точки истины:
/// <list type="bullet">
/// <item>rub (T-Invest): облигации — <see cref="PortfolioHoldingsBuilder"/> (грязная цена × кол-во), ключ — ISIN, класс <c>bond</c>; фонды —
/// <see cref="IncomeLedgerBuilder.ResolveFundPriceAsync"/> × паи, ключ — тикер, класс <c>fund</c>; валюта T-Invest —
/// <see cref="IncomeLedgerBuilder.ResolveCurrencyPriceAsync"/> × ЕДИНИЦЫ, класс <c>currency</c> → системная корзина <c>fx</c> (вне целей:
/// юани — не облигации и не фонды, цели не имеют); свободные рубли — <see cref="CashBreakdown.CashRubOnly"/> (брокерский <c>cash_rub</c>
/// уже включает валюту, второй раз её не добавляем).</item>
/// <item>usd (Freedom): <see cref="BrokerHoldingsBuilder"/> — стоимость в рублях по курсам bonds, ÷ курс USD (позиции в USD — как есть, без
/// курса); ключ — тикер; свободные деньги — остатки всех валют счёта в долларах.</item>
/// </list>
/// Нет курса/цены — позиция не вносит вклад, <c>DataIncomplete</c> (как везде в репо: не нуль «втихую»). Стартовые корзины сидятся при первом чтении.
/// </summary>
public sealed class CapitalOverviewBuilder
{
    private const string Usd = "USD";

    private readonly ICapitalBasketRepository _baskets;
    private readonly IAccountRepository _accounts;
    private readonly PortfolioHoldingsBuilder _holdings;
    private readonly IFundPositionRepository _fundPositions;
    private readonly IFundRepository _funds;
    private readonly ICurrencyPositionRepository _currencyPositions;
    private readonly ICurrencyRepository _currencies;
    private readonly IncomeLedgerBuilder _income;
    private readonly BrokerHoldingsBuilder _brokerHoldings;
    private readonly ICurrencyRateLookup _rates;

    public CapitalOverviewBuilder(
        ICapitalBasketRepository baskets,
        IAccountRepository accounts,
        PortfolioHoldingsBuilder holdings,
        IFundPositionRepository fundPositions,
        IFundRepository funds,
        ICurrencyPositionRepository currencyPositions,
        ICurrencyRepository currencies,
        IncomeLedgerBuilder income,
        BrokerHoldingsBuilder brokerHoldings,
        ICurrencyRateLookup rates)
    {
        _baskets = baskets;
        _accounts = accounts;
        _holdings = holdings;
        _fundPositions = fundPositions;
        _funds = funds;
        _currencyPositions = currencyPositions;
        _currencies = currencies;
        _income = income;
        _brokerHoldings = brokerHoldings;
        _rates = rates;
    }

    /// <summary>Оба портфеля пользователя (rub, затем usd). Первый вызов сидит стартовые корзины.</summary>
    public async Task<IReadOnlyList<CapitalPortfolioOverview>> BuildAsync(ulong userId, DateOnly asOf, CancellationToken ct = default)
    {
        await EnsureSeededAsync(userId);
        var all = await _baskets.GetByUserAsync(userId);
        var result = new List<CapitalPortfolioOverview>();
        foreach (var portfolio in CapitalPortfolios.All)
        {
            ct.ThrowIfCancellationRequested();
            var baskets = all.Where(b => b.Portfolio == portfolio).ToList();
            result.Add(await BuildPortfolioAsync(userId, portfolio, baskets, asOf, ct));
        }

        return result;
    }

    /// <summary>Сидит стартовые корзины, если у пользователя их нет.</summary>
    public async Task EnsureSeededAsync(ulong userId)
    {
        var existing = await _baskets.GetByUserAsync(userId);
        if (existing.Count == 0) await _baskets.SeedIfEmptyAsync(userId, DefaultBaskets.All(userId));
    }

    private async Task<CapitalPortfolioOverview> BuildPortfolioAsync(
        ulong userId, string portfolio, IReadOnlyList<CapitalBasket> baskets, DateOnly asOf, CancellationToken ct)
    {
        var account = (await _accounts.GetByUserIdAndBrokerAsync(userId, CapitalPortfolios.BrokerOf(portfolio))).FirstOrDefault();
        var targetsSum = baskets.Where(b => b.TargetWeight is not null).Sum(b => b.TargetWeight!.Value);

        if (account is null)
        {
            return new CapitalPortfolioOverview(
                portfolio, CapitalPortfolios.CurrencyOf(portfolio), false, baskets,
                BasketDriftService.Calculate(baskets, [], 0m), 0m, targetsSum, false, null);
        }

        var valued = portfolio == CapitalPortfolios.Usd
            ? await BuildUsdAsync(account, asOf, ct)
            : await BuildRubAsync(account, ct, asOf);

        return new CapitalPortfolioOverview(
            portfolio, CapitalPortfolios.CurrencyOf(portfolio), true, baskets,
            BasketDriftService.Calculate(baskets, valued.Positions, valued.FreeCash), valued.FreeCash, targetsSum, valued.Incomplete, valued.SyncedAt)
        {
            IncompleteKeys = valued.IncompleteKeys,
        };
    }

    private sealed record Valued(List<CapitalPosition> Positions, decimal FreeCash, bool Incomplete, DateTime? SyncedAt, List<string> IncompleteKeys);

    private async Task<Valued> BuildRubAsync(Account account, CancellationToken ct, DateOnly asOf)
    {
        var positions = new List<CapitalPosition>();
        var incomplete = false;
        var incompleteKeys = new List<string>();

        foreach (var h in (await _holdings.BuildForAccountAsync(account.Id, asOf, ct)).Where(h => h.Quantity > 0m))
        {
            var key = h.Isin ?? $"instrument:{h.InstrumentId}";
            if (h.MarketValueRub <= 0m)
            {
                incomplete = true; // нет котировки — позиция в списке, но вклада нет
                incompleteKeys.Add(key);
            }

            positions.Add(new CapitalPosition(Brokers.TInvest, key, CapitalAssetClasses.Bond, h.Name ?? h.Issuer ?? h.Isin ?? "Облигация", h.MarketValueRub));
        }

        foreach (var p in (await _fundPositions.GetByAccountAsync(account.Id)).Where(p => p.Quantity > 0m))
        {
            ct.ThrowIfCancellationRequested();
            var fund = await _funds.GetByIdAsync(p.FundId);
            if (fund is null) continue;
            var price = await _income.ResolveFundPriceAsync(p, p.FundId, ct);
            var fundKey = string.IsNullOrWhiteSpace(fund.Ticker) ? fund.Isin : fund.Ticker;
            if (price is null)
            {
                incomplete = true;
                incompleteKeys.Add(fundKey);
            }

            positions.Add(new CapitalPosition(Brokers.TInvest, fundKey, CapitalAssetClasses.Fund, fund.Name ?? fund.Ticker ?? fund.Isin, (price ?? 0m) * p.Quantity));
        }

        var currencyValueRub = 0m;
        foreach (var p in (await _currencyPositions.GetByAccountAsync(account.Id)).Where(p => p.Quantity > 0m))
        {
            ct.ThrowIfCancellationRequested();
            var currency = await _currencies.GetByIdAsync(p.CurrencyId);
            if (currency is null) continue;
            var price = await _income.ResolveCurrencyPriceAsync(p, p.CurrencyId, ct);
            if (price is null)
            {
                // Без курса валюты её не вычесть из cash_rub брокера: не определены ни позиция, ни сумма свободных рублей.
                incomplete = true;
                incompleteKeys.Add(currency.IsoCode.ToUpperInvariant());
                incompleteKeys.Add(SystemBaskets.Cash);
            }

            var value = (price ?? 0m) * p.Quantity;
            currencyValueRub += value;
            positions.Add(new CapitalPosition(Brokers.TInvest, currency.IsoCode.ToUpperInvariant(), CapitalAssetClasses.Currency, currency.IsoCode.ToUpperInvariant(), value));
        }

        var freeCash = account.CashRub is { } cashRub ? CashBreakdown.CashRubOnly(cashRub, currencyValueRub) : 0m;
        DateTime? synced = account.BalanceSyncedAt is { } s ? DateTime.SpecifyKind(s, DateTimeKind.Utc) : null;
        return new Valued(positions, freeCash, incomplete, synced, incompleteKeys);
    }

    private async Task<Valued> BuildUsdAsync(Account account, DateOnly asOf, CancellationToken ct)
    {
        var holdings = await _brokerHoldings.BuildAsync(account.Id, asOf, ct);
        var usdRate = await _rates.GetRubPerUnitAsync(Usd, asOf, ct);
        var incomplete = holdings.DataIncomplete;

        // Значение в долларах: бумага в USD — как есть; иначе рубли по курсам bonds ÷ рубль за доллар.
        decimal? ToUsd(string currency, decimal amount, decimal? amountRub)
        {
            if (string.Equals(currency, Usd, StringComparison.OrdinalIgnoreCase)) return amount;
            return amountRub is { } rub && usdRate is > 0m ? rub / usdRate.Value : null;
        }

        var positions = new List<CapitalPosition>();
        var incompleteKeys = new List<string>();
        foreach (var v in holdings.Positions.Where(v => v.Position.Quantity != 0m)) // задача 67 (#14): шорт (qty < 0) входит с отрицательной стоимостью
        {
            var usd = ToUsd(v.Position.Currency, v.MarketValue, v.MarketValueRub);
            if (usd is null)
            {
                incomplete = true;
                incompleteKeys.Add(v.Position.Ticker);
            }

            positions.Add(new CapitalPosition(Brokers.Freedom, v.Position.Ticker, v.Position.AssetClass, v.Position.Name, usd ?? 0m));
        }

        var freeCash = 0m;
        foreach (var c in holdings.Cash)
        {
            var usd = ToUsd(c.Cash.Currency, c.Cash.Amount, c.AmountRub);
            if (usd is null)
            {
                incomplete = true;
                incompleteKeys.Add(SystemBaskets.Cash);
            }
            else
            {
                freeCash += usd.Value;
            }
        }

        return new Valued(positions, freeCash, incomplete, holdings.SyncedAtUtc, incompleteKeys);
    }
}

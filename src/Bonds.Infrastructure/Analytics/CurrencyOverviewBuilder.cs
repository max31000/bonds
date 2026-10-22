using Bonds.Core.Currencies;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;

namespace Bonds.Infrastructure.Analytics;

/// <summary>
/// Валюта счёта со всеми расчётами задачи 55. <see cref="Income"/> - результат <see cref="IncomeLedgerBuilder.BuildCurrenciesAsync"/> (ТА ЖЕ
/// методика, что у страницы «Доходы»); <see cref="Rates"/> - история курса за год (РУБЛИ за 1 единицу), <see cref="RateChange"/> - ДОЛИ.
/// </summary>
public sealed record CurrencyOverviewItem(
    Currency Currency,
    CurrencyPosition? Position,
    IncomeAssetInfo Income,
    IReadOnlyList<CurrencyRatePoint> Rates,
    CurrencyRateChange RateChange);

/// <summary>
/// Сборка данных валют для <c>/api/currencies</c> и баланса (задача 55): P&amp;L - <see cref="IncomeLedgerBuilder"/>, изменение курса -
/// <see cref="CurrencyRateChangeCalculator"/>. Курс валюты везде берётся из ЕДИНОЙ точки <see cref="IncomeLedgerBuilder.ResolveCurrencyPriceAsync"/>.
/// </summary>
public sealed class CurrencyOverviewBuilder
{
    /// <summary>Глубина истории курса для графика и окон изменения (год).</summary>
    public const int HistoryYears = 1;

    /// <summary>Запас назад для forward fill курса на начало 12-месячного окна (выходные/праздники).</summary>
    private const int HistoryLookbackDays = 14;

    private readonly IncomeLedgerBuilder _income;
    private readonly ICurrencyRepository _currencies;
    private readonly ICurrencyPositionRepository _positions;
    private readonly ICurrencyMarketDataRepository _market;

    public CurrencyOverviewBuilder(
        IncomeLedgerBuilder income,
        ICurrencyRepository currencies,
        ICurrencyPositionRepository positions,
        ICurrencyMarketDataRepository market)
    {
        _income = income;
        _currencies = currencies;
        _positions = positions;
        _market = market;
    }

    /// <summary>
    /// Валюты счёта (<paramref name="currencyIds"/> = null - все, у кого есть позиция или журнал, включая проданные).
    /// Актив с упавшим расчётом (<c>Result</c> = null) пропускается из выдачи (его видно на «Доходах» как неполный).
    /// </summary>
    public async Task<IReadOnlyList<CurrencyOverviewItem>> BuildAsync(
        ulong accountId, DateOnly asOf, IReadOnlyCollection<ulong>? currencyIds = null, CancellationToken ct = default)
    {
        var data = await _income.BuildCurrenciesAsync(accountId, asOf, currencyIds, ct);
        var positions = (await _positions.GetByAccountAsync(accountId)).ToDictionary(p => p.CurrencyId);
        var chartFrom = asOf.AddYears(-HistoryYears);
        var fetchFrom = chartFrom.AddDays(-HistoryLookbackDays);

        var items = new List<CurrencyOverviewItem>();
        foreach (var asset in data.Assets)
        {
            ct.ThrowIfCancellationRequested();
            if (asset.Result is null || asset.CurrencyId is not { } currencyId) continue;

            var currency = await _currencies.GetByIdAsync(currencyId);
            if (currency is null) continue;
            positions.TryGetValue(currencyId, out var position);

            var rates = await _market.GetRatesAsync(currencyId, fetchFrom, asOf, ct);
            // Изменение курса считается от ТОГО ЖЕ курса, что и стоимость позиции (единая точка цены); у проданной валюты - от последнего закрытия.
            var current = asset.PriceRub ?? (await _income.ResolveCurrencyPriceAsync(position, currencyId, ct));
            var change = CurrencyRateChangeCalculator.Compute(rates, current, asOf);

            items.Add(new CurrencyOverviewItem(currency, position, asset, rates.Where(r => r.TradeDate >= chartFrom).ToList(), change));
        }

        return items.OrderByDescending(i => i.Income.Result!.Income.MarketValueCleanRub).ThenBy(i => i.Currency.Id).ToList();
    }

    /// <summary>
    /// Σ стоимости валют счёта: единый курс (<see cref="IncomeLedgerBuilder.ResolveCurrencyPriceAsync"/>) × ЕДИНИЦЫ валюты, рубли.
    /// <c>Incomplete</c> = true, если у открытой позиции нет курса вообще (вклад 0). Валюта уже входит в брокерский <c>cashRub</c>
    /// (<c>TotalAmountCurrencies</c>) - это значение лишь ВЫДЕЛЯЕТ её из денег на счёте, к итогу не прибавляется.
    /// </summary>
    public async Task<(decimal ValueRub, bool Incomplete)> GetCurrenciesValueAsync(ulong accountId, CancellationToken ct = default)
    {
        var positions = await _positions.GetByAccountAsync(accountId);
        decimal sum = 0m;
        var incomplete = false;
        foreach (var p in positions.Where(p => p.Quantity > 0m))
        {
            var price = await _income.ResolveCurrencyPriceAsync(p, p.CurrencyId, ct);
            if (price is null) incomplete = true;
            else sum += price.Value * p.Quantity;
        }

        return (sum, incomplete);
    }
}

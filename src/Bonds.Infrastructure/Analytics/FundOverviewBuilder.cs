using Bonds.Core.Funds;
using Bonds.Core.Income;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;

namespace Bonds.Infrastructure.Analytics;

/// <summary>
/// Фонд счёта со всеми расчётами задачи 53. <see cref="Income"/> — результат <see cref="IncomeLedgerBuilder.BuildFundsAsync"/>
/// (ТА ЖЕ методика, что у страницы «Доходы»); <see cref="Prices"/>/<see cref="Distributions"/> — рыночные данные (рубли за пай).
/// </summary>
public sealed record FundOverviewItem(
    Fund Fund,
    FundPosition? Position,
    IncomeAssetInfo Income,
    IReadOnlyList<FundPricePoint> Prices,
    IReadOnlyList<FundDistribution> Distributions,
    FundPerformance Performance,
    FundOutlook Outlook);

/// <summary>
/// Сборка данных фондов для <c>/api/funds</c>, <c>/api/funds/{id}</c> (задача 53): P&amp;L — из <see cref="IncomeLedgerBuilder"/>,
/// доходность по окнам — <see cref="FundPerformanceCalculator"/>, оценка — <see cref="FundOutlookService"/>. Фонды не входят в
/// облигационные аналитики; стоимость облигаций для долей/баланса — тот же <see cref="PortfolioHoldingsBuilder"/> (единое правило).
/// </summary>
public sealed class FundOverviewBuilder
{
    /// <summary>Глубина истории цен для окон/графика/выплат (≤ 2 лет, по плану).</summary>
    public const int HistoryYears = 2;

    private readonly IncomeLedgerBuilder _income;
    private readonly IFundRepository _funds;
    private readonly IFundPositionRepository _positions;
    private readonly IFundMarketDataRepository _market;
    private readonly PortfolioHoldingsBuilder _holdings;

    public FundOverviewBuilder(
        IncomeLedgerBuilder income,
        IFundRepository funds,
        IFundPositionRepository positions,
        IFundMarketDataRepository market,
        PortfolioHoldingsBuilder holdings)
    {
        _income = income;
        _funds = funds;
        _positions = positions;
        _market = market;
        _holdings = holdings;
    }

    /// <summary>
    /// Фонды счёта (<paramref name="fundIds"/> = null — все, у кого есть позиция или журнал). <paramref name="openOnly"/> — только qty &gt; 0.
    /// Актив с упавшим расчётом (<c>Result</c> = null) пропускается из выдачи (его видно на «Доходах» как неполный).
    /// </summary>
    public async Task<IReadOnlyList<FundOverviewItem>> BuildAsync(
        ulong accountId, DateOnly asOf, IReadOnlyCollection<ulong>? fundIds = null, bool openOnly = false, CancellationToken ct = default)
    {
        var data = await _income.BuildFundsAsync(accountId, asOf, fundIds, ct);
        var positions = (await _positions.GetByAccountAsync(accountId)).ToDictionary(p => p.FundId);
        var indexCache = new Dictionary<string, MarketIndexPoint?>();
        var from = asOf.AddYears(-HistoryYears);

        var items = new List<FundOverviewItem>();
        foreach (var asset in data.Assets)
        {
            ct.ThrowIfCancellationRequested();
            if (asset.Result is null || asset.FundId is not { } fundId) continue;
            if (openOnly && asset.Result.Income.QuantityNow <= 0m) continue;

            var fund = await _funds.GetByIdAsync(fundId);
            if (fund is null) continue;
            positions.TryGetValue(fundId, out var position);

            var prices = await _market.GetPricesAsync(fundId, from, asOf, ct);
            var distributions = await _market.GetDistributionsAsync(fundId, ct);

            var indexCode = FundOutlookService.ResolveIndexCode(fund.BenchmarkIndex);
            if (!indexCache.TryGetValue(indexCode, out var indexPoint))
            {
                indexPoint = await _market.GetLatestIndexPointAsync(indexCode, ct);
                indexCache[indexCode] = indexPoint;
            }

            var income = asset.Result.Income;
            var price = position?.CurrentPriceRub ?? prices.OrderByDescending(p => p.TradeDate).Select(p => (decimal?)p.CloseRub).FirstOrDefault();
            var performance = FundPerformanceCalculator.Compute(
                prices, distributions, asOf, income.QuantityNow > 0m || income.InvestedTotalRub > 0m ? income.FirstDate : null);
            var outlook = FundOutlookService.Compute(new FundOutlookInput(
                fund.ForecastMethod, fund.FocusType, fund.Name, fund.Ticker, fund.TotalExpenseFraction,
                price, income.QuantityNow, income.MarketValueCleanRub, prices, distributions,
                indexCode, !string.IsNullOrWhiteSpace(fund.BenchmarkIndex), indexPoint, asOf));

            items.Add(new FundOverviewItem(fund, position, asset, prices, distributions, performance, outlook));
        }

        return items.OrderByDescending(i => i.Income.Result!.Income.MarketValueCleanRub).ThenBy(i => i.Fund.Id).ToList();
    }

    /// <summary>Σ рыночной стоимости облигаций счёта — единое правило <see cref="PortfolioHoldingsBuilder"/> (грязная цена × кол-во).</summary>
    public async Task<decimal> GetBondsValueRubAsync(ulong accountId, DateOnly asOf, CancellationToken ct = default)
    {
        var holdings = await _holdings.BuildForAccountAsync(accountId, asOf, ct);
        return holdings.Sum(h => h.MarketValueRub);
    }

    /// <summary>Σ стоимости паёв счёта: <c>current_price_rub × quantity</c> (рубли; позиция без цены вклада не даёт — как в «Доходах»).</summary>
    public async Task<decimal> GetFundsValueRubAsync(ulong accountId)
    {
        var positions = await _positions.GetByAccountAsync(accountId);
        return positions.Sum(p => (p.CurrentPriceRub ?? 0m) * p.Quantity);
    }
}

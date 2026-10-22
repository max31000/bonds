using Bonds.Core.Analytics;
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
    FundOutlook Outlook,
    FundDurationEstimate DurationEstimate);

/// <summary>Фонд для композиции/сценариев ставок: <see cref="Exposure"/> (рубли, годы) + <see cref="Estimate"/> с источником дюрации.</summary>
public sealed record FundExposureItem(FundExposure Exposure, FundDurationEstimate Estimate);

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
            var indexPoint = await LoadIndexPointAsync(indexCode, indexCache, ct);

            var income = asset.Result.Income;
            var price = asset.PriceRub; // единая цена пая (IncomeLedgerBuilder.ResolveFundPriceAsync)
            var performance = FundPerformanceCalculator.Compute(
                prices, distributions, asOf, income.QuantityNow > 0m || income.InvestedTotalRub > 0m ? income.FirstDate : null);
            var outlook = FundOutlookService.Compute(new FundOutlookInput(
                fund.ForecastMethod, fund.FocusType, fund.Name, fund.Ticker, fund.TotalExpenseFraction,
                price, income.QuantityNow, income.MarketValueCleanRub, prices, distributions,
                indexCode, !string.IsNullOrWhiteSpace(fund.BenchmarkIndex), indexPoint, asOf));

            var durationEstimate = FundDurationEstimator.Estimate(fund, indexPoint, asOf);

            items.Add(new FundOverviewItem(fund, position, asset, prices, distributions, performance, outlook, durationEstimate));
        }

        return items.OrderByDescending(i => i.Income.Result!.Income.MarketValueCleanRub).ThenBy(i => i.Fund.Id).ToList();
    }

    /// <summary>
    /// Последняя точка индекса фонда (резолв кода — <see cref="FundOutlookService.ResolveIndexCode"/>, тот же, что у прогноза доходности и
    /// оценки дюрации), с кэшем на время одной сборки.
    /// </summary>
    private async Task<MarketIndexPoint?> LoadIndexPointAsync(
        string indexCode, Dictionary<string, MarketIndexPoint?> cache, CancellationToken ct)
    {
        if (!cache.TryGetValue(indexCode, out var point))
        {
            point = await _market.GetLatestIndexPointAsync(indexCode, ct);
            cache[indexCode] = point;
        }
        return point;
    }

    /// <summary>Оценка дюрации одного фонда по его актуальным настройкам (для ответа на PUT settings — без полной сборки карточки).</summary>
    public async Task<FundDurationEstimate> GetDurationEstimateAsync(Fund fund, DateOnly asOf, CancellationToken ct = default)
    {
        var point = await LoadIndexPointAsync(FundOutlookService.ResolveIndexCode(fund.BenchmarkIndex), new(), ct);
        return FundDurationEstimator.Estimate(fund, point, asOf);
    }

    /// <summary>
    /// Фонды счёта для композиции и сценариев ставок (задача 57): позиции qty &gt; 0, стоимость = ЕДИНАЯ цена пая
    /// (<see cref="IncomeLedgerBuilder.ResolveFundPriceAsync"/>) × паи, РУБЛИ; дюрация — <see cref="FundDurationEstimator"/> (годы, оценка).
    /// Фонд без цены (стоимость 0) в выдачу не попадает — он не влияет ни на доли, ни на Δ. Порядок: по убыванию стоимости.
    /// </summary>
    public async Task<IReadOnlyList<FundExposureItem>> BuildExposuresAsync(ulong accountId, DateOnly asOf, CancellationToken ct = default)
    {
        var positions = (await _positions.GetByAccountAsync(accountId)).Where(p => p.Quantity > 0m).ToList();
        var indexCache = new Dictionary<string, MarketIndexPoint?>();
        var result = new List<FundExposureItem>();

        foreach (var position in positions)
        {
            ct.ThrowIfCancellationRequested();
            var price = await _income.ResolveFundPriceAsync(position, position.FundId, ct);
            if (price is not > 0m) continue; // нет цены (или 0) — фонд не вносит вклад и не включает переключатель
            var fund = await _funds.GetByIdAsync(position.FundId);
            if (fund is null) continue;

            var point = await LoadIndexPointAsync(FundOutlookService.ResolveIndexCode(fund.BenchmarkIndex), indexCache, ct);
            var estimate = FundDurationEstimator.Estimate(fund, point, asOf);
            result.Add(new FundExposureItem(
                new FundExposure(fund.Id, fund.Ticker, fund.Name, price.Value * position.Quantity, estimate.ModifiedDurationYears),
                estimate));
        }

        return result.OrderByDescending(r => r.Exposure.MarketValueRub).ThenBy(r => r.Exposure.FundId).ToList();
    }

    /// <summary>Σ рыночной стоимости облигаций счёта — единое правило <see cref="PortfolioHoldingsBuilder"/> (грязная цена × кол-во).</summary>
    public async Task<decimal> GetBondsValueRubAsync(ulong accountId, DateOnly asOf, CancellationToken ct = default)
    {
        var holdings = await _holdings.BuildForAccountAsync(accountId, asOf, ct);
        return holdings.Sum(h => h.MarketValueRub);
    }

    /// <summary>
    /// Σ стоимости паёв счёта: единая цена пая (<see cref="IncomeLedgerBuilder.ResolveFundPriceAsync"/>: current_price_rub, иначе последнее
    /// закрытие) × кол-во, рубли. <c>Incomplete</c> = true, если у открытой позиции нет цены вообще (вклад 0).
    /// </summary>
    public async Task<(decimal ValueRub, bool Incomplete)> GetFundsValueAsync(ulong accountId, CancellationToken ct = default)
    {
        var positions = await _positions.GetByAccountAsync(accountId);
        decimal sum = 0m;
        var incomplete = false;
        foreach (var p in positions.Where(p => p.Quantity > 0m))
        {
            var price = await _income.ResolveFundPriceAsync(p, p.FundId, ct);
            if (price is null) incomplete = true;
            else sum += price.Value * p.Quantity;
        }
        return (sum, incomplete);
    }

    public async Task<decimal> GetFundsValueRubAsync(ulong accountId, CancellationToken ct = default) =>
        (await GetFundsValueAsync(accountId, ct)).ValueRub;
}

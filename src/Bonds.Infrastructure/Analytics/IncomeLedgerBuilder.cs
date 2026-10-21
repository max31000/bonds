using Bonds.Core.Analytics;
using Bonds.Core.Income;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Microsoft.Extensions.Logging;

namespace Bonds.Infrastructure.Analytics;

/// <summary>
/// Актив единого журнала доходов с метаданными для UI. <see cref="Result"/> = null — расчёт актива упал
/// (<see cref="DataIncomplete"/> = true), остальные активы считаются независимо.
/// </summary>
public sealed record IncomeAssetInfo(
    AssetKind Kind,
    ulong AssetId,
    string? Name,
    string? Ticker,
    string? Isin,
    ulong? PositionId,
    ulong? FundId,
    bool DataIncomplete,
    AssetIncome? Result,
    decimal? PriceRub = null);

/// <summary>Результат сборки: активы (включая закрытые) + записи «по счёту» (налоги/комиссии без актива).</summary>
public sealed record IncomeLedgerData(
    IReadOnlyList<IncomeAssetInfo> Assets,
    IReadOnlyList<LedgerEntry> AccountEntries);

/// <summary>
/// Сборка входов <see cref="PositionIncomeCalculator"/> (задача 52): читает <c>operations</c> и <c>fund_operations</c> счёта
/// ОДНИМ запросом каждое, текущую стоимость облигаций берёт из <see cref="PortfolioHoldingsBuilder"/> (единое правило
/// стоимости: чистая = MarketValue − AccruedPerBond × Qty, НКД остатка = AccruedPerBond × Qty; цену здесь не пересчитываем),
/// фондов — из <c>fund_positions</c> (<c>current_price_rub × quantity</c>, НКД 0). Закрытые активы остаются в выдаче:
/// набор строится из журнала, а не из таблицы позиций.
/// <para>
/// Задача 53 переиспользует <see cref="BuildFundsAsync"/> (доходы по фондам счёта без дублирования логики).
/// </para>
/// </summary>
public sealed class IncomeLedgerBuilder
{
    private readonly PortfolioHoldingsBuilder _holdings;
    private readonly IOperationRepository _operations;
    private readonly IInstrumentRepository _instruments;
    private readonly IFundRepository _funds;
    private readonly IFundPositionRepository _fundPositions;
    private readonly IFundOperationRepository _fundOperations;
    private readonly IFundMarketDataRepository _fundMarket;
    private readonly ILogger<IncomeLedgerBuilder> _logger;

    public IncomeLedgerBuilder(
        PortfolioHoldingsBuilder holdings,
        IOperationRepository operations,
        IInstrumentRepository instruments,
        IFundRepository funds,
        IFundPositionRepository fundPositions,
        IFundOperationRepository fundOperations,
        IFundMarketDataRepository fundMarket,
        ILogger<IncomeLedgerBuilder> logger)
    {
        _holdings = holdings;
        _operations = operations;
        _instruments = instruments;
        _funds = funds;
        _fundPositions = fundPositions;
        _fundOperations = fundOperations;
        _fundMarket = fundMarket;
        _logger = logger;
    }

    /// <summary>Все активы счёта (облигации + фонды, фильтр по <paramref name="kind"/>) и записи «по счёту».</summary>
    public async Task<IncomeLedgerData> BuildAsync(ulong accountId, DateOnly asOf, AssetKind? kind = null, CancellationToken ct = default)
    {
        var assets = new List<IncomeAssetInfo>();
        var account = new List<LedgerEntry>();

        if (kind is null or AssetKind.Bond)
        {
            var bonds = await BuildBondsAsync(accountId, asOf, ct);
            assets.AddRange(bonds.Assets);
            account.AddRange(bonds.AccountEntries);
        }

        if (kind is null or AssetKind.Fund)
        {
            var funds = await BuildFundsAsync(accountId, asOf, null, ct);
            assets.AddRange(funds.Assets);
            account.AddRange(funds.AccountEntries);
        }

        return new IncomeLedgerData(assets, account);
    }

    /// <summary>
    /// Доходы по фондам счёта (<paramref name="fundIds"/> = null — по всем, иначе только указанные). Публичная точка входа для
    /// задачи 53 (<c>/api/funds</c>): тот же калькулятор и те же правила, что у <c>/api/income</c>.
    /// </summary>
    public async Task<IncomeLedgerData> BuildFundsAsync(
        ulong accountId, DateOnly asOf, IReadOnlyCollection<ulong>? fundIds = null, CancellationToken ct = default)
    {
        var ops = (await _fundOperations.GetByAccountAsync(accountId)).ToList();
        var positions = (await _fundPositions.GetByAccountAsync(accountId)).ToDictionary(p => p.FundId);
        var directory = (await _funds.GetAllAsync()).ToDictionary(f => f.Id);

        var account = ops
            .Where(o => o.FundId is null && o.Type is FundOperationType.Tax or FundOperationType.Fee)
            .Select(LedgerEntryMapper.FromFundOperation)
            .ToList();

        var byFund = ops.Where(o => o.FundId is not null).GroupBy(o => o.FundId!.Value).ToDictionary(g => g.Key, g => g.ToList());
        var ids = byFund.Keys.Union(positions.Keys).Where(id => fundIds is null || fundIds.Contains(id)).OrderBy(id => id).ToList();

        var assets = new List<IncomeAssetInfo>(ids.Count);
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            directory.TryGetValue(id, out var fund);
            positions.TryGetValue(id, out var position);
            try
            {
                var entries = byFund.TryGetValue(id, out var list)
                    ? list.Select(LedgerEntryMapper.FromFundOperation).ToList()
                    : [];
                var qty = position?.Quantity ?? 0m;
                // Единая цена фонда: current_price_rub позиции, иначе последнее закрытие из fund_price_history.
                var price = qty > 0m ? await ResolveFundPriceAsync(position, id, ct) : position?.CurrentPriceRub;
                // Рубли за пай × паи; цены нет вообще — стоимость 0 и актив помечен неполным.
                var marketValue = price is { } p ? p * qty : 0m;
                var result = PositionIncomeCalculator.CalculateDetailed(AssetKind.Fund, id, entries, qty, marketValue, 0m, asOf);
                assets.Add(new IncomeAssetInfo(
                    AssetKind.Fund, id, fund?.Name, fund?.Ticker, fund?.Isin, null, id,
                    DataIncomplete: qty > 0m && price is null, result, price));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Income: failed to calculate fund {FundId} — marked dataIncomplete", id);
                assets.Add(new IncomeAssetInfo(AssetKind.Fund, id, fund?.Name, fund?.Ticker, fund?.Isin, null, id, true, null));
            }
        }

        return new IncomeLedgerData(assets, account);
    }

    /// <summary>
    /// ЕДИНАЯ точка цены пая (рубли за пай): <c>fund_positions.current_price_rub</c>, а если его нет — последнее закрытие из
    /// <c>fund_price_history</c>; null — цены нет вообще (стоимость 0 + DataIncomplete). Аналог правила стоимости облигаций:
    /// все потребители (журнал доходов, /api/funds, /api/portfolio/summary) берут цену только отсюда.
    /// </summary>
    public async Task<decimal?> ResolveFundPriceAsync(FundPosition? position, ulong fundId, CancellationToken ct = default)
    {
        if (position?.CurrentPriceRub is > 0m and var direct) return direct;
        var last = await _fundMarket.GetLatestPriceAsync(fundId, ct);
        return last is { CloseRub: > 0m } ? last.CloseRub : null;
    }

    private async Task<IncomeLedgerData> BuildBondsAsync(ulong accountId, DateOnly asOf, CancellationToken ct)
    {
        var ops = (await _operations.GetByAccountIdAsync(accountId)).ToList();
        var holdings = (await _holdings.BuildForAccountAsync(accountId, asOf, ct)).ToDictionary(h => h.InstrumentId);

        var account = ops
            .Where(o => o.InstrumentId is null && o.Type is OperationType.Tax or OperationType.Fee)
            .Select(LedgerEntryMapper.FromOperation)
            .ToList();

        var byInstrument = ops.Where(o => o.InstrumentId is not null).GroupBy(o => o.InstrumentId!.Value).ToDictionary(g => g.Key, g => g.ToList());
        var ids = byInstrument.Keys.Union(holdings.Keys).OrderBy(id => id).ToList();

        var assets = new List<IncomeAssetInfo>(ids.Count);
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            holdings.TryGetValue(id, out var holding);
            Instrument? instrument = null;
            try
            {
                instrument = await _instruments.GetByIdAsync(id);
                var entries = byInstrument.TryGetValue(id, out var list)
                    ? list.Select(LedgerEntryMapper.FromOperation).ToList()
                    : [];

                decimal qty = 0m, clean = 0m, accrued = 0m;
                var noQuote = false;
                if (holding is not null)
                {
                    qty = holding.Quantity;
                    if (qty > 0m && holding.MarketValueRub <= 0m)
                    {
                        noQuote = true; // нет котировки: позиция не вносит вклад в стоимость (правило PortfolioHoldingsBuilder)
                    }
                    else
                    {
                        accrued = holding.AccruedPerBondRub * qty;
                        clean = holding.MarketValueRub - accrued; // грязная → чистая: единое правило, цену не пересчитываем
                    }
                }

                var result = PositionIncomeCalculator.CalculateDetailed(AssetKind.Bond, id, entries, qty, clean, accrued, asOf);
                assets.Add(new IncomeAssetInfo(
                    AssetKind.Bond, id, instrument?.Name, null, instrument?.Isin,
                    holding is { PositionId: > 0 } ? holding.PositionId : null, null,
                    DataIncomplete: noQuote || holding?.DataIncomplete == true, result));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Income: failed to calculate bond instrument {InstrumentId} — marked dataIncomplete", id);
                assets.Add(new IncomeAssetInfo(
                    AssetKind.Bond, id, instrument?.Name, null, instrument?.Isin,
                    holding is { PositionId: > 0 } ? holding.PositionId : null, null, true, null));
            }
        }

        return new IncomeLedgerData(assets, account);
    }
}

using System.Data;
using System.Text;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Dapper;
using MySqlConnector;

namespace Bonds.Infrastructure.Repositories;

/// <summary>
/// Рыночные данные фондов (задача 51): цены паёв (РУБЛИ за пай), выплаты на пай (РУБЛИ), история индексов
/// (yield — ДОЛЯ, duration — ГОДЫ). Батчевый multi-row upsert чанками — образец <see cref="InstrumentRiskLevelRepository"/>.
/// </summary>
public class FundMarketDataRepository : IFundMarketDataRepository
{
    private readonly string _connectionString;

    private const int BatchSize = 500;

    public FundMarketDataRepository(string connectionString) => _connectionString = connectionString;

    private IDbConnection CreateConnection() => new MySqlConnection(_connectionString);

    public async Task UpsertPricesAsync(ulong fundId, IReadOnlyList<FundPricePoint> points, CancellationToken ct = default)
    {
        var unique = points.GroupBy(p => p.TradeDate).Select(g => g.Last()).ToList();
        await BatchUpsertAsync(
            "INSERT INTO fund_price_history (fund_id, trade_date, close_rub) VALUES ",
            "(@F{0}, @D{0}, @C{0})",
            " ON DUPLICATE KEY UPDATE close_rub = VALUES(close_rub)",
            unique,
            (p, i, prm) =>
            {
                prm.Add($"F{i}", fundId);
                prm.Add($"D{i}", p.TradeDate);
                prm.Add($"C{i}", p.CloseRub);
            });
    }

    public async Task<IReadOnlyList<FundPricePoint>> GetPricesAsync(ulong fundId, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        using var conn = CreateConnection();
        var rows = await conn.QueryAsync<PriceRow>(
            @"SELECT trade_date AS TradeDate, close_rub AS CloseRub FROM fund_price_history
              WHERE fund_id = @FundId AND trade_date BETWEEN @From AND @To ORDER BY trade_date",
            new { FundId = fundId, From = from, To = to });
        return rows.Select(r => new FundPricePoint(r.TradeDate, r.CloseRub)).ToList();
    }

    public async Task<FundPricePoint?> GetLatestPriceAsync(ulong fundId, CancellationToken ct = default)
    {
        using var conn = CreateConnection();
        var row = await conn.QueryFirstOrDefaultAsync<PriceRow>(
            @"SELECT trade_date AS TradeDate, close_rub AS CloseRub FROM fund_price_history
              WHERE fund_id = @FundId ORDER BY trade_date DESC LIMIT 1",
            new { FundId = fundId });
        return row is null ? null : new FundPricePoint(row.TradeDate, row.CloseRub);
    }

    public async Task<DateOnly?> GetLastPriceDateAsync(ulong fundId, CancellationToken ct = default)
    {
        using var conn = CreateConnection();
        return await conn.ExecuteScalarAsync<DateOnly?>(
            "SELECT MAX(trade_date) FROM fund_price_history WHERE fund_id = @FundId", new { FundId = fundId });
    }

    public async Task UpsertDistributionsAsync(ulong fundId, IReadOnlyList<FundDistribution> items, CancellationToken ct = default)
    {
        var unique = items.GroupBy(d => d.RecordDate).Select(g => g.Last()).ToList();
        await BatchUpsertAsync(
            "INSERT INTO fund_distributions (fund_id, record_date, payment_date, amount_per_share_rub) VALUES ",
            "(@F{0}, @R{0}, @P{0}, @A{0})",
            " ON DUPLICATE KEY UPDATE payment_date = VALUES(payment_date), amount_per_share_rub = VALUES(amount_per_share_rub)",
            unique,
            (d, i, prm) =>
            {
                prm.Add($"F{i}", fundId);
                prm.Add($"R{i}", d.RecordDate);
                prm.Add($"P{i}", d.PaymentDate);
                prm.Add($"A{i}", d.AmountPerShareRub);
            });
    }

    public async Task<IReadOnlyList<FundDistribution>> GetDistributionsAsync(ulong fundId, CancellationToken ct = default)
    {
        using var conn = CreateConnection();
        var rows = await conn.QueryAsync<DistributionRow>(
            @"SELECT record_date AS RecordDate, payment_date AS PaymentDate, amount_per_share_rub AS AmountPerShareRub
              FROM fund_distributions WHERE fund_id = @FundId ORDER BY record_date",
            new { FundId = fundId });
        return rows.Select(r => new FundDistribution(r.RecordDate, r.PaymentDate, r.AmountPerShareRub)).ToList();
    }

    public async Task UpsertIndexHistoryAsync(string indexCode, IReadOnlyList<MarketIndexPoint> points, CancellationToken ct = default)
    {
        var unique = points.GroupBy(p => p.TradeDate).Select(g => g.Last()).ToList();
        await BatchUpsertAsync(
            "INSERT INTO market_index_history (index_code, trade_date, close_value, yield_fraction, duration_years) VALUES ",
            "(@I{0}, @D{0}, @C{0}, @Y{0}, @U{0})",
            " ON DUPLICATE KEY UPDATE close_value = VALUES(close_value), yield_fraction = VALUES(yield_fraction), duration_years = VALUES(duration_years)",
            unique,
            (p, i, prm) =>
            {
                prm.Add($"I{i}", indexCode);
                prm.Add($"D{i}", p.TradeDate);
                prm.Add($"C{i}", p.CloseValue);
                prm.Add($"Y{i}", p.YieldFraction);
                prm.Add($"U{i}", p.DurationYears);
            });
    }

    public async Task<MarketIndexPoint?> GetLatestIndexPointAsync(string indexCode, CancellationToken ct = default)
    {
        using var conn = CreateConnection();
        var row = await conn.QueryFirstOrDefaultAsync<IndexRow>(
            @"SELECT trade_date AS TradeDate, close_value AS CloseValue, yield_fraction AS YieldFraction,
                     duration_years AS DurationYears
              FROM market_index_history
              WHERE index_code = @Code AND yield_fraction IS NOT NULL
              ORDER BY trade_date DESC LIMIT 1",
            new { Code = indexCode });
        return row is null ? null : new MarketIndexPoint(row.TradeDate, row.CloseValue, row.YieldFraction, row.DurationYears);
    }

    public async Task<DateOnly?> GetLastIndexDateAsync(string indexCode, CancellationToken ct = default)
    {
        using var conn = CreateConnection();
        return await conn.ExecuteScalarAsync<DateOnly?>(
            "SELECT MAX(trade_date) FROM market_index_history WHERE index_code = @Code", new { Code = indexCode });
    }

    private async Task BatchUpsertAsync<T>(
        string insertPrefix, string rowTemplate, string onDuplicate,
        IReadOnlyList<T> items, Action<T, int, DynamicParameters> bind)
    {
        if (items.Count == 0) return;

        using var conn = CreateConnection();
        conn.Open();
        for (var offset = 0; offset < items.Count; offset += BatchSize)
        {
            var chunk = items.Skip(offset).Take(BatchSize).ToList();
            var sb = new StringBuilder(insertPrefix);
            var parameters = new DynamicParameters();
            for (var i = 0; i < chunk.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(string.Format(rowTemplate, i));
                bind(chunk[i], i, parameters);
            }

            sb.Append(onDuplicate);
            await conn.ExecuteAsync(sb.ToString(), parameters);
        }
    }

    // Dapper не умеет сопоставлять позиционные record-конструкторы с DateOnly через TypeHandler —
    // читаем в POCO с сеттерами и собираем record вручную.
    private sealed class PriceRow
    {
        public DateOnly TradeDate { get; set; }
        public decimal CloseRub { get; set; }
    }

    private sealed class DistributionRow
    {
        public DateOnly RecordDate { get; set; }
        public DateOnly? PaymentDate { get; set; }
        public decimal AmountPerShareRub { get; set; }
    }

    private sealed class IndexRow
    {
        public DateOnly TradeDate { get; set; }
        public decimal? CloseValue { get; set; }
        public decimal? YieldFraction { get; set; }
        public decimal? DurationYears { get; set; }
    }
}

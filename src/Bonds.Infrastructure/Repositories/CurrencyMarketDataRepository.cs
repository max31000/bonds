using System.Data;
using System.Text;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Dapper;
using MySqlConnector;

namespace Bonds.Infrastructure.Repositories;

/// <summary>История курса валют (задача 55): <c>close_rub</c> — РУБЛИ за 1 единицу валюты (деление на FACEVALUE — до записи).</summary>
public class CurrencyMarketDataRepository : ICurrencyMarketDataRepository
{
    private readonly string _connectionString;

    private const int BatchSize = 500;

    public CurrencyMarketDataRepository(string connectionString) => _connectionString = connectionString;

    private IDbConnection CreateConnection() => new MySqlConnection(_connectionString);

    public async Task UpsertRatesAsync(ulong currencyId, IReadOnlyList<CurrencyRatePoint> points, CancellationToken ct = default)
    {
        var unique = points.GroupBy(p => p.TradeDate).Select(g => g.Last()).ToList();
        if (unique.Count == 0) return;

        using var conn = CreateConnection();
        conn.Open();
        for (var offset = 0; offset < unique.Count; offset += BatchSize)
        {
            var chunk = unique.Skip(offset).Take(BatchSize).ToList();
            var sb = new StringBuilder("INSERT INTO currency_rate_history (currency_id, trade_date, close_rub) VALUES ");
            var parameters = new DynamicParameters();
            for (var i = 0; i < chunk.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append($"(@C{i}, @D{i}, @R{i})");
                parameters.Add($"C{i}", currencyId);
                parameters.Add($"D{i}", chunk[i].TradeDate);
                parameters.Add($"R{i}", chunk[i].CloseRub);
            }

            sb.Append(" ON DUPLICATE KEY UPDATE close_rub = VALUES(close_rub)");
            await conn.ExecuteAsync(sb.ToString(), parameters);
        }
    }

    public async Task<IReadOnlyList<CurrencyRatePoint>> GetRatesAsync(ulong currencyId, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        using var conn = CreateConnection();
        var rows = await conn.QueryAsync<RateRow>(
            @"SELECT trade_date AS TradeDate, close_rub AS CloseRub FROM currency_rate_history
              WHERE currency_id = @CurrencyId AND trade_date BETWEEN @From AND @To ORDER BY trade_date",
            new { CurrencyId = currencyId, From = from, To = to });
        return rows.Select(r => new CurrencyRatePoint(r.TradeDate, r.CloseRub)).ToList();
    }

    public async Task<CurrencyRatePoint?> GetLatestRateAsync(ulong currencyId, CancellationToken ct = default)
    {
        using var conn = CreateConnection();
        var row = await conn.QueryFirstOrDefaultAsync<RateRow>(
            @"SELECT trade_date AS TradeDate, close_rub AS CloseRub FROM currency_rate_history
              WHERE currency_id = @CurrencyId ORDER BY trade_date DESC LIMIT 1",
            new { CurrencyId = currencyId });
        return row is null ? null : new CurrencyRatePoint(row.TradeDate, row.CloseRub);
    }

    public async Task<DateOnly?> GetLastRateDateAsync(ulong currencyId, CancellationToken ct = default)
    {
        using var conn = CreateConnection();
        return await conn.ExecuteScalarAsync<DateOnly?>(
            "SELECT MAX(trade_date) FROM currency_rate_history WHERE currency_id = @CurrencyId", new { CurrencyId = currencyId });
    }

    // Dapper не сопоставляет позиционные record'ы с DateOnly через TypeHandler — читаем в POCO.
    private sealed class RateRow
    {
        public DateOnly TradeDate { get; set; }
        public decimal CloseRub { get; set; }
    }
}

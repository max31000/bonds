using System.Data;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Dapper;
using MySqlConnector;

namespace Bonds.Infrastructure.Repositories;

public class InstrumentPriceHistoryRepository : IInstrumentPriceHistoryRepository
{
    private readonly string _connectionString;

    public InstrumentPriceHistoryRepository(string connectionString) => _connectionString = connectionString;

    private IDbConnection CreateConnection() => new MySqlConnection(_connectionString);

    public async Task<IReadOnlyList<InstrumentPriceHistory>> GetRangeAsync(ulong instrumentId, DateOnly from, DateOnly to)
    {
        using var conn = CreateConnection();
        var rows = await conn.QueryAsync<InstrumentPriceHistory>(
            @"SELECT id AS Id, instrument_id AS InstrumentId, date AS Date,
                     close_price_percent AS ClosePricePercent, accrued_interest_rub AS AccruedInterestRub,
                     created_at AS CreatedAt
              FROM instrument_price_history
              WHERE instrument_id = @InstrumentId AND date BETWEEN @From AND @To
              ORDER BY date",
            new { InstrumentId = instrumentId, From = from, To = to });

        return rows.AsList();
    }

    public async Task<DateOnly?> GetLatestDateAsync(ulong instrumentId)
    {
        using var conn = CreateConnection();
        return await conn.QueryFirstOrDefaultAsync<DateOnly?>(
            "SELECT MAX(date) FROM instrument_price_history WHERE instrument_id = @InstrumentId",
            new { InstrumentId = instrumentId });
    }

    public async Task UpsertManyAsync(ulong instrumentId, IEnumerable<InstrumentPriceHistory> points)
    {
        var list = points.ToList();
        if (list.Count == 0) return;

        using var conn = CreateConnection();
        conn.Open();
        using var tx = conn.BeginTransaction();

        const string sql = @"
            INSERT INTO instrument_price_history (instrument_id, date, close_price_percent, accrued_interest_rub)
            VALUES (@InstrumentId, @Date, @ClosePricePercent, @AccruedInterestRub)
            ON DUPLICATE KEY UPDATE
                close_price_percent = @ClosePricePercent,
                accrued_interest_rub = @AccruedInterestRub";

        foreach (var point in list)
        {
            await conn.ExecuteAsync(sql, new
            {
                InstrumentId = instrumentId,
                point.Date,
                point.ClosePricePercent,
                point.AccruedInterestRub,
            }, tx);
        }

        tx.Commit();
    }
}

using System.Data;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Dapper;
using MySqlConnector;

namespace Bonds.Infrastructure.Repositories;

public class IntradayQuoteRepository : IIntradayQuoteRepository
{
    private readonly string _connectionString;

    public IntradayQuoteRepository(string connectionString) => _connectionString = connectionString;

    private IDbConnection CreateConnection() => new MySqlConnection(_connectionString);

    public async Task InsertAndPruneAsync(IntradayQuote quote, DateTime retentionCutoffUtc)
    {
        using var conn = CreateConnection();
        conn.Open();
        using var tx = conn.BeginTransaction();

        await conn.ExecuteAsync(
            @"INSERT INTO intraday_quotes (instrument_id, ts_utc, dirty_price_rub)
              VALUES (@InstrumentId, @TsUtc, @DirtyPriceRub)",
            new { quote.InstrumentId, quote.TsUtc, quote.DirtyPriceRub },
            tx);

        // Retention (plan/16 часть A): удаляем строки старше 8 дней при каждой записи — не нужен
        // отдельный периодический job, таблица растёт ограниченно даже без него.
        await conn.ExecuteAsync(
            "DELETE FROM intraday_quotes WHERE ts_utc < @CutoffUtc",
            new { CutoffUtc = retentionCutoffUtc },
            tx);

        tx.Commit();
    }

    public async Task<IntradayQuote?> GetLatestAsync(ulong instrumentId)
    {
        using var conn = CreateConnection();
        return await conn.QueryFirstOrDefaultAsync<IntradayQuote>(
            @"SELECT id AS Id, instrument_id AS InstrumentId, ts_utc AS TsUtc, dirty_price_rub AS DirtyPriceRub
              FROM intraday_quotes
              WHERE instrument_id = @InstrumentId
              ORDER BY ts_utc DESC LIMIT 1",
            new { InstrumentId = instrumentId });
    }

    public async Task<IReadOnlyList<IntradayQuote>> GetRangeAsync(
        IReadOnlyCollection<ulong> instrumentIds, DateTime fromUtc, DateTime toUtc)
    {
        if (instrumentIds.Count == 0) return [];

        using var conn = CreateConnection();
        var rows = await conn.QueryAsync<IntradayQuote>(
            @"SELECT id AS Id, instrument_id AS InstrumentId, ts_utc AS TsUtc, dirty_price_rub AS DirtyPriceRub
              FROM intraday_quotes
              WHERE instrument_id IN @InstrumentIds AND ts_utc BETWEEN @FromUtc AND @ToUtc
              ORDER BY ts_utc",
            new { InstrumentIds = instrumentIds, FromUtc = fromUtc, ToUtc = toUtc });

        return rows.AsList();
    }
}

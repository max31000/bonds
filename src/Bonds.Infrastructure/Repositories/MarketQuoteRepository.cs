using System.Data;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Dapper;
using MySqlConnector;

namespace Bonds.Infrastructure.Repositories;

public class MarketQuoteRepository : IMarketQuoteRepository
{
    private readonly string _connectionString;

    public MarketQuoteRepository(string connectionString) => _connectionString = connectionString;

    private IDbConnection CreateConnection() => new MySqlConnection(_connectionString);

    private const string SelectColumns = @"
        id AS Id, instrument_id AS InstrumentId, as_of AS AsOf, clean_price AS CleanPrice,
        dirty_price AS DirtyPrice, accrued AS Accrued, volume AS Volume, source AS Source,
        created_at AS CreatedAt";

    public async Task<MarketQuote?> GetLatestAsync(ulong instrumentId)
    {
        using var conn = CreateConnection();
        return await conn.QueryFirstOrDefaultAsync<MarketQuote>(
            $@"SELECT {SelectColumns} FROM market_quotes
               WHERE instrument_id = @InstrumentId
               ORDER BY as_of DESC LIMIT 1",
            new { InstrumentId = instrumentId });
    }

    public async Task<IEnumerable<MarketQuote>> GetHistoryAsync(ulong instrumentId, DateOnly from, DateOnly to)
    {
        using var conn = CreateConnection();
        return await conn.QueryAsync<MarketQuote>(
            $@"SELECT {SelectColumns} FROM market_quotes
               WHERE instrument_id = @InstrumentId AND as_of BETWEEN @From AND @To
               ORDER BY as_of",
            new { InstrumentId = instrumentId, From = from, To = to });
    }

    public async Task UpsertAsync(MarketQuote quote)
    {
        using var conn = CreateConnection();
        const string sql = @"
            INSERT INTO market_quotes (instrument_id, as_of, clean_price, dirty_price, accrued, volume, source)
            VALUES (@InstrumentId, @AsOf, @CleanPrice, @DirtyPrice, @Accrued, @Volume, @Source)
            ON DUPLICATE KEY UPDATE
                clean_price = @CleanPrice, dirty_price = @DirtyPrice, accrued = @Accrued, volume = @Volume";

        // Source передаётся как строка явно — см. комментарий в DapperTypeHandlers.cs про fast-path Dapper для enum.
        await conn.ExecuteAsync(sql, new
        {
            quote.InstrumentId,
            quote.AsOf,
            quote.CleanPrice,
            quote.DirtyPrice,
            quote.Accrued,
            quote.Volume,
            Source = quote.Source.ToString(),
        });
    }
}

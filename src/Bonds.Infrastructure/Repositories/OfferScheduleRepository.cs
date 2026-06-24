using System.Data;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Dapper;
using MySqlConnector;

namespace Bonds.Infrastructure.Repositories;

public class OfferScheduleRepository : IOfferScheduleRepository
{
    private readonly string _connectionString;

    public OfferScheduleRepository(string connectionString) => _connectionString = connectionString;

    private IDbConnection CreateConnection() => new MySqlConnection(_connectionString);

    public async Task<IEnumerable<OfferSchedule>> GetByInstrumentIdAsync(ulong instrumentId)
    {
        using var conn = CreateConnection();
        return await conn.QueryAsync<OfferSchedule>(
            @"SELECT id AS Id, instrument_id AS InstrumentId, date AS Date,
                     offer_type AS OfferType, is_executed AS IsExecuted, created_at AS CreatedAt
              FROM offer_schedules WHERE instrument_id = @InstrumentId ORDER BY date",
            new { InstrumentId = instrumentId });
    }

    public async Task ReplaceForInstrumentAsync(ulong instrumentId, IEnumerable<OfferSchedule> schedule)
    {
        using var conn = CreateConnection();
        conn.Open();
        using var tx = conn.BeginTransaction();

        await conn.ExecuteAsync(
            "DELETE FROM offer_schedules WHERE instrument_id = @InstrumentId",
            new { InstrumentId = instrumentId }, tx);

        const string insertSql = @"
            INSERT INTO offer_schedules (instrument_id, date, offer_type, is_executed)
            VALUES (@InstrumentId, @Date, @OfferType, @IsExecuted)";

        foreach (var item in schedule)
        {
            // OfferType передаётся как строка явно — см. комментарий в DapperTypeHandlers.cs про fast-path Dapper для enum.
            await conn.ExecuteAsync(insertSql, new
            {
                InstrumentId = instrumentId,
                item.Date,
                OfferType = item.OfferType.ToString(),
                item.IsExecuted,
            }, tx);
        }

        tx.Commit();
    }
}

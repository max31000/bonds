using System.Data;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Dapper;
using MySqlConnector;

namespace Bonds.Infrastructure.Repositories;

public class AmortizationScheduleRepository : IAmortizationScheduleRepository
{
    private readonly string _connectionString;

    public AmortizationScheduleRepository(string connectionString) => _connectionString = connectionString;

    private IDbConnection CreateConnection() => new MySqlConnection(_connectionString);

    public async Task<IEnumerable<AmortizationSchedule>> GetByInstrumentIdAsync(ulong instrumentId)
    {
        using var conn = CreateConnection();
        return await conn.QueryAsync<AmortizationSchedule>(
            @"SELECT id AS Id, instrument_id AS InstrumentId, date AS Date,
                     amount_rub AS AmountRub, is_known AS IsKnown, created_at AS CreatedAt
              FROM amortization_schedules WHERE instrument_id = @InstrumentId ORDER BY date",
            new { InstrumentId = instrumentId });
    }

    public async Task ReplaceForInstrumentAsync(ulong instrumentId, IEnumerable<AmortizationSchedule> schedule)
    {
        using var conn = CreateConnection();
        conn.Open();
        using var tx = conn.BeginTransaction();

        await conn.ExecuteAsync(
            "DELETE FROM amortization_schedules WHERE instrument_id = @InstrumentId",
            new { InstrumentId = instrumentId }, tx);

        const string insertSql = @"
            INSERT INTO amortization_schedules (instrument_id, date, amount_rub, is_known)
            VALUES (@InstrumentId, @Date, @AmountRub, @IsKnown)";

        foreach (var item in schedule)
        {
            await conn.ExecuteAsync(insertSql, new
            {
                InstrumentId = instrumentId,
                item.Date,
                item.AmountRub,
                item.IsKnown,
            }, tx);
        }

        tx.Commit();
    }
}

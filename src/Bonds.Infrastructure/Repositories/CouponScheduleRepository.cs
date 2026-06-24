using System.Data;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Dapper;
using MySqlConnector;

namespace Bonds.Infrastructure.Repositories;

public class CouponScheduleRepository : ICouponScheduleRepository
{
    private readonly string _connectionString;

    public CouponScheduleRepository(string connectionString) => _connectionString = connectionString;

    private IDbConnection CreateConnection() => new MySqlConnection(_connectionString);

    public async Task<IEnumerable<CouponSchedule>> GetByInstrumentIdAsync(ulong instrumentId)
    {
        using var conn = CreateConnection();
        return await conn.QueryAsync<CouponSchedule>(
            @"SELECT id AS Id, instrument_id AS InstrumentId, coupon_date AS CouponDate,
                     value_rub AS ValueRub, period_days AS PeriodDays, is_known AS IsKnown,
                     created_at AS CreatedAt
              FROM coupon_schedules WHERE instrument_id = @InstrumentId ORDER BY coupon_date",
            new { InstrumentId = instrumentId });
    }

    public async Task ReplaceForInstrumentAsync(ulong instrumentId, IEnumerable<CouponSchedule> schedule)
    {
        using var conn = CreateConnection();
        conn.Open();
        using var tx = conn.BeginTransaction();

        await conn.ExecuteAsync(
            "DELETE FROM coupon_schedules WHERE instrument_id = @InstrumentId",
            new { InstrumentId = instrumentId }, tx);

        const string insertSql = @"
            INSERT INTO coupon_schedules (instrument_id, coupon_date, value_rub, period_days, is_known)
            VALUES (@InstrumentId, @CouponDate, @ValueRub, @PeriodDays, @IsKnown)";

        foreach (var item in schedule)
        {
            await conn.ExecuteAsync(insertSql, new
            {
                InstrumentId = instrumentId,
                item.CouponDate,
                item.ValueRub,
                item.PeriodDays,
                item.IsKnown,
            }, tx);
        }

        tx.Commit();
    }
}

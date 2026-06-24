using System.Data;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Dapper;
using MySqlConnector;

namespace Bonds.Infrastructure.Repositories;

public class YieldCurveRepository : IYieldCurveRepository
{
    private readonly string _connectionString;

    public YieldCurveRepository(string connectionString) => _connectionString = connectionString;

    private IDbConnection CreateConnection() => new MySqlConnection(_connectionString);

    private const string SelectColumns = @"
        id AS Id, as_of AS AsOf, b1 AS B1, b2 AS B2, b3 AS B3, t1 AS T1,
        g1 AS G1, g2 AS G2, g3 AS G3, g4 AS G4, g5 AS G5, g6 AS G6, g7 AS G7, g8 AS G8, g9 AS G9,
        created_at AS CreatedAt";

    public async Task<YieldCurveSnapshot?> GetLatestAsync()
    {
        using var conn = CreateConnection();
        return await conn.QueryFirstOrDefaultAsync<YieldCurveSnapshot>(
            $"SELECT {SelectColumns} FROM yield_curve_snapshots ORDER BY as_of DESC LIMIT 1");
    }

    public async Task<YieldCurveSnapshot?> GetByDateAsync(DateOnly asOf)
    {
        using var conn = CreateConnection();
        return await conn.QueryFirstOrDefaultAsync<YieldCurveSnapshot>(
            $"SELECT {SelectColumns} FROM yield_curve_snapshots WHERE as_of = @AsOf",
            new { AsOf = asOf });
    }

    public async Task<IEnumerable<YieldCurveSnapshot>> GetHistoryAsync(DateOnly from, DateOnly to)
    {
        using var conn = CreateConnection();
        return await conn.QueryAsync<YieldCurveSnapshot>(
            $"SELECT {SelectColumns} FROM yield_curve_snapshots WHERE as_of BETWEEN @From AND @To ORDER BY as_of",
            new { From = from, To = to });
    }

    public async Task UpsertAsync(YieldCurveSnapshot snapshot)
    {
        using var conn = CreateConnection();
        const string sql = @"
            INSERT INTO yield_curve_snapshots (as_of, b1, b2, b3, t1, g1, g2, g3, g4, g5, g6, g7, g8, g9)
            VALUES (@AsOf, @B1, @B2, @B3, @T1, @G1, @G2, @G3, @G4, @G5, @G6, @G7, @G8, @G9)
            ON DUPLICATE KEY UPDATE
                b1 = @B1, b2 = @B2, b3 = @B3, t1 = @T1,
                g1 = @G1, g2 = @G2, g3 = @G3, g4 = @G4, g5 = @G5, g6 = @G6, g7 = @G7, g8 = @G8, g9 = @G9";

        await conn.ExecuteAsync(sql, snapshot);
    }
}

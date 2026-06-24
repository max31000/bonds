using System.Data;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Dapper;
using MySqlConnector;

namespace Bonds.Infrastructure.Repositories;

public class PortfolioValueSnapshotRepository : IPortfolioValueSnapshotRepository
{
    private readonly string _connectionString;

    public PortfolioValueSnapshotRepository(string connectionString) => _connectionString = connectionString;

    private IDbConnection CreateConnection() => new MySqlConnection(_connectionString);

    private const string SelectColumns = @"
        id AS Id, account_id AS AccountId, as_of AS AsOf, market_value_rub AS MarketValueRub,
        xirr_to_date AS XirrToDate, invested_rub AS InvestedRub, created_at AS CreatedAt";

    public async Task<IEnumerable<PortfolioValueSnapshot>> GetByAccountIdAsync(ulong accountId, DateOnly? from = null, DateOnly? to = null)
    {
        using var conn = CreateConnection();
        var sql = $"SELECT {SelectColumns} FROM portfolio_value_snapshots WHERE account_id = @AccountId";
        var parameters = new DynamicParameters();
        parameters.Add("AccountId", accountId);

        if (from.HasValue)
        {
            sql += " AND as_of >= @From";
            parameters.Add("From", from.Value);
        }
        if (to.HasValue)
        {
            sql += " AND as_of <= @To";
            parameters.Add("To", to.Value);
        }
        sql += " ORDER BY as_of";

        return await conn.QueryAsync<PortfolioValueSnapshot>(sql, parameters);
    }

    public async Task<PortfolioValueSnapshot?> GetLatestAsync(ulong accountId)
    {
        using var conn = CreateConnection();
        return await conn.QueryFirstOrDefaultAsync<PortfolioValueSnapshot>(
            $@"SELECT {SelectColumns} FROM portfolio_value_snapshots
               WHERE account_id = @AccountId ORDER BY as_of DESC LIMIT 1",
            new { AccountId = accountId });
    }

    public async Task UpsertAsync(PortfolioValueSnapshot snapshot)
    {
        using var conn = CreateConnection();
        const string sql = @"
            INSERT INTO portfolio_value_snapshots (account_id, as_of, market_value_rub, xirr_to_date, invested_rub)
            VALUES (@AccountId, @AsOf, @MarketValueRub, @XirrToDate, @InvestedRub)
            ON DUPLICATE KEY UPDATE
                market_value_rub = @MarketValueRub, xirr_to_date = @XirrToDate, invested_rub = @InvestedRub";

        await conn.ExecuteAsync(sql, snapshot);
    }
}

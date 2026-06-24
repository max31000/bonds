using System.Data;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Dapper;
using MySqlConnector;

namespace Bonds.Infrastructure.Repositories;

public class TargetAllocationRepository : ITargetAllocationRepository
{
    private readonly string _connectionString;

    public TargetAllocationRepository(string connectionString) => _connectionString = connectionString;

    private IDbConnection CreateConnection() => new MySqlConnection(_connectionString);

    private const string SelectColumns = @"
        id AS Id, account_id AS AccountId, issuer AS Issuer,
        target_share_percent AS TargetSharePercent, max_concentration_percent AS MaxConcentrationPercent,
        target_duration_years AS TargetDurationYears, created_at AS CreatedAt, updated_at AS UpdatedAt";

    public async Task<IEnumerable<TargetAllocation>> GetByAccountIdAsync(ulong accountId)
    {
        using var conn = CreateConnection();
        return await conn.QueryAsync<TargetAllocation>(
            $"SELECT {SelectColumns} FROM target_allocations WHERE account_id = @AccountId ORDER BY id",
            new { AccountId = accountId });
    }

    public async Task<ulong> CreateAsync(TargetAllocation allocation)
    {
        using var conn = CreateConnection();
        const string sql = @"
            INSERT INTO target_allocations
                (account_id, issuer, target_share_percent, max_concentration_percent, target_duration_years)
            VALUES (@AccountId, @Issuer, @TargetSharePercent, @MaxConcentrationPercent, @TargetDurationYears);
            SELECT LAST_INSERT_ID();";

        return await conn.ExecuteScalarAsync<ulong>(sql, allocation);
    }

    public async Task UpdateAsync(TargetAllocation allocation)
    {
        using var conn = CreateConnection();
        const string sql = @"
            UPDATE target_allocations
            SET issuer = @Issuer, target_share_percent = @TargetSharePercent,
                max_concentration_percent = @MaxConcentrationPercent,
                target_duration_years = @TargetDurationYears
            WHERE id = @Id AND account_id = @AccountId";

        await conn.ExecuteAsync(sql, allocation);
    }

    public async Task DeleteAsync(ulong id, ulong accountId)
    {
        using var conn = CreateConnection();
        await conn.ExecuteAsync(
            "DELETE FROM target_allocations WHERE id = @Id AND account_id = @AccountId",
            new { Id = id, AccountId = accountId });
    }
}

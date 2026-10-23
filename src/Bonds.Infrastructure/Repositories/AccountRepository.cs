using System.Data;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Dapper;
using MySqlConnector;

namespace Bonds.Infrastructure.Repositories;

public class AccountRepository : IAccountRepository
{
    private readonly string _connectionString;

    public AccountRepository(string connectionString) => _connectionString = connectionString;

    private IDbConnection CreateConnection() => new MySqlConnection(_connectionString);

    private const string SelectColumns = @"
        id AS Id, user_id AS UserId, broker AS Broker, broker_account_id AS BrokerAccountId, name AS Name,
        cash_rub AS CashRub, broker_total_rub AS BrokerTotalRub, balance_synced_at AS BalanceSyncedAt,
        created_at AS CreatedAt, updated_at AS UpdatedAt";

    public async Task<Account?> GetByIdAsync(ulong id, ulong userId)
    {
        using var conn = CreateConnection();
        return await conn.QueryFirstOrDefaultAsync<Account>(
            $"SELECT {SelectColumns} FROM accounts WHERE id = @Id AND user_id = @UserId",
            new { Id = id, UserId = userId });
    }

    public async Task<Account?> GetByIdAsync(ulong id)
    {
        using var conn = CreateConnection();
        return await conn.QueryFirstOrDefaultAsync<Account>(
            $"SELECT {SelectColumns} FROM accounts WHERE id = @Id", new { Id = id });
    }

    public async Task<IEnumerable<Account>> GetByUserIdAsync(ulong userId)
    {
        using var conn = CreateConnection();
        return await conn.QueryAsync<Account>(
            $"SELECT {SelectColumns} FROM accounts WHERE user_id = @UserId ORDER BY id",
            new { UserId = userId });
    }

    public async Task<ulong> CreateAsync(Account account)
    {
        using var conn = CreateConnection();
        const string sql = @"
            INSERT INTO accounts (user_id, broker, broker_account_id, name)
            VALUES (@UserId, @Broker, @BrokerAccountId, @Name);
            SELECT LAST_INSERT_ID();";

        return await conn.ExecuteScalarAsync<ulong>(sql, new
        {
            account.UserId,
            Broker = string.IsNullOrEmpty(account.Broker) ? Brokers.TInvest : account.Broker,
            account.BrokerAccountId,
            account.Name,
        });
    }

    public async Task UpdateAsync(Account account)
    {
        using var conn = CreateConnection();
        const string sql = @"
            UPDATE accounts SET broker_account_id = @BrokerAccountId, name = @Name
            WHERE id = @Id AND user_id = @UserId";

        await conn.ExecuteAsync(sql, new
        {
            account.BrokerAccountId,
            account.Name,
            account.Id,
            account.UserId,
        });
    }

    public async Task UpdateBalanceAsync(ulong accountId, decimal? cashRub, decimal? brokerTotalRub, DateTime syncedAtUtc)
    {
        using var conn = CreateConnection();
        await conn.ExecuteAsync(
            @"UPDATE accounts
              SET cash_rub = @CashRub, broker_total_rub = @BrokerTotalRub, balance_synced_at = @SyncedAt
              WHERE id = @Id",
            new { Id = accountId, CashRub = cashRub, BrokerTotalRub = brokerTotalRub, SyncedAt = syncedAtUtc });
    }

    public async Task<ulong?> GetPrimaryAccountIdAsync()
    {
        using var conn = CreateConnection();
        return await conn.QueryFirstOrDefaultAsync<ulong?>(
            "SELECT id FROM accounts WHERE broker = @Broker ORDER BY id LIMIT 1", new { Broker = Brokers.TInvest });
    }

    public async Task<IReadOnlyList<Account>> GetByUserIdAndBrokerAsync(ulong userId, string broker)
    {
        using var conn = CreateConnection();
        return (await conn.QueryAsync<Account>(
            $"SELECT {SelectColumns} FROM accounts WHERE user_id = @UserId AND broker = @Broker ORDER BY id",
            new { UserId = userId, Broker = broker })).ToList();
    }
}

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
        id AS Id, user_id AS UserId, broker_account_id AS BrokerAccountId, name AS Name,
        created_at AS CreatedAt, updated_at AS UpdatedAt";

    public async Task<Account?> GetByIdAsync(ulong id, ulong userId)
    {
        using var conn = CreateConnection();
        return await conn.QueryFirstOrDefaultAsync<Account>(
            $"SELECT {SelectColumns} FROM accounts WHERE id = @Id AND user_id = @UserId",
            new { Id = id, UserId = userId });
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
            INSERT INTO accounts (user_id, broker_account_id, name)
            VALUES (@UserId, @BrokerAccountId, @Name);
            SELECT LAST_INSERT_ID();";

        return await conn.ExecuteScalarAsync<ulong>(sql, new
        {
            account.UserId,
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

    public async Task<ulong?> GetPrimaryAccountIdAsync()
    {
        using var conn = CreateConnection();
        return await conn.QueryFirstOrDefaultAsync<ulong?>(
            "SELECT id FROM accounts ORDER BY id LIMIT 1");
    }
}

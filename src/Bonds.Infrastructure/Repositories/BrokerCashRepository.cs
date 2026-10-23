using System.Data;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Dapper;
using MySqlConnector;

namespace Bonds.Infrastructure.Repositories;

/// <summary>Остатки денег не-T-Invest счетов (<c>broker_cash</c>, задача 60), по валюте, в СВОЕЙ валюте.</summary>
public class BrokerCashRepository : IBrokerCashRepository
{
    private readonly string _connectionString;

    public BrokerCashRepository(string connectionString) => _connectionString = connectionString;

    private IDbConnection CreateConnection() => new MySqlConnection(_connectionString);

    public async Task<IReadOnlyList<BrokerCash>> GetByAccountAsync(ulong accountId)
    {
        using var conn = CreateConnection();
        return (await conn.QueryAsync<BrokerCash>(
            @"SELECT id AS Id, account_id AS AccountId, currency AS Currency, amount AS Amount, synced_at AS SyncedAt
              FROM broker_cash WHERE account_id = @AccountId ORDER BY currency",
            new { AccountId = accountId })).ToList();
    }

    public async Task UpsertAsync(BrokerCash cash)
    {
        using var conn = CreateConnection();
        await conn.ExecuteAsync(@"
            INSERT INTO broker_cash (account_id, currency, amount, synced_at)
            VALUES (@AccountId, @Currency, @Amount, @SyncedAt)
            ON DUPLICATE KEY UPDATE amount = @Amount, synced_at = @SyncedAt",
            new { cash.AccountId, cash.Currency, cash.Amount, cash.SyncedAt });
    }

    public async Task<int> DeleteAllExceptAsync(ulong accountId, IReadOnlyCollection<string> keepCurrencies)
    {
        using var conn = CreateConnection();
        return keepCurrencies.Count == 0
            ? await conn.ExecuteAsync("DELETE FROM broker_cash WHERE account_id = @AccountId", new { AccountId = accountId })
            : await conn.ExecuteAsync(
                "DELETE FROM broker_cash WHERE account_id = @AccountId AND currency NOT IN @Keep",
                new { AccountId = accountId, Keep = keepCurrencies.ToArray() });
    }
}

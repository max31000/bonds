using System.Data;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Dapper;
using MySqlConnector;

namespace Bonds.Infrastructure.Repositories;

/// <summary>Журнал операций по фондам (задача 51). Знак <c>amount_rub</c> — как отдал брокер.</summary>
public class FundOperationRepository : IFundOperationRepository
{
    private readonly string _connectionString;

    public FundOperationRepository(string connectionString) => _connectionString = connectionString;

    private IDbConnection CreateConnection() => new MySqlConnection(_connectionString);

    private const string SelectColumns = @"
        id AS Id, account_id AS AccountId, fund_id AS FundId, type AS Type, date AS Date,
        amount_rub AS AmountRub, quantity AS Quantity, external_id AS ExternalId, created_at AS CreatedAt";

    public async Task<int> UpsertManyByExternalIdAsync(IEnumerable<FundOperation> operations)
    {
        const string sql = @"
            INSERT INTO fund_operations (account_id, fund_id, type, date, amount_rub, quantity, external_id)
            VALUES (@AccountId, @FundId, @Type, @Date, @AmountRub, @Quantity, @ExternalId)
            ON DUPLICATE KEY UPDATE
                account_id = @AccountId, fund_id = @FundId, type = @Type, date = @Date,
                amount_rub = @AmountRub, quantity = @Quantity,
                id = LAST_INSERT_ID(id)";

        using var conn = CreateConnection();
        conn.Open();
        using var tx = conn.BeginTransaction();

        var count = 0;
        foreach (var op in operations)
        {
            // Type — строкой явно (Dapper шлёт enum числом, см. DapperTypeHandlers.cs).
            await conn.ExecuteAsync(sql, new
            {
                op.AccountId,
                op.FundId,
                Type = op.Type.ToString(),
                op.Date,
                op.AmountRub,
                op.Quantity,
                op.ExternalId,
            }, tx);
            count++;
        }

        tx.Commit();
        return count;
    }

    public async Task<IReadOnlyList<FundOperation>> GetByAccountAsync(ulong accountId)
    {
        using var conn = CreateConnection();
        return (await conn.QueryAsync<FundOperation>(
            $"SELECT {SelectColumns} FROM fund_operations WHERE account_id = @AccountId ORDER BY date, id",
            new { AccountId = accountId })).ToList();
    }

    public async Task<IReadOnlyList<FundOperation>> GetByFundAsync(ulong accountId, ulong fundId)
    {
        using var conn = CreateConnection();
        return (await conn.QueryAsync<FundOperation>(
            $@"SELECT {SelectColumns} FROM fund_operations
               WHERE account_id = @AccountId AND fund_id = @FundId ORDER BY date, id",
            new { AccountId = accountId, FundId = fundId })).ToList();
    }
}

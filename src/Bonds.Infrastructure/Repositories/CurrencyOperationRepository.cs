using System.Data;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Dapper;
using MySqlConnector;

namespace Bonds.Infrastructure.Repositories;

/// <summary>Журнал операций с валютой (задача 55): суммы — РУБЛИ со знаком брокера, количество — единицы валюты. Образец — <see cref="FundOperationRepository"/>.</summary>
public class CurrencyOperationRepository : ICurrencyOperationRepository
{
    private readonly string _connectionString;

    public CurrencyOperationRepository(string connectionString) => _connectionString = connectionString;

    private IDbConnection CreateConnection() => new MySqlConnection(_connectionString);

    private const string SelectColumns = @"
        id AS Id, account_id AS AccountId, currency_id AS CurrencyId, type AS Type, date AS Date,
        amount_rub AS AmountRub, quantity AS Quantity, external_id AS ExternalId, created_at AS CreatedAt";

    public async Task<int> UpsertManyByExternalIdAsync(IEnumerable<CurrencyOperation> operations)
    {
        const string sql = @"
            INSERT INTO currency_operations (account_id, currency_id, type, date, amount_rub, quantity, external_id)
            VALUES (@AccountId, @CurrencyId, @Type, @Date, @AmountRub, @Quantity, @ExternalId)
            ON DUPLICATE KEY UPDATE
                account_id = @AccountId, currency_id = @CurrencyId, type = @Type, date = @Date,
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
                op.CurrencyId,
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

    public async Task<IReadOnlyList<CurrencyOperation>> GetByAccountAsync(ulong accountId)
    {
        using var conn = CreateConnection();
        return (await conn.QueryAsync<CurrencyOperation>(
            $"SELECT {SelectColumns} FROM currency_operations WHERE account_id = @AccountId ORDER BY date, id",
            new { AccountId = accountId })).ToList();
    }
}

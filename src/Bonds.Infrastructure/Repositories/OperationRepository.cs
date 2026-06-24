using System.Data;
using System.Text;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Dapper;
using MySqlConnector;

namespace Bonds.Infrastructure.Repositories;

public class OperationRepository : IOperationRepository
{
    private readonly string _connectionString;

    public OperationRepository(string connectionString) => _connectionString = connectionString;

    private IDbConnection CreateConnection() => new MySqlConnection(_connectionString);

    private const string SelectColumns = @"
        id AS Id, account_id AS AccountId, instrument_id AS InstrumentId, type AS Type, date AS Date,
        amount_rub AS AmountRub, quantity AS Quantity, external_id AS ExternalId, created_at AS CreatedAt";

    public async Task<Operation?> GetByIdAsync(ulong id, ulong accountId)
    {
        using var conn = CreateConnection();
        return await conn.QueryFirstOrDefaultAsync<Operation>(
            $"SELECT {SelectColumns} FROM operations WHERE id = @Id AND account_id = @AccountId",
            new { Id = id, AccountId = accountId });
    }

    public async Task<Operation?> GetByExternalIdAsync(string externalId)
    {
        using var conn = CreateConnection();
        return await conn.QueryFirstOrDefaultAsync<Operation>(
            $"SELECT {SelectColumns} FROM operations WHERE external_id = @ExternalId",
            new { ExternalId = externalId });
    }

    public async Task<IEnumerable<Operation>> GetByAccountIdAsync(ulong accountId, DateOnly? from = null, DateOnly? to = null)
    {
        using var conn = CreateConnection();
        var sb = new StringBuilder($"SELECT {SelectColumns} FROM operations WHERE account_id = @AccountId");
        var parameters = new DynamicParameters();
        parameters.Add("AccountId", accountId);

        if (from.HasValue)
        {
            sb.Append(" AND date >= @From");
            parameters.Add("From", from.Value.ToDateTime(TimeOnly.MinValue));
        }
        if (to.HasValue)
        {
            sb.Append(" AND date <= @To");
            parameters.Add("To", to.Value.ToDateTime(TimeOnly.MaxValue));
        }
        sb.Append(" ORDER BY date, id");

        return await conn.QueryAsync<Operation>(sb.ToString(), parameters);
    }

    /// <summary>
    /// Идемпотентный upsert по ExternalId (plan/03 §C): INSERT ... ON DUPLICATE KEY UPDATE
    /// на уникальный индекс uq_operations_external_id гарантирует, что повторный синк той же
    /// брокерской операции обновляет существующую строку, а не создаёт дубль.
    /// </summary>
    public async Task<ulong> UpsertByExternalIdAsync(Operation operation)
    {
        using var conn = CreateConnection();
        var id = await UpsertOneAsync(conn, null, operation);
        return id;
    }

    public async Task<int> UpsertManyByExternalIdAsync(IEnumerable<Operation> operations)
    {
        using var conn = CreateConnection();
        conn.Open();
        using var tx = conn.BeginTransaction();

        var count = 0;
        foreach (var op in operations)
        {
            await UpsertOneAsync(conn, tx, op);
            count++;
        }

        tx.Commit();
        return count;
    }

    private static async Task<ulong> UpsertOneAsync(IDbConnection conn, IDbTransaction? tx, Operation operation)
    {
        const string sql = @"
            INSERT INTO operations (account_id, instrument_id, type, date, amount_rub, quantity, external_id)
            VALUES (@AccountId, @InstrumentId, @Type, @Date, @AmountRub, @Quantity, @ExternalId)
            ON DUPLICATE KEY UPDATE
                account_id = @AccountId, instrument_id = @InstrumentId, type = @Type, date = @Date,
                amount_rub = @AmountRub, quantity = @Quantity,
                id = LAST_INSERT_ID(id);
            SELECT LAST_INSERT_ID();";

        // Type передаётся как строка явно — см. комментарий в DapperTypeHandlers.cs про fast-path Dapper для enum.
        return await conn.ExecuteScalarAsync<ulong>(sql, new
        {
            operation.AccountId,
            operation.InstrumentId,
            Type = operation.Type.ToString(),
            operation.Date,
            operation.AmountRub,
            operation.Quantity,
            operation.ExternalId,
        }, tx);
    }
}

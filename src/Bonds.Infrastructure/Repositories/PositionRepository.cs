using System.Data;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Dapper;
using MySqlConnector;

namespace Bonds.Infrastructure.Repositories;

public class PositionRepository : IPositionRepository
{
    private readonly string _connectionString;

    public PositionRepository(string connectionString) => _connectionString = connectionString;

    private IDbConnection CreateConnection() => new MySqlConnection(_connectionString);

    private const string SelectColumns = @"
        id AS Id, account_id AS AccountId, instrument_id AS InstrumentId, quantity AS Quantity,
        avg_purchase_price AS AvgPurchasePrice, accrued AS Accrued, data_incomplete AS DataIncomplete,
        updated_at AS UpdatedAt";

    public async Task<Position?> GetByIdAsync(ulong id, ulong accountId)
    {
        using var conn = CreateConnection();
        return await conn.QueryFirstOrDefaultAsync<Position>(
            $"SELECT {SelectColumns} FROM positions WHERE id = @Id AND account_id = @AccountId",
            new { Id = id, AccountId = accountId });
    }

    public async Task<IEnumerable<Position>> GetByAccountIdAsync(ulong accountId)
    {
        using var conn = CreateConnection();
        return await conn.QueryAsync<Position>(
            $"SELECT {SelectColumns} FROM positions WHERE account_id = @AccountId ORDER BY id",
            new { AccountId = accountId });
    }

    public async Task<Position?> GetByAccountAndInstrumentAsync(ulong accountId, ulong instrumentId)
    {
        using var conn = CreateConnection();
        return await conn.QueryFirstOrDefaultAsync<Position>(
            $@"SELECT {SelectColumns} FROM positions
               WHERE account_id = @AccountId AND instrument_id = @InstrumentId",
            new { AccountId = accountId, InstrumentId = instrumentId });
    }

    public async Task<ulong> UpsertAsync(Position position)
    {
        using var conn = CreateConnection();
        const string sql = @"
            INSERT INTO positions (account_id, instrument_id, quantity, avg_purchase_price, accrued, data_incomplete)
            VALUES (@AccountId, @InstrumentId, @Quantity, @AvgPurchasePrice, @Accrued, @DataIncomplete)
            ON DUPLICATE KEY UPDATE
                quantity = @Quantity, avg_purchase_price = @AvgPurchasePrice,
                accrued = @Accrued, data_incomplete = @DataIncomplete,
                id = LAST_INSERT_ID(id);
            SELECT LAST_INSERT_ID();";

        return await conn.ExecuteScalarAsync<ulong>(sql, position);
    }

    public async Task DeleteAsync(ulong id, ulong accountId)
    {
        using var conn = CreateConnection();
        await conn.ExecuteAsync(
            "DELETE FROM positions WHERE id = @Id AND account_id = @AccountId",
            new { Id = id, AccountId = accountId });
    }
}

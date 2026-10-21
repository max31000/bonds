using System.Data;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Dapper;
using MySqlConnector;

namespace Bonds.Infrastructure.Repositories;

/// <summary>Позиции в фондах (задача 51). Цены — рубли за пай.</summary>
public class FundPositionRepository : IFundPositionRepository
{
    private readonly string _connectionString;

    public FundPositionRepository(string connectionString) => _connectionString = connectionString;

    private IDbConnection CreateConnection() => new MySqlConnection(_connectionString);

    private const string SelectColumns = @"
        id AS Id, account_id AS AccountId, fund_id AS FundId, quantity AS Quantity,
        avg_purchase_price_rub AS AvgPurchasePriceRub, current_price_rub AS CurrentPriceRub,
        price_as_of AS PriceAsOf, updated_at AS UpdatedAt";

    public async Task<IReadOnlyList<FundPosition>> GetByAccountAsync(ulong accountId)
    {
        using var conn = CreateConnection();
        return (await conn.QueryAsync<FundPosition>(
            $"SELECT {SelectColumns} FROM fund_positions WHERE account_id = @AccountId ORDER BY id",
            new { AccountId = accountId })).ToList();
    }

    public async Task<ulong> UpsertAsync(FundPosition position)
    {
        using var conn = CreateConnection();
        const string sql = @"
            INSERT INTO fund_positions (account_id, fund_id, quantity, avg_purchase_price_rub, current_price_rub, price_as_of)
            VALUES (@AccountId, @FundId, @Quantity, @AvgPurchasePriceRub, @CurrentPriceRub, @PriceAsOf)
            ON DUPLICATE KEY UPDATE
                quantity = @Quantity, avg_purchase_price_rub = @AvgPurchasePriceRub,
                current_price_rub = @CurrentPriceRub, price_as_of = @PriceAsOf,
                id = LAST_INSERT_ID(id);
            SELECT LAST_INSERT_ID();";

        return await conn.ExecuteScalarAsync<ulong>(sql, position);
    }

    public async Task<int> DeleteAllExceptAsync(ulong accountId, IReadOnlyCollection<ulong> keepFundIds)
    {
        using var conn = CreateConnection();
        if (keepFundIds.Count == 0)
        {
            return await conn.ExecuteAsync(
                "DELETE FROM fund_positions WHERE account_id = @AccountId",
                new { AccountId = accountId });
        }

        return await conn.ExecuteAsync(
            "DELETE FROM fund_positions WHERE account_id = @AccountId AND fund_id NOT IN @Keep",
            new { AccountId = accountId, Keep = keepFundIds.ToArray() });
    }
}

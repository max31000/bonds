using System.Data;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Dapper;
using MySqlConnector;

namespace Bonds.Infrastructure.Repositories;

/// <summary>Позиции в валюте (задача 55): количество — ЕДИНИЦЫ валюты, цены — РУБЛИ за 1 единицу. Образец — <see cref="FundPositionRepository"/>.</summary>
public class CurrencyPositionRepository : ICurrencyPositionRepository
{
    private readonly string _connectionString;

    public CurrencyPositionRepository(string connectionString) => _connectionString = connectionString;

    private IDbConnection CreateConnection() => new MySqlConnection(_connectionString);

    private const string SelectColumns = @"
        id AS Id, account_id AS AccountId, currency_id AS CurrencyId, quantity AS Quantity,
        avg_purchase_price_rub AS AvgPurchasePriceRub, current_price_rub AS CurrentPriceRub,
        price_as_of AS PriceAsOf, updated_at AS UpdatedAt";

    public async Task<IReadOnlyList<CurrencyPosition>> GetByAccountAsync(ulong accountId)
    {
        using var conn = CreateConnection();
        return (await conn.QueryAsync<CurrencyPosition>(
            $"SELECT {SelectColumns} FROM currency_positions WHERE account_id = @AccountId ORDER BY id",
            new { AccountId = accountId })).ToList();
    }

    public async Task<ulong> UpsertAsync(CurrencyPosition position)
    {
        using var conn = CreateConnection();
        const string sql = @"
            INSERT INTO currency_positions (account_id, currency_id, quantity, avg_purchase_price_rub, current_price_rub, price_as_of)
            VALUES (@AccountId, @CurrencyId, @Quantity, @AvgPurchasePriceRub, @CurrentPriceRub, @PriceAsOf)
            ON DUPLICATE KEY UPDATE
                quantity = @Quantity, avg_purchase_price_rub = @AvgPurchasePriceRub,
                current_price_rub = @CurrentPriceRub, price_as_of = @PriceAsOf,
                id = LAST_INSERT_ID(id);
            SELECT LAST_INSERT_ID();";

        return await conn.ExecuteScalarAsync<ulong>(sql, position);
    }

    public async Task<int> DeleteAllExceptAsync(ulong accountId, IReadOnlyCollection<ulong> keepCurrencyIds)
    {
        using var conn = CreateConnection();
        if (keepCurrencyIds.Count == 0)
        {
            return await conn.ExecuteAsync(
                "DELETE FROM currency_positions WHERE account_id = @AccountId",
                new { AccountId = accountId });
        }

        return await conn.ExecuteAsync(
            "DELETE FROM currency_positions WHERE account_id = @AccountId AND currency_id NOT IN @Keep",
            new { AccountId = accountId, Keep = keepCurrencyIds.ToArray() });
    }
}

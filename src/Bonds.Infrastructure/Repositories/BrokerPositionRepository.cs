using System.Data;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Dapper;
using MySqlConnector;

namespace Bonds.Infrastructure.Repositories;

/// <summary>Позиции не-T-Invest счетов (<c>broker_positions</c>, задача 60). Цены/суммы — в валюте позиции.</summary>
public class BrokerPositionRepository : IBrokerPositionRepository
{
    private readonly string _connectionString;

    public BrokerPositionRepository(string connectionString) => _connectionString = connectionString;

    private IDbConnection CreateConnection() => new MySqlConnection(_connectionString);

    private const string SelectColumns = @"
        id AS Id, account_id AS AccountId, ticker AS Ticker, name AS Name, asset_class AS AssetClass, currency AS Currency,
        quantity AS Quantity, avg_price AS AvgPrice, last_price AS LastPrice, market_value AS MarketValue, synced_at AS SyncedAt";

    public async Task<IReadOnlyList<BrokerPosition>> GetByAccountAsync(ulong accountId)
    {
        using var conn = CreateConnection();
        return (await conn.QueryAsync<BrokerPosition>(
            $"SELECT {SelectColumns} FROM broker_positions WHERE account_id = @AccountId ORDER BY ticker",
            new { AccountId = accountId })).ToList();
    }

    public async Task UpsertAsync(BrokerPosition position)
    {
        using var conn = CreateConnection();
        await conn.ExecuteAsync(@"
            INSERT INTO broker_positions (account_id, ticker, name, asset_class, currency, quantity, avg_price, last_price, market_value, synced_at)
            VALUES (@AccountId, @Ticker, @Name, @AssetClass, @Currency, @Quantity, @AvgPrice, @LastPrice, @MarketValue, @SyncedAt)
            ON DUPLICATE KEY UPDATE
                name = @Name, asset_class = @AssetClass, currency = @Currency, quantity = @Quantity,
                avg_price = @AvgPrice, last_price = @LastPrice, market_value = @MarketValue, synced_at = @SyncedAt",
            new
            {
                position.AccountId, position.Ticker, position.Name, position.AssetClass, position.Currency, position.Quantity,
                position.AvgPrice, position.LastPrice, position.MarketValue, position.SyncedAt,
            });
    }

    public async Task<int> DeleteAllExceptAsync(ulong accountId, IReadOnlyCollection<string> keepTickers)
    {
        using var conn = CreateConnection();
        // Пустой keep-набор: IN @Keep развернулся бы в IN () — синтаксическая ошибка, отдельная ветка.
        return keepTickers.Count == 0
            ? await conn.ExecuteAsync("DELETE FROM broker_positions WHERE account_id = @AccountId", new { AccountId = accountId })
            : await conn.ExecuteAsync(
                "DELETE FROM broker_positions WHERE account_id = @AccountId AND ticker NOT IN @Keep",
                new { AccountId = accountId, Keep = keepTickers.ToArray() });
    }
}

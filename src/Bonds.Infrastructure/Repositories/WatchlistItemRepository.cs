using System.Data;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Dapper;
using MySqlConnector;

namespace Bonds.Infrastructure.Repositories;

public class WatchlistItemRepository : IWatchlistItemRepository
{
    private readonly string _connectionString;

    public WatchlistItemRepository(string connectionString) => _connectionString = connectionString;

    private IDbConnection CreateConnection() => new MySqlConnection(_connectionString);

    private const string SelectColumns = @"
        id AS Id, user_id AS UserId, isin AS Isin, added_at_utc AS AddedAtUtc, note AS Note";

    public async Task<IEnumerable<WatchlistItem>> GetByUserIdAsync(ulong userId)
    {
        using var conn = CreateConnection();
        return await conn.QueryAsync<WatchlistItem>(
            $"SELECT {SelectColumns} FROM watchlist_items WHERE user_id = @UserId ORDER BY added_at_utc DESC, id DESC",
            new { UserId = userId });
    }

    public async Task<WatchlistItem?> GetByIdAsync(ulong id, ulong userId)
    {
        using var conn = CreateConnection();
        return await conn.QueryFirstOrDefaultAsync<WatchlistItem>(
            $"SELECT {SelectColumns} FROM watchlist_items WHERE id = @Id AND user_id = @UserId",
            new { Id = id, UserId = userId });
    }

    public async Task<WatchlistItem?> GetByUserIdAndIsinAsync(ulong userId, string isin)
    {
        using var conn = CreateConnection();
        return await conn.QueryFirstOrDefaultAsync<WatchlistItem>(
            $"SELECT {SelectColumns} FROM watchlist_items WHERE user_id = @UserId AND isin = @Isin",
            new { UserId = userId, Isin = isin });
    }

    public async Task<ulong> CreateAsync(WatchlistItem item)
    {
        using var conn = CreateConnection();
        const string sql = @"
            INSERT INTO watchlist_items (user_id, isin, note)
            VALUES (@UserId, @Isin, @Note);
            SELECT LAST_INSERT_ID();";

        return await conn.ExecuteScalarAsync<ulong>(sql, item);
    }

    public async Task DeleteAsync(ulong id, ulong userId)
    {
        using var conn = CreateConnection();
        await conn.ExecuteAsync(
            "DELETE FROM watchlist_items WHERE id = @Id AND user_id = @UserId",
            new { Id = id, UserId = userId });
    }

    public async Task<IEnumerable<WatchlistItem>> GetAllAsync()
    {
        using var conn = CreateConnection();
        return await conn.QueryAsync<WatchlistItem>($"SELECT {SelectColumns} FROM watchlist_items");
    }
}

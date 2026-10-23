using Bonds.Core.Interfaces.Repositories;
using Dapper;
using MySqlConnector;

namespace Bonds.Infrastructure.Repositories;

/// <summary>Хранилище <c>bot_state</c> (ключ-значение, задача 64).</summary>
public class BotStateRepository : IBotStateRepository
{
    private readonly string _connectionString;

    public BotStateRepository(string connectionString) => _connectionString = connectionString;

    public async Task<string?> GetAsync(string key)
    {
        using var conn = new MySqlConnection(_connectionString);
        return await conn.ExecuteScalarAsync<string?>("SELECT `value` FROM bot_state WHERE `key` = @Key", new { Key = key });
    }

    public async Task SetAsync(string key, string value)
    {
        using var conn = new MySqlConnection(_connectionString);
        await conn.ExecuteAsync(
            "INSERT INTO bot_state (`key`, `value`) VALUES (@Key, @Value) ON DUPLICATE KEY UPDATE `value` = @Value",
            new { Key = key, Value = value });
    }
}

using System.Data;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Dapper;
using MySqlConnector;

namespace Bonds.Infrastructure.Repositories;

public class UserRepository : IUserRepository
{
    private readonly string _connectionString;

    public UserRepository(string connectionString) => _connectionString = connectionString;

    private IDbConnection CreateConnection() => new MySqlConnection(_connectionString);

    public async Task<User?> GetByTelegramIdAsync(long telegramId)
    {
        using var conn = CreateConnection();
        return await conn.QueryFirstOrDefaultAsync<User>(
            @"SELECT id AS Id, telegram_id AS TelegramId, username AS Username,
                     first_name AS FirstName, last_name AS LastName,
                     created_at AS CreatedAt, updated_at AS UpdatedAt
              FROM users WHERE telegram_id = @TelegramId",
            new { TelegramId = telegramId });
    }

    public async Task<User?> GetByIdAsync(ulong id)
    {
        using var conn = CreateConnection();
        return await conn.QueryFirstOrDefaultAsync<User>(
            @"SELECT id AS Id, telegram_id AS TelegramId, username AS Username,
                     first_name AS FirstName, last_name AS LastName,
                     created_at AS CreatedAt, updated_at AS UpdatedAt
              FROM users WHERE id = @Id",
            new { Id = id });
    }

    public async Task<ulong> CreateAsync(User user)
    {
        using var conn = CreateConnection();
        var id = await conn.ExecuteScalarAsync<ulong>(
            @"INSERT INTO users (telegram_id, username, first_name, last_name)
              VALUES (@TelegramId, @Username, @FirstName, @LastName);
              SELECT LAST_INSERT_ID();",
            new
            {
                user.TelegramId,
                user.Username,
                user.FirstName,
                user.LastName,
            });
        return id;
    }

    public async Task UpdateAsync(User user)
    {
        using var conn = CreateConnection();
        await conn.ExecuteAsync(
            @"UPDATE users SET username = @Username, first_name = @FirstName, last_name = @LastName
              WHERE id = @Id",
            new { user.Username, user.FirstName, user.LastName, user.Id });
    }
}

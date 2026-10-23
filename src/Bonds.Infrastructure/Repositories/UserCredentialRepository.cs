using System.Data;
using Bonds.Core.Interfaces.Repositories;
using Dapper;
using MySqlConnector;

namespace Bonds.Infrastructure.Repositories;

/// <summary>Dapper-репозиторий <c>user_credentials</c> (миграция 029). Всё по <c>user_id</c>.</summary>
public class UserCredentialRepository : IUserCredentialRepository
{
    private readonly string _connectionString;

    public UserCredentialRepository(string connectionString) => _connectionString = connectionString;

    private IDbConnection CreateConnection() => new MySqlConnection(_connectionString);

    private const string SelectColumns = @"
        id AS Id, user_id AS UserId, provider AS Provider,
        secret_encrypted AS SecretEncrypted, secret_last4 AS SecretLast4,
        CAST(meta_json AS CHAR) AS MetaJson,
        status AS Status, status_message AS StatusMessage, verified_at AS VerifiedAt";

    public async Task<UserCredentialRecord?> GetAsync(ulong userId, string provider)
    {
        using var conn = CreateConnection();
        return await conn.QueryFirstOrDefaultAsync<UserCredentialRecord>(
            $"SELECT {SelectColumns} FROM user_credentials WHERE user_id = @UserId AND provider = @Provider",
            new { UserId = userId, Provider = provider });
    }

    public async Task<IReadOnlyList<UserCredentialRecord>> ListAsync(ulong userId)
    {
        using var conn = CreateConnection();
        var rows = await conn.QueryAsync<UserCredentialRecord>(
            $"SELECT {SelectColumns} FROM user_credentials WHERE user_id = @UserId ORDER BY provider",
            new { UserId = userId });
        return rows.ToList();
    }

    public async Task UpsertAsync(UserCredentialRecord record)
    {
        using var conn = CreateConnection();
        await conn.ExecuteAsync(@"
            INSERT INTO user_credentials
                (user_id, provider, secret_encrypted, secret_last4, meta_json, status, status_message, verified_at)
            VALUES
                (@UserId, @Provider, @SecretEncrypted, @SecretLast4, @MetaJson, @Status, @StatusMessage, @VerifiedAt)
            ON DUPLICATE KEY UPDATE
                secret_encrypted = @SecretEncrypted,
                secret_last4 = @SecretLast4,
                meta_json = @MetaJson,
                status = @Status,
                status_message = @StatusMessage,
                verified_at = @VerifiedAt", record);
    }

    public async Task UpdateStatusAsync(ulong userId, string provider, string status, string? statusMessage)
    {
        using var conn = CreateConnection();
        await conn.ExecuteAsync(
            @"UPDATE user_credentials SET status = @Status, status_message = @StatusMessage
              WHERE user_id = @UserId AND provider = @Provider",
            new { UserId = userId, Provider = provider, Status = status, StatusMessage = statusMessage });
    }

    public async Task<IReadOnlyList<ulong>> ListUserIdsAsync(string provider, string status)
    {
        using var conn = CreateConnection();
        return (await conn.QueryAsync<ulong>(
            "SELECT user_id FROM user_credentials WHERE provider = @Provider AND status = @Status ORDER BY user_id",
            new { Provider = provider, Status = status })).ToList();
    }

    public async Task DeleteAsync(ulong userId, string provider)
    {
        using var conn = CreateConnection();
        await conn.ExecuteAsync(
            "DELETE FROM user_credentials WHERE user_id = @UserId AND provider = @Provider",
            new { UserId = userId, Provider = provider });
    }
}

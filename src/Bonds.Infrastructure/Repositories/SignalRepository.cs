using System.Data;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Dapper;
using MySqlConnector;

namespace Bonds.Infrastructure.Repositories;

public class SignalRepository : ISignalRepository
{
    private readonly string _connectionString;

    public SignalRepository(string connectionString) => _connectionString = connectionString;

    private IDbConnection CreateConnection() => new MySqlConnection(_connectionString);

    private const string SelectColumns = @"
        id AS Id, account_id AS AccountId, type AS Type, severity AS Severity,
        position_id AS PositionId, instrument_id AS InstrumentId, suggested_action AS SuggestedAction,
        date AS Date, is_read AS IsRead, created_at AS CreatedAt";

    public async Task<IEnumerable<Signal>> GetByAccountIdAsync(ulong accountId, bool? isRead = null)
    {
        using var conn = CreateConnection();
        var sql = $"SELECT {SelectColumns} FROM signals WHERE account_id = @AccountId";
        if (isRead.HasValue)
            sql += " AND is_read = @IsRead";
        sql += " ORDER BY date DESC, id DESC";

        return await conn.QueryAsync<Signal>(sql, new { AccountId = accountId, IsRead = isRead });
    }

    public async Task<ulong> CreateAsync(Signal signal)
    {
        using var conn = CreateConnection();
        const string sql = @"
            INSERT INTO signals (account_id, type, severity, position_id, instrument_id,
                                  suggested_action, date, is_read)
            VALUES (@AccountId, @Type, @Severity, @PositionId, @InstrumentId,
                    @SuggestedAction, @Date, @IsRead);
            SELECT LAST_INSERT_ID();";

        // Type/Severity передаются как строки явно — см. комментарий в DapperTypeHandlers.cs про fast-path Dapper для enum.
        return await conn.ExecuteScalarAsync<ulong>(sql, new
        {
            signal.AccountId,
            Type = signal.Type.ToString(),
            Severity = signal.Severity.ToString(),
            signal.PositionId,
            signal.InstrumentId,
            signal.SuggestedAction,
            signal.Date,
            signal.IsRead,
        });
    }

    public async Task MarkReadAsync(ulong id, ulong accountId)
    {
        using var conn = CreateConnection();
        await conn.ExecuteAsync(
            "UPDATE signals SET is_read = 1 WHERE id = @Id AND account_id = @AccountId",
            new { Id = id, AccountId = accountId });
    }
}

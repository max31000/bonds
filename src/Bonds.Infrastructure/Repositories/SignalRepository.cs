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
        id AS Id, account_id AS AccountId, event_key AS EventKey, type AS Type, severity AS Severity,
        instrument_id AS InstrumentId, subject AS Subject, suggested_action AS SuggestedAction,
        event_date AS EventDate, first_seen_at AS FirstSeenAt, last_seen_at AS LastSeenAt,
        resolved_at AS ResolvedAt, acknowledged_at AS AcknowledgedAt";

    public async Task<IReadOnlyList<Signal>> GetByAccountIdAsync(ulong accountId, bool includeResolved = false)
    {
        using var conn = CreateConnection();
        var sql = $"SELECT {SelectColumns} FROM signals WHERE account_id = @AccountId";
        if (!includeResolved) sql += " AND resolved_at IS NULL";
        sql += " ORDER BY event_date IS NULL, event_date, id";

        return (await conn.QueryAsync<Signal>(sql, new { AccountId = accountId })).ToList();
    }

    public async Task<ulong> InsertAsync(Signal signal)
    {
        using var conn = CreateConnection();
        const string sql = @"
            INSERT INTO signals (account_id, event_key, type, severity, instrument_id, subject,
                                  suggested_action, event_date, first_seen_at, last_seen_at)
            VALUES (@AccountId, @EventKey, @Type, @Severity, @InstrumentId, @Subject,
                    @SuggestedAction, @EventDate, @Now, @Now)
            ON DUPLICATE KEY UPDATE
                severity = VALUES(severity), subject = VALUES(subject),
                suggested_action = VALUES(suggested_action), last_seen_at = VALUES(last_seen_at),
                resolved_at = NULL, id = LAST_INSERT_ID(id);
            SELECT LAST_INSERT_ID();";

        var now = signal.FirstSeenAt == default ? DateTime.UtcNow : signal.FirstSeenAt;
        // Type/Severity передаются как строки явно — см. комментарий в DapperTypeHandlers.cs про fast-path Dapper для enum.
        return await conn.ExecuteScalarAsync<ulong>(sql, new
        {
            signal.AccountId,
            signal.EventKey,
            Type = signal.Type.ToString(),
            Severity = signal.Severity.ToString(),
            signal.InstrumentId,
            signal.Subject,
            signal.SuggestedAction,
            signal.EventDate,
            Now = now,
        });
    }

    public async Task RefreshAsync(ulong id, SignalSeverity severity, string? subject, string? suggestedAction, DateTime seenAtUtc)
    {
        using var conn = CreateConnection();
        await conn.ExecuteAsync(
            @"UPDATE signals SET severity = @Severity, subject = @Subject, suggested_action = @SuggestedAction,
                                 last_seen_at = @SeenAt, resolved_at = NULL
              WHERE id = @Id",
            new { Id = id, Severity = severity.ToString(), Subject = subject, SuggestedAction = suggestedAction, SeenAt = seenAtUtc });
    }

    public async Task ResolveAsync(IReadOnlyCollection<ulong> ids, DateTime resolvedAtUtc)
    {
        if (ids.Count == 0) return;
        using var conn = CreateConnection();
        await conn.ExecuteAsync(
            "UPDATE signals SET resolved_at = @At WHERE id IN @Ids AND resolved_at IS NULL",
            new { At = resolvedAtUtc, Ids = ids });
    }

    public async Task<bool> AcknowledgeAsync(ulong id, ulong accountId, DateTime nowUtc)
    {
        using var conn = CreateConnection();
        var exists = await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM signals WHERE id = @Id AND account_id = @AccountId",
            new { Id = id, AccountId = accountId });
        if (exists == 0) return false;

        await conn.ExecuteAsync(
            "UPDATE signals SET acknowledged_at = @Now WHERE id = @Id AND account_id = @AccountId AND acknowledged_at IS NULL",
            new { Id = id, AccountId = accountId, Now = nowUtc });
        return true;
    }

    public async Task<int> AcknowledgeAllAsync(ulong accountId, DateTime nowUtc)
    {
        using var conn = CreateConnection();
        return await conn.ExecuteAsync(
            "UPDATE signals SET acknowledged_at = @Now WHERE account_id = @AccountId AND resolved_at IS NULL AND acknowledged_at IS NULL",
            new { AccountId = accountId, Now = nowUtc });
    }

    public async Task<int> PurgeResolvedBeforeAsync(ulong accountId, DateTime cutoffUtc)
    {
        using var conn = CreateConnection();
        return await conn.ExecuteAsync(
            "DELETE FROM signals WHERE account_id = @AccountId AND resolved_at IS NOT NULL AND resolved_at < @Cutoff",
            new { AccountId = accountId, Cutoff = cutoffUtc });
    }
}

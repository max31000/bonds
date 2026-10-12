using System.Data;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Dapper;
using MySqlConnector;

namespace Bonds.Infrastructure.Repositories;

public class UserSettingsRepository : IUserSettingsRepository
{
    private readonly string _connectionString;

    public UserSettingsRepository(string connectionString) => _connectionString = connectionString;

    private IDbConnection CreateConnection() => new MySqlConnection(_connectionString);

    private const string SelectColumns = @"
        id AS Id, user_id AS UserId,
        tinvest_token_encrypted AS TInvestTokenEncrypted, tinvest_token_last4 AS TInvestTokenLast4,
        upcoming_event_days_threshold AS UpcomingEventDaysThreshold,
        uninvested_cash_threshold_rub AS UninvestedCashThresholdRub,
        default_max_concentration_pct AS DefaultMaxConcentrationPercent,
        commission_rate_override AS CommissionRateOverride,
        created_at AS CreatedAt, updated_at AS UpdatedAt";

    public async Task<UserSettings?> GetByUserIdAsync(ulong userId)
    {
        using var conn = CreateConnection();
        return await conn.QueryFirstOrDefaultAsync<UserSettings>(
            $"SELECT {SelectColumns} FROM user_settings WHERE user_id = @UserId", new { UserId = userId });
    }

    public async Task UpsertAsync(UserSettings settings)
    {
        using var conn = CreateConnection();
        const string sql = @"
            INSERT INTO user_settings (
                user_id, tinvest_token_encrypted, tinvest_token_last4,
                upcoming_event_days_threshold, uninvested_cash_threshold_rub, default_max_concentration_pct,
                commission_rate_override)
            VALUES (
                @UserId, @TInvestTokenEncrypted, @TInvestTokenLast4,
                @UpcomingEventDaysThreshold, @UninvestedCashThresholdRub, @DefaultMaxConcentrationPercent,
                @CommissionRateOverride)
            ON DUPLICATE KEY UPDATE
                tinvest_token_encrypted = @TInvestTokenEncrypted,
                tinvest_token_last4 = @TInvestTokenLast4,
                upcoming_event_days_threshold = @UpcomingEventDaysThreshold,
                uninvested_cash_threshold_rub = @UninvestedCashThresholdRub,
                default_max_concentration_pct = @DefaultMaxConcentrationPercent,
                commission_rate_override = @CommissionRateOverride";

        await conn.ExecuteAsync(sql, settings);
    }
}

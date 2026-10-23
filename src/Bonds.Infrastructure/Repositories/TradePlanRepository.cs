using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Policy;
using Dapper;
using MySqlConnector;

namespace Bonds.Infrastructure.Repositories;

/// <summary>
/// Планы сделок (задача 63). Деньги — DECIMAL, даты — UTC. Enum-значения (<c>rule</c>, <c>status</c>, <c>side</c>) — строками. Репозиторий только хранит
/// данные: отправки заявок брокерам в коде нет.
/// </summary>
public class TradePlanRepository : ITradePlanRepository
{
    private readonly string _connectionString;

    public TradePlanRepository(string connectionString) => _connectionString = connectionString;

    private MySqlConnection CreateConnection() => new(_connectionString);

    private const string PlanColumns = @"
        id AS Id, user_id AS UserId, rule AS Rule, `trigger` AS `Trigger`, status AS Status, dry_run AS DryRun, created_at AS CreatedAt,
        expires_at AS ExpiresAt, decided_at AS DecidedAt, decided_via AS DecidedVia, CAST(summary_json AS CHAR) AS SummaryJson";

    private const string LineColumns = @"
        plan_id AS PlanId, line_no AS LineNo, broker AS Broker, account_id AS AccountId, instrument_key AS InstrumentKey, ticker AS Ticker,
        side AS Side, quantity_lots AS QuantityLots, lot_size AS LotSize, limit_price AS LimitPrice, limit_price_points AS LimitPricePoints,
        est_amount AS EstAmount, est_fee AS EstFee, est_tax AS EstTax, currency AS Currency, reason AS Reason";

    public async Task<ulong> InsertAsync(TradePlan plan)
    {
        using var conn = CreateConnection();
        await conn.OpenAsync();
        using var tx = await conn.BeginTransactionAsync();
        var id = await conn.ExecuteScalarAsync<ulong>(
            @"INSERT INTO trade_plans (user_id, rule, `trigger`, status, dry_run, created_at, expires_at, decided_at, decided_via, summary_json)
              VALUES (@UserId, @Rule, @Trigger, @Status, @DryRun, @CreatedAt, @ExpiresAt, @DecidedAt, @DecidedVia, @SummaryJson);
              SELECT LAST_INSERT_ID();",
            new
            {
                plan.UserId, plan.Rule, plan.Trigger, plan.Status, plan.DryRun, plan.CreatedAt, plan.ExpiresAt, plan.DecidedAt, plan.DecidedVia, plan.SummaryJson,
            }, tx);

        foreach (var l in plan.Lines)
        {
            await conn.ExecuteAsync(
                @"INSERT INTO trade_plan_lines (plan_id, line_no, broker, account_id, instrument_key, ticker, side, quantity_lots, lot_size, limit_price,
                                                limit_price_points, est_amount, est_fee, est_tax, currency, reason)
                  VALUES (@PlanId, @LineNo, @Broker, @AccountId, @InstrumentKey, @Ticker, @Side, @QuantityLots, @LotSize, @LimitPrice,
                          @LimitPricePoints, @EstAmount, @EstFee, @EstTax, @Currency, @Reason)",
                new
                {
                    PlanId = id, l.LineNo, l.Broker, l.AccountId, l.InstrumentKey, l.Ticker, l.Side, l.QuantityLots, l.LotSize, l.LimitPrice,
                    l.LimitPricePoints, l.EstAmount, l.EstFee, l.EstTax, l.Currency, Reason = Truncate(l.Reason, 500),
                }, tx);
        }

        await tx.CommitAsync();
        return id;
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    public async Task<TradePlan?> GetAsync(ulong userId, ulong id)
    {
        using var conn = CreateConnection();
        var plan = await conn.QuerySingleOrDefaultAsync<TradePlan>(
            $"SELECT {PlanColumns} FROM trade_plans WHERE id = @Id AND user_id = @UserId", new { Id = id, UserId = userId });
        if (plan is null) return null;
        plan.Lines = (await conn.QueryAsync<TradePlanLine>(
            $"SELECT {LineColumns} FROM trade_plan_lines WHERE plan_id = @Id ORDER BY line_no", new { Id = id })).ToList();
        return plan;
    }

    public async Task<IReadOnlyList<TradePlan>> ListAsync(ulong userId, string? status, int limit)
    {
        using var conn = CreateConnection();
        var plans = (await conn.QueryAsync<TradePlan>(
            $@"SELECT {PlanColumns} FROM trade_plans WHERE user_id = @UserId AND (@Status IS NULL OR status = @Status)
               ORDER BY created_at DESC, id DESC LIMIT @Limit",
            new { UserId = userId, Status = status, Limit = Math.Clamp(limit, 1, 500) })).ToList();
        if (plans.Count == 0) return plans;

        var lines = (await conn.QueryAsync<TradePlanLine>(
            $"SELECT {LineColumns} FROM trade_plan_lines WHERE plan_id IN @Ids ORDER BY plan_id, line_no", new { Ids = plans.Select(p => p.Id).ToArray() })).ToList();
        var byPlan = lines.GroupBy(l => l.PlanId).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var plan in plans) plan.Lines = byPlan.TryGetValue(plan.Id, out var l) ? l : [];
        return plans;
    }

    public async Task<bool> ExistsAsync(ulong userId, string rule, string trigger, bool openOnly, DateTime nowUtc, DateTime? createdSince = null)
    {
        using var conn = CreateConnection();
        var count = await conn.ExecuteScalarAsync<long>(
            @"SELECT COUNT(*) FROM trade_plans
              WHERE user_id = @UserId AND rule = @Rule AND `trigger` = @Trigger
                AND (@OpenOnly = 0 OR (status IN @Open AND expires_at > @Now))
                AND (@CreatedSince IS NULL OR created_at >= @CreatedSince)",
            new { UserId = userId, Rule = rule, Trigger = trigger, OpenOnly = openOnly ? 1 : 0, Open = PlanStatuses.Open.ToArray(), Now = nowUtc, CreatedSince = createdSince });
        return count > 0;
    }

    public async Task<bool> HasOpenRuleAsync(ulong userId, string rule, DateTime nowUtc)
    {
        using var conn = CreateConnection();
        var count = await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM trade_plans WHERE user_id = @UserId AND rule = @Rule AND status IN @Open AND expires_at > @Now",
            new { UserId = userId, Rule = rule, Open = PlanStatuses.Open.ToArray(), Now = nowUtc });
        return count > 0;
    }

    public async Task<decimal> SumAutoApprovedRubSinceAsync(ulong userId, DateTime sinceUtc)
    {
        using var conn = CreateConnection();
        return await conn.ExecuteScalarAsync<decimal>(
            @"SELECT COALESCE(SUM(l.est_amount), 0)
              FROM trade_plans p JOIN trade_plan_lines l ON l.plan_id = p.id
              WHERE p.user_id = @UserId AND p.status IN ('AutoApproved', 'Submitting', 'Done') AND p.decided_at >= @Since
                AND l.side = 'Buy' AND l.currency = 'RUB'",
            new { UserId = userId, Since = sinceUtc });
    }

    public async Task<decimal> SumReservedBuysAsync(ulong userId, string broker, string currency, DateTime nowUtc)
    {
        using var conn = CreateConnection();
        return await conn.ExecuteScalarAsync<decimal>(
            @"SELECT COALESCE(SUM(l.est_amount + l.est_fee), 0)
              FROM trade_plans p JOIN trade_plan_lines l ON l.plan_id = p.id
              WHERE p.user_id = @UserId AND p.status IN @Reserving AND p.expires_at > @Now
                AND l.side = 'Buy' AND l.broker = @Broker AND l.currency = @Currency",
            new { UserId = userId, Reserving = PlanStatuses.ReservingCash.ToArray(), Now = nowUtc, Broker = broker, Currency = currency });
    }

    public async Task<bool> TransitionAsync(
        ulong userId, ulong id, IReadOnlyCollection<string> fromStatuses, string toStatus, string? decidedVia, DateTime decidedAtUtc, string? summaryJson)
    {
        using var conn = CreateConnection();
        var affected = await conn.ExecuteAsync(
            @"UPDATE trade_plans
              SET status = @To, decided_at = @DecidedAt, decided_via = @DecidedVia, summary_json = COALESCE(@SummaryJson, summary_json)
              WHERE id = @Id AND user_id = @UserId AND status IN @From",
            new { Id = id, UserId = userId, From = fromStatuses.ToArray(), To = toStatus, DecidedAt = decidedAtUtc, DecidedVia = decidedVia, SummaryJson = summaryJson });
        return affected > 0;
    }

    public async Task<int> ExpirePendingAsync(DateTime nowUtc)
    {
        using var conn = CreateConnection();
        return await conn.ExecuteAsync(
            "UPDATE trade_plans SET status = 'Expired' WHERE status = 'PendingApproval' AND expires_at <= @Now", new { Now = nowUtc });
    }
}

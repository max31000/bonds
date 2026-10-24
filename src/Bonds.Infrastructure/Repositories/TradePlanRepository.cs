using Bonds.Core.Interfaces.Repositories;
using System.Text.Json;
using Bonds.Core.Policy;
using Bonds.Infrastructure.Policy;
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
        expires_at AS ExpiresAt, decided_at AS DecidedAt, decided_via AS DecidedVia, bot_nonce AS BotNonce, cancel_requested AS CancelRequested,
        cash_hold_released_at AS CashHoldReleasedAt, CAST(summary_json AS CHAR) AS SummaryJson";

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
            @"INSERT INTO trade_plans (user_id, rule, `trigger`, status, dry_run, created_at, expires_at, decided_at, decided_via, bot_nonce, summary_json)
              VALUES (@UserId, @Rule, @Trigger, @Status, @DryRun, @CreatedAt, @ExpiresAt, @DecidedAt, @DecidedVia, @BotNonce, @SummaryJson);
              SELECT LAST_INSERT_ID();",
            new
            {
                plan.UserId, plan.Rule, plan.Trigger, plan.Status, plan.DryRun, plan.CreatedAt, plan.ExpiresAt, plan.DecidedAt, plan.DecidedVia, plan.BotNonce, plan.SummaryJson,
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

    public async Task<decimal> SumAutoApprovedRubSinceAsync(ulong userId, DateTime sinceUtc, ulong? excludePlanId = null)
    {
        // Исполняемые/исполненные (Submitting, PartiallyDone, Done) + AutoApproved, ждущий исполнителя (dry_run = 0). AutoApproved dry-run НИКОГДА не исполняется —
        // его деньги не потрачены, в лимит он не входит. Внутри одного цикла раннер сам наращивает счётчик (PolicyRunner.CarryForward).
        // Задача 67 (#10): только планы, решённые АВТОМАТИЧЕСКИ (decided_via = 'auto') — ручная покупка не съедает авто-лимит.
        using var conn = CreateConnection();
        return await conn.ExecuteScalarAsync<decimal>(
            @"SELECT COALESCE(SUM(l.est_amount), 0)
              FROM trade_plans p JOIN trade_plan_lines l ON l.plan_id = p.id
              WHERE p.user_id = @UserId AND p.decided_at >= @Since AND p.decided_via = 'auto'
                AND (p.status IN ('Submitting', 'PartiallyDone', 'Done') OR (p.status = 'AutoApproved' AND p.dry_run = 0))
                AND (@Exclude IS NULL OR p.id <> @Exclude)
                AND l.side = 'Buy' AND l.currency = 'RUB'",
            new { UserId = userId, Since = sinceUtc, Exclude = excludePlanId });
    }

    public async Task<IReadOnlyList<TradePlan>> ListAutoDayPlansAsync(ulong userId, DateTime sinceUtc)
    {
        using var conn = CreateConnection();
        var plans = (await conn.QueryAsync<TradePlan>(
            $@"SELECT {PlanColumns} FROM trade_plans
               WHERE user_id = @UserId AND decided_at >= @Since AND decided_via = 'auto'
                 AND (status IN ('Submitting', 'PartiallyDone', 'Done', 'Failed') OR (status = 'AutoApproved' AND dry_run = 0))
               ORDER BY id",
            new { UserId = userId, Since = sinceUtc })).ToList();
        return await WithLinesAsync(conn, plans);
    }

    public async Task<bool> ExtendExpiryAsync(ulong userId, ulong id, DateTime expiresAtUtc)
    {
        using var conn = CreateConnection();
        var affected = await conn.ExecuteAsync(
            "UPDATE trade_plans SET expires_at = GREATEST(expires_at, @ExpiresAt) WHERE id = @Id AND user_id = @UserId AND status = 'Approved'",
            new { Id = id, UserId = userId, ExpiresAt = expiresAtUtc });
        return affected > 0;
    }

    private static async Task<IReadOnlyList<TradePlan>> WithLinesAsync(MySqlConnection conn, List<TradePlan> plans)
    {
        if (plans.Count == 0) return plans;
        var lines = (await conn.QueryAsync<TradePlanLine>(
            $"SELECT {LineColumns} FROM trade_plan_lines WHERE plan_id IN @Ids ORDER BY plan_id, line_no", new { Ids = plans.Select(p => p.Id).ToArray() })).ToList();
        var byPlan = lines.GroupBy(l => l.PlanId).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var plan in plans) plan.Lines = byPlan.TryGetValue(plan.Id, out var l) ? l : [];
        return plans;
    }

    private const string ExecutableFilter =
        "(status = 'Submitting' OR (status IN ('Approved', 'AutoApproved') AND dry_run = 0))";

    public async Task<IReadOnlyList<ulong>> ListUserIdsWithExecutablePlansAsync()
    {
        using var conn = CreateConnection();
        return (await conn.QueryAsync<ulong>($"SELECT DISTINCT user_id FROM trade_plans WHERE {ExecutableFilter}")).ToList();
    }

    public async Task<IReadOnlyList<TradePlan>> ListExecutableAsync(ulong userId)
    {
        using var conn = CreateConnection();
        var plans = (await conn.QueryAsync<TradePlan>(
            $"SELECT {PlanColumns} FROM trade_plans WHERE user_id = @UserId AND {ExecutableFilter} ORDER BY id",
            new { UserId = userId })).ToList();
        return await WithLinesAsync(conn, plans);
    }

    public async Task<bool> SetExecutionStatusAsync(
        ulong userId, ulong id, IReadOnlyCollection<string> fromStatuses, string toStatus, string? summaryJson)
    {
        using var conn = CreateConnection();
        var affected = await conn.ExecuteAsync(
            @"UPDATE trade_plans SET status = @To, summary_json = COALESCE(@SummaryJson, summary_json)
              WHERE id = @Id AND user_id = @UserId AND status IN @From",
            new { Id = id, UserId = userId, From = fromStatuses.ToArray(), To = toStatus, SummaryJson = summaryJson });
        return affected > 0;
    }

    public async Task<bool> RequestCancelAsync(ulong userId, ulong id)
    {
        using var conn = CreateConnection();
        var affected = await conn.ExecuteAsync(
            @"UPDATE trade_plans SET cancel_requested = 1
              WHERE id = @Id AND user_id = @UserId AND (status = 'Submitting' OR (status IN ('Approved', 'AutoApproved') AND dry_run = 0))",
            new { Id = id, UserId = userId });
        return affected > 0;
    }

    public async Task<bool> RotateNonceAsync(ulong userId, ulong id, string expectedNonce, string newNonce)
    {
        // Сравнение-и-замена: из двух одновременных нажатий одной кнопки выигрывает одно; второе увидит уже другой nonce.
        using var conn = CreateConnection();
        var affected = await conn.ExecuteAsync(
            @"UPDATE trade_plans SET bot_nonce = @New
              WHERE id = @Id AND user_id = @UserId AND bot_nonce = @Expected AND status = 'PendingApproval'",
            new { Id = id, UserId = userId, Expected = expectedNonce, New = newNonce });
        return affected > 0;
    }

    public async Task<decimal> SumReservedBuysAsync(ulong userId, string broker, string currency, DateTime nowUtc, int withdrawHoldTtl = 3)
    {
        var hold = await GetCashHoldAsync(userId, broker, currency, nowUtc, withdrawHoldTtl);
        return await SumReservedBuyOnlyAsync(userId, broker, currency, nowUtc) + hold.Amount;
    }

    public async Task<CashHoldState> GetCashHoldAsync(ulong userId, string broker, string currency, DateTime nowUtc, int withdrawHoldTtl)
    {
        // Кандидаты — планы вывода/выхода с удержанием, не снятым владельцем, у брокера портфеля (любая сторона строк); окно выборки с запасом на TTL
        // (рабочие дни + выходные), точное решение — WithdrawHold.IsActive. План «только удержание» (задача 67, A2) строк не имеет — его брокер берём из
        // summary.portfolio (CapitalPortfolios.BrokerOf).
        using var conn = CreateConnection();
        var since = nowUtc.AddDays(-(Math.Max(0, withdrawHoldTtl) * 2 + 14));
        var plans = (await conn.QueryAsync<TradePlan>(
            $@"SELECT {PlanColumns} FROM trade_plans p
               WHERE p.user_id = @UserId AND p.rule IN @Rules AND p.status IN @Statuses AND p.cash_hold_released_at IS NULL AND p.created_at >= @Since
                 AND (EXISTS (SELECT 1 FROM trade_plan_lines l WHERE l.plan_id = p.id AND l.broker = @Broker)
                      OR NOT EXISTS (SELECT 1 FROM trade_plan_lines l WHERE l.plan_id = p.id))",
            new { UserId = userId, Rules = WithdrawHold.Rules.ToArray(), Statuses = WithdrawHold.Statuses.ToArray(), Since = since, Broker = broker })).ToList();
        var withLines = (await conn.QueryAsync<ulong>(
            "SELECT DISTINCT plan_id FROM trade_plan_lines WHERE plan_id IN @Ids", new { Ids = plans.Select(p => p.Id).DefaultIfEmpty(0UL).ToArray() })).ToHashSet();

        var amount = 0m;
        var count = 0;
        foreach (var plan in plans.Where(p => WithdrawHold.IsActive(p, nowUtc, withdrawHoldTtl)))
        {
            var summary = WithdrawHold.SummaryOf(plan);
            if (summary is null || !string.Equals(summary.Currency, currency, StringComparison.OrdinalIgnoreCase)) continue;
            if (!withLines.Contains(plan.Id)
                && !string.Equals(Bonds.Core.Capital.CapitalPortfolios.BrokerOf(summary.Portfolio), broker, StringComparison.OrdinalIgnoreCase)) continue;
            amount += summary.CashHold;
            count++;
        }

        return new CashHoldState(amount, count);
    }

    public async Task<int> ReleaseCashHoldsAsync(ulong userId, DateTime nowUtc)
    {
        // Снимаем у ВСЕХ планов вывода/выхода пользователя, где удержание ещё не снято: «деньги выведены» — общее подтверждение владельца.
        using var conn = CreateConnection();
        var ids = (await conn.QueryAsync<ulong>(
            @"SELECT id FROM trade_plans
              WHERE user_id = @UserId AND rule IN @Rules AND status IN @Statuses AND cash_hold_released_at IS NULL",
            new { UserId = userId, Rules = WithdrawHold.Rules.ToArray(), Statuses = WithdrawHold.Statuses.ToArray() })).ToList();
        if (ids.Count == 0) return 0;

        var candidates = (await conn.QueryAsync<TradePlan>($"SELECT {PlanColumns} FROM trade_plans WHERE id IN @Ids", new { Ids = ids })).ToList();
        var holding = candidates.Where(p => WithdrawHold.AmountOf(p) > 0m).Select(p => p.Id).ToList();
        if (holding.Count == 0) return 0;
        return await conn.ExecuteAsync(
            "UPDATE trade_plans SET cash_hold_released_at = @Now WHERE user_id = @UserId AND id IN @Ids AND cash_hold_released_at IS NULL",
            new { Now = nowUtc, UserId = userId, Ids = holding });
    }

    private async Task<decimal> SumReservedBuyOnlyAsync(ulong userId, string broker, string currency, DateTime nowUtc)
    {
        // Строки хранятся в валюте ЦЕНЫ (EUR-строка Freedom, USD-комиссия и т.п.), складывать их напрямую нельзя: берём резерв из summary_json
        // (TradePlanSummary.ReservedBuy — уже в валюте ПОРТФЕЛЯ плана). План без поля (до введения) резервирует 0.
        // Задача 67 (#2): живой Approved (dry_run = 0) тоже резервирует — он ждёт открытия торгов и утром отправит заявки на эти деньги.
        using var conn = CreateConnection();
        var summaries = await conn.QueryAsync<string?>(
            @"SELECT CAST(p.summary_json AS CHAR)
              FROM trade_plans p
              WHERE p.user_id = @UserId AND p.expires_at > @Now
                AND (p.status IN @Reserving OR (p.status = 'Approved' AND p.dry_run = 0))
                AND EXISTS (SELECT 1 FROM trade_plan_lines l WHERE l.plan_id = p.id AND l.side = 'Buy' AND l.broker = @Broker)",
            new { UserId = userId, Reserving = PlanStatuses.ReservingCash.ToArray(), Now = nowUtc, Broker = broker });

        var total = 0m;
        foreach (var json in summaries)
        {
            if (string.IsNullOrWhiteSpace(json)) continue;
            try
            {
                var summary = JsonSerializer.Deserialize<TradePlanSummary>(json, PolicyService.JsonOptions);
                if (summary is not null && string.Equals(summary.Currency, currency, StringComparison.OrdinalIgnoreCase)) total += summary.ReservedBuy;
            }
            catch (JsonException)
            {
                // Повреждённая сводка — резерв этого плана неизвестен, не учитываем (план всё равно живёт не дольше TTL).
            }
        }

        return total;
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
        var expired = await conn.ExecuteAsync(
            "UPDATE trade_plans SET status = 'Expired' WHERE status = 'PendingApproval' AND expires_at <= @Now", new { Now = nowUtc });

        // AutoApproved тоже истекает: в dry-run он не исполняется, и без этого висел бы «одобренным» вечно. Помечаем «не исполнен».
        var autos = (await conn.QueryAsync<AutoRow>(
            "SELECT id AS Id, CAST(summary_json AS CHAR) AS Summary FROM trade_plans WHERE status = 'AutoApproved' AND expires_at <= @Now", new { Now = nowUtc })).ToList();
        foreach (var auto in autos)
        {
            expired += await conn.ExecuteAsync(
                "UPDATE trade_plans SET status = 'Expired', summary_json = COALESCE(@Summary, summary_json) WHERE id = @Id AND status = 'AutoApproved'",
                new { auto.Id, Summary = PolicyService.WithNote(auto.Summary, PolicyService.NotExecutedExpiredNote) });
        }

        return expired;
    }

    private sealed class AutoRow
    {
        public ulong Id { get; set; }
        public string? Summary { get; set; }
    }
}

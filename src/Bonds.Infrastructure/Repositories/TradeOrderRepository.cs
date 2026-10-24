using Bonds.Core.Execution;
using Bonds.Core.Interfaces.Repositories;
using Dapper;
using MySqlConnector;

namespace Bonds.Infrastructure.Repositories;

/// <summary>Заявки исполнителя (<c>trade_orders</c>, задача 65). Только данные: брокера не знает. Даты — UTC, суммы — DECIMAL.</summary>
public class TradeOrderRepository : ITradeOrderRepository
{
    private readonly string _connectionString;

    public TradeOrderRepository(string connectionString) => _connectionString = connectionString;

    private MySqlConnection CreateConnection() => new(_connectionString);

    private const string Columns = @"
        id AS Id, plan_id AS PlanId, line_no AS LineNo, attempt AS Attempt, client_order_id AS ClientOrderId, broker_order_id AS BrokerOrderId,
        status AS Status, limit_price AS LimitPrice, price_unit AS PriceUnit, lots_requested AS LotsRequested, lots_filled AS LotsFilled,
        avg_fill_price AS AvgFillPrice, executed_commission AS ExecutedCommission, currency AS Currency, error_code AS ErrorCode,
        error_message AS ErrorMessage, created_at AS CreatedAt, updated_at AS UpdatedAt, halt_alerted_at AS HaltAlertedAt, fee_rechecks AS FeeRechecks,
        fee_checked_at AS FeeCheckedAt, broker AS Broker";

    private static string? Truncate(string? value, int max) => value is null || value.Length <= max ? value : value[..max];

    public async Task<ulong> InsertAsync(TradeOrder order)
    {
        using var conn = CreateConnection();
        return await conn.ExecuteScalarAsync<ulong>(
            @"INSERT INTO trade_orders (plan_id, line_no, attempt, client_order_id, broker_order_id, status, limit_price, price_unit, lots_requested, lots_filled,
                                        avg_fill_price, executed_commission, currency, error_code, error_message, created_at, updated_at, broker)
              VALUES (@PlanId, @LineNo, @Attempt, @ClientOrderId, @BrokerOrderId, @Status, @LimitPrice, @PriceUnit, @LotsRequested, @LotsFilled,
                      @AvgFillPrice, @ExecutedCommission, @Currency, @ErrorCode, @ErrorMessage, @CreatedAt, @UpdatedAt, @Broker);
              SELECT LAST_INSERT_ID();",
            new
            {
                order.PlanId, order.LineNo, order.Attempt, order.ClientOrderId, order.BrokerOrderId, order.Status, order.LimitPrice, order.PriceUnit,
                order.LotsRequested, order.LotsFilled, order.AvgFillPrice, order.ExecutedCommission, order.Currency,
                ErrorCode = Truncate(order.ErrorCode, 64), ErrorMessage = Truncate(order.ErrorMessage, 500), order.CreatedAt, order.UpdatedAt,
                Broker = string.IsNullOrWhiteSpace(order.Broker) ? Bonds.Core.Models.Brokers.TInvest : order.Broker,
            });
    }

    public async Task UpdateAsync(TradeOrder order)
    {
        using var conn = CreateConnection();
        await conn.ExecuteAsync(
            @"UPDATE trade_orders
              SET broker_order_id = @BrokerOrderId, status = @Status, lots_filled = @LotsFilled, avg_fill_price = @AvgFillPrice,
                  executed_commission = @ExecutedCommission, error_code = @ErrorCode, error_message = @ErrorMessage, updated_at = @UpdatedAt,
                  halt_alerted_at = @HaltAlertedAt, fee_rechecks = @FeeRechecks, fee_checked_at = @FeeCheckedAt
              WHERE id = @Id",
            new
            {
                order.Id, order.BrokerOrderId, order.Status, order.LotsFilled, order.AvgFillPrice, order.ExecutedCommission,
                ErrorCode = Truncate(order.ErrorCode, 64), ErrorMessage = Truncate(order.ErrorMessage, 500), order.UpdatedAt,
                order.HaltAlertedAt, order.FeeRechecks, order.FeeCheckedAt,
            });
    }

    public async Task<IReadOnlyList<TradeOrder>> ListByPlanAsync(ulong planId)
    {
        using var conn = CreateConnection();
        return (await conn.QueryAsync<TradeOrder>(
            $"SELECT {Columns} FROM trade_orders WHERE plan_id = @PlanId ORDER BY line_no, attempt", new { PlanId = planId })).ToList();
    }

    public async Task<IReadOnlyList<TradeOrder>> ListActiveByUserAsync(ulong userId)
    {
        using var conn = CreateConnection();
        return (await conn.QueryAsync<TradeOrder>(
            $@"SELECT {Columns} FROM trade_orders
               WHERE plan_id IN (SELECT id FROM trade_plans WHERE user_id = @UserId) AND status IN ('New', 'Submitted', 'PartiallyFilled')
               ORDER BY plan_id, line_no, attempt",
            new { UserId = userId })).ToList();
    }

    public async Task<IReadOnlyList<TradeOrder>> ListActiveByUserAsync(ulong userId, string broker)
    {
        using var conn = CreateConnection();
        return (await conn.QueryAsync<TradeOrder>(
            $@"SELECT {Columns} FROM trade_orders
               WHERE plan_id IN (SELECT id FROM trade_plans WHERE user_id = @UserId) AND status IN ('New', 'Submitted', 'PartiallyFilled') AND broker = @Broker
               ORDER BY plan_id, line_no, attempt",
            new { UserId = userId, Broker = broker })).ToList();
    }

    public async Task<IReadOnlyList<TradeOrder>> ListByPlansAsync(IReadOnlyCollection<ulong> planIds)
    {
        if (planIds.Count == 0) return [];
        using var conn = CreateConnection();
        return (await conn.QueryAsync<TradeOrder>(
            $"SELECT {Columns} FROM trade_orders WHERE plan_id IN @Ids ORDER BY plan_id, line_no, attempt", new { Ids = planIds.ToArray() })).ToList();
    }
}

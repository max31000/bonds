using System.Data;
using System.Text;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Dapper;
using MySqlConnector;

namespace Bonds.Infrastructure.Repositories;

public class ProjectedCashFlowRepository : IProjectedCashFlowRepository
{
    private readonly string _connectionString;

    public ProjectedCashFlowRepository(string connectionString) => _connectionString = connectionString;

    private IDbConnection CreateConnection() => new MySqlConnection(_connectionString);

    private const string SelectColumns = @"
        id AS Id, position_id AS PositionId, instrument_id AS InstrumentId, date AS Date,
        flow_type AS FlowType, gross_rub AS GrossRub, tax_rub AS TaxRub, net_rub AS NetRub,
        is_estimated AS IsEstimated, created_at AS CreatedAt";

    public async Task<IEnumerable<ProjectedCashFlow>> GetByPositionIdAsync(ulong positionId)
    {
        using var conn = CreateConnection();
        return await conn.QueryAsync<ProjectedCashFlow>(
            $"SELECT {SelectColumns} FROM projected_cash_flows WHERE position_id = @PositionId ORDER BY date",
            new { PositionId = positionId });
    }

    public async Task<IEnumerable<ProjectedCashFlow>> GetByAccountIdAsync(ulong accountId, DateOnly? from = null, DateOnly? to = null)
    {
        using var conn = CreateConnection();
        var sb = new StringBuilder($@"
            SELECT pcf.id AS Id, pcf.position_id AS PositionId, pcf.instrument_id AS InstrumentId,
                   pcf.date AS Date, pcf.flow_type AS FlowType, pcf.gross_rub AS GrossRub,
                   pcf.tax_rub AS TaxRub, pcf.net_rub AS NetRub, pcf.is_estimated AS IsEstimated,
                   pcf.created_at AS CreatedAt
            FROM projected_cash_flows pcf
            INNER JOIN positions p ON p.id = pcf.position_id
            WHERE p.account_id = @AccountId");

        var parameters = new DynamicParameters();
        parameters.Add("AccountId", accountId);

        if (from.HasValue)
        {
            sb.Append(" AND pcf.date >= @From");
            parameters.Add("From", from.Value);
        }
        if (to.HasValue)
        {
            sb.Append(" AND pcf.date <= @To");
            parameters.Add("To", to.Value);
        }
        sb.Append(" ORDER BY pcf.date");

        return await conn.QueryAsync<ProjectedCashFlow>(sb.ToString(), parameters);
    }

    public async Task ReplaceForPositionAsync(ulong positionId, IEnumerable<ProjectedCashFlow> flows)
    {
        using var conn = CreateConnection();
        conn.Open();
        using var tx = conn.BeginTransaction();

        await conn.ExecuteAsync(
            "DELETE FROM projected_cash_flows WHERE position_id = @PositionId",
            new { PositionId = positionId }, tx);

        const string insertSql = @"
            INSERT INTO projected_cash_flows
                (position_id, instrument_id, date, flow_type, gross_rub, tax_rub, net_rub, is_estimated)
            VALUES (@PositionId, @InstrumentId, @Date, @FlowType, @GrossRub, @TaxRub, @NetRub, @IsEstimated)";

        foreach (var flow in flows)
        {
            // FlowType передаётся как строка явно — см. комментарий в DapperTypeHandlers.cs про fast-path Dapper для enum.
            await conn.ExecuteAsync(insertSql, new
            {
                PositionId = positionId,
                flow.InstrumentId,
                flow.Date,
                FlowType = flow.FlowType.ToString(),
                flow.GrossRub,
                flow.TaxRub,
                flow.NetRub,
                flow.IsEstimated,
            }, tx);
        }

        tx.Commit();
    }
}

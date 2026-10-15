using System.Data;
using System.Text;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Dapper;
using MySqlConnector;

namespace Bonds.Infrastructure.Repositories;

/// <summary>
/// Задача 46: суточный кэш <c>risk_level</c> облигаций T-Invest. Образец — <see cref="SecurityDetailsRepository"/>
/// (батчевый multi-row upsert чанками). Enum хранится числом (0–3, как протобуф-enum); <c>fetched_at</c> — UTC.
/// </summary>
public class InstrumentRiskLevelRepository : IInstrumentRiskLevelRepository
{
    private readonly string _connectionString;

    /// <summary>Строк на один INSERT-батч (3 параметра на строку).</summary>
    private const int BatchSize = 500;

    public InstrumentRiskLevelRepository(string connectionString) => _connectionString = connectionString;

    private IDbConnection CreateConnection() => new MySqlConnection(_connectionString);

    public async Task UpsertBatchAsync(IReadOnlyList<InstrumentRiskLevel> items, CancellationToken ct = default)
    {
        if (items.Count == 0) return;

        var unique = items
            .GroupBy(i => i.Isin, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last())
            .ToList();

        using var conn = CreateConnection();
        conn.Open();

        for (var offset = 0; offset < unique.Count; offset += BatchSize)
        {
            var chunk = unique.Skip(offset).Take(BatchSize).ToList();
            var sb = new StringBuilder("INSERT INTO instrument_risk_levels (isin, risk_level, fetched_at) VALUES ");
            var parameters = new DynamicParameters();
            for (var i = 0; i < chunk.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append($"(@Isin{i}, @RiskLevel{i}, @FetchedAt{i})");
                parameters.Add($"Isin{i}", chunk[i].Isin);
                parameters.Add($"RiskLevel{i}", (int)chunk[i].RiskLevel);
                parameters.Add($"FetchedAt{i}", chunk[i].FetchedAt);
            }

            sb.Append(" ON DUPLICATE KEY UPDATE risk_level = VALUES(risk_level), fetched_at = VALUES(fetched_at)");
            await conn.ExecuteAsync(sb.ToString(), parameters);
        }
    }

    public async Task<IReadOnlyDictionary<string, BrokerRiskLevel>> GetAllAsync(CancellationToken ct = default)
    {
        using var conn = CreateConnection();
        var rows = await conn.QueryAsync<Row>("SELECT isin AS Isin, risk_level AS RiskLevel FROM instrument_risk_levels");
        return rows.ToDictionary(r => r.Isin, r => (BrokerRiskLevel)r.RiskLevel, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<DateTime?> GetLastFetchedAtAsync(CancellationToken ct = default)
    {
        using var conn = CreateConnection();
        return await conn.ExecuteScalarAsync<DateTime?>("SELECT MAX(fetched_at) FROM instrument_risk_levels");
    }

    private sealed class Row
    {
        public string Isin { get; set; } = string.Empty;
        public int RiskLevel { get; set; }
    }
}

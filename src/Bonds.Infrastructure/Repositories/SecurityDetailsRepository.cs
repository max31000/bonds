using System.Data;
using System.Text;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Dapper;
using MySqlConnector;

namespace Bonds.Infrastructure.Repositories;

/// <summary>
/// Задача 43: персистентный кэш карточек бумаг MOEX (<c>security_details</c>). Образец —
/// <see cref="InstrumentRepository"/>/<see cref="BondUniverseRepository"/>: батчевый multi-row upsert
/// чанками, значения только через DynamicParameters.
/// </summary>
public class SecurityDetailsRepository : ISecurityDetailsRepository
{
    private readonly string _connectionString;

    /// <summary>Строк на один INSERT-батч (10 параметров на строку — с большим запасом под лимиты MySQL).</summary>
    private const int BatchSize = 200;

    public SecurityDetailsRepository(string connectionString) => _connectionString = connectionString;

    private IDbConnection CreateConnection() => new MySqlConnection(_connectionString);

    private const string SelectColumns = @"
        secid AS Secid, isin AS Isin, emitent_id AS EmitentId, has_default AS HasDefault,
        has_technical_default AS HasTechnicalDefault, is_qualified_investors AS IsQualifiedInvestors,
        coupon_benchmark AS CouponBenchmark, coupon_benchmark_spread_fraction AS CouponBenchmarkSpreadFraction,
        early_repayment AS EarlyRepayment, fetched_at AS FetchedAt";

    public async Task UpsertBatchAsync(IReadOnlyList<SecurityDetails> items, CancellationToken ct = default)
    {
        if (items.Count == 0) return;

        // Дубликаты SECID внутри одного батча схлопываем (последний выигрывает) — иначе multi-row
        // INSERT ... ON DUPLICATE KEY UPDATE применил бы их по порядку, что тоже корректно, но лишний шум.
        var unique = items
            .GroupBy(i => i.Secid, StringComparer.Ordinal)
            .Select(g => g.Last())
            .ToList();

        using var conn = CreateConnection();
        conn.Open();

        for (var offset = 0; offset < unique.Count; offset += BatchSize)
        {
            var chunk = unique.Skip(offset).Take(BatchSize).ToList();
            var sb = new StringBuilder(@"
                INSERT INTO security_details
                    (secid, isin, emitent_id, has_default, has_technical_default, is_qualified_investors,
                     coupon_benchmark, coupon_benchmark_spread_fraction, early_repayment, fetched_at)
                VALUES ");
            var parameters = new DynamicParameters();
            for (var i = 0; i < chunk.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append($@"
                    (@Secid{i}, @Isin{i}, @EmitentId{i}, @HasDefault{i}, @HasTechnicalDefault{i}, @IsQualifiedInvestors{i},
                     @CouponBenchmark{i}, @CouponBenchmarkSpreadFraction{i}, @EarlyRepayment{i}, @FetchedAt{i})");

                var d = chunk[i];
                parameters.Add($"Secid{i}", d.Secid);
                parameters.Add($"Isin{i}", d.Isin);
                parameters.Add($"EmitentId{i}", d.EmitentId);
                parameters.Add($"HasDefault{i}", d.HasDefault);
                parameters.Add($"HasTechnicalDefault{i}", d.HasTechnicalDefault);
                parameters.Add($"IsQualifiedInvestors{i}", d.IsQualifiedInvestors);
                parameters.Add($"CouponBenchmark{i}", d.CouponBenchmark);
                parameters.Add($"CouponBenchmarkSpreadFraction{i}", d.CouponBenchmarkSpreadFraction);
                parameters.Add($"EarlyRepayment{i}", d.EarlyRepayment);
                parameters.Add($"FetchedAt{i}", d.FetchedAt);
            }

            sb.Append(@"
                ON DUPLICATE KEY UPDATE
                    isin = VALUES(isin), emitent_id = VALUES(emitent_id), has_default = VALUES(has_default),
                    has_technical_default = VALUES(has_technical_default),
                    is_qualified_investors = VALUES(is_qualified_investors),
                    coupon_benchmark = VALUES(coupon_benchmark),
                    coupon_benchmark_spread_fraction = VALUES(coupon_benchmark_spread_fraction),
                    early_repayment = VALUES(early_repayment), fetched_at = VALUES(fetched_at)");

            await conn.ExecuteAsync(sb.ToString(), parameters);
        }
    }

    public async Task<IReadOnlyList<SecurityDetails>> GetBySecidsAsync(
        IReadOnlyCollection<string> secids, CancellationToken ct = default)
    {
        if (secids.Count == 0) return [];

        using var conn = CreateConnection();
        var rows = await conn.QueryAsync<SecurityDetails>(
            $"SELECT {SelectColumns} FROM security_details WHERE secid IN @Secids",
            new { Secids = secids.ToArray() });
        return rows.ToList();
    }

    public async Task<IReadOnlyList<string>> GetEnrichmentQueueAsync(
        int limit, DateTime staleBeforeUtc, CancellationToken ct = default)
    {
        if (limit <= 0) return [];

        using var conn = CreateConnection();

        // Кандидаты = бумаги портфеля/watchlist (prio 0) UNION ALL весь банк (prio 1); к каждому
        // присоединена его карточка. Нужна карточка, если её нет ИЛИ она старше порога. Итоговый
        // приоритет: портфель/watchlist (0) → банк без карточки (1) → протухшие (2). Бумага, которая
        // одновременно в портфеле и в банке, схлопывается GROUP BY secid (MIN(prio) = 0).
        // Портфель: позиции с quantity > 0 (через instruments.secid). Watchlist: watchlist_items.isin,
        // резолв в SECID через instruments.isin либо bond_universe.isin.
        const string sql = @"
            SELECT x.secid
            FROM (
                SELECT i.secid AS secid, 0 AS prio
                  FROM instruments i
                  JOIN positions p ON p.instrument_id = i.id AND p.quantity > 0
                 WHERE i.secid IS NOT NULL
                UNION ALL
                SELECT i.secid, 0
                  FROM instruments i
                  JOIN watchlist_items w ON w.isin = i.isin
                 WHERE i.secid IS NOT NULL
                UNION ALL
                SELECT bu.secid, 0
                  FROM bond_universe bu
                  JOIN watchlist_items w ON w.isin = bu.isin
                UNION ALL
                SELECT bu.secid, 1 FROM bond_universe bu
            ) x
            LEFT JOIN security_details sd ON sd.secid = x.secid
            WHERE sd.secid IS NULL OR sd.fetched_at < @StaleBefore
            GROUP BY x.secid
            ORDER BY CASE WHEN MIN(x.prio) = 0 THEN 0 WHEN MAX(sd.secid) IS NULL THEN 1 ELSE 2 END,
                     MAX(sd.fetched_at) ASC, x.secid ASC
            LIMIT @Limit";

        var rows = await conn.QueryAsync<string>(sql, new { StaleBefore = staleBeforeUtc, Limit = limit });
        return rows.ToList();
    }

    public async Task<IReadOnlyDictionary<int, EmitentDefaultHistory>> GetEmitentDefaultHistoryAsync(
        IReadOnlyCollection<int> emitentIds, CancellationToken ct = default)
    {
        if (emitentIds.Count == 0) return new Dictionary<int, EmitentDefaultHistory>();

        using var conn = CreateConnection();
        var rows = await conn.QueryAsync<EmitentDefaultHistoryRow>(@"
            SELECT emitent_id AS EmitentId,
                   CAST(MAX(has_default) AS SIGNED) AS HasDefault,
                   CAST(MAX(has_technical_default) AS SIGNED) AS HasTechnicalDefault
              FROM security_details
             WHERE emitent_id IN @Ids
             GROUP BY emitent_id",
            new { Ids = emitentIds.ToArray() });

        // MAX по TINYINT(1) игнорирует NULL: все NULL -> NULL (неизвестно), иначе 0/1.
        return rows.ToDictionary(
            r => r.EmitentId,
            r => new EmitentDefaultHistory(r.EmitentId, r.HasDefault is null ? null : r.HasDefault != 0,
                r.HasTechnicalDefault is null ? null : r.HasTechnicalDefault != 0));
    }

    private sealed class EmitentDefaultHistoryRow
    {
        public int EmitentId { get; set; }
        public int? HasDefault { get; set; }
        public int? HasTechnicalDefault { get; set; }
    }
}

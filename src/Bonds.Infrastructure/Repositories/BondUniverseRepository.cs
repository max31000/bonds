using System.Data;
using System.Text;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Dapper;
using MySqlConnector;

namespace Bonds.Infrastructure.Repositories;

/// <summary>
/// Задача 26 часть B.3 — снимок вселенной облигаций MOEX + дневная история. Батчевый upsert
/// собирает ОДНУ multi-row INSERT ... ON DUPLICATE KEY UPDATE команду на весь снимок (не N
/// отдельных INSERT) — иначе ~3000 round-trip'ов к MySQL превращают refresh из секунд в десятки
/// секунд/минуты (тот же принцип, что явно требует plan/26 часть B.3). Разбито на суб-батчи по
/// <see cref="BatchSize"/> строк — MySQL по умолчанию ограничивает размер одного пакета
/// (max_allowed_packet) и число параметров в одном запросе, полный снимок в одну команду рискует
/// упереться в этот лимит на инсталляциях с дефолтными настройками.
/// </summary>
public class BondUniverseRepository : IBondUniverseRepository
{
    private readonly string _connectionString;

    /// <summary>Строк на один INSERT-батч — 500 строк × ~19 колонок ≈ 9500 параметров, с запасом
    /// ниже дефолтного лимита MySQL max_prepared_stmt_count/packet, но крупными пачками, чтобы
    /// снимок ~3000 строк укладывался в считанные round-trip'ы.</summary>
    private const int BatchSize = 500;

    public BondUniverseRepository(string connectionString) => _connectionString = connectionString;

    private IDbConnection CreateConnection() => new MySqlConnection(_connectionString);

    private const string SelectColumns = @"
        id AS Id, secid AS Secid, isin AS Isin, short_name AS ShortName, sec_name AS SecName,
        face_value AS FaceValue, lot_value AS LotValue, coupon_percent AS CouponPercent,
        maturity_date AS MaturityDate, offer_date AS OfferDate, list_level AS ListLevel,
        sector AS Sector, yield_fraction AS YieldFraction, duration_years AS DurationYears,
        price_percent AS PricePercent, turnover_rub AS TurnoverRub, bid_percent AS BidPercent,
        offer_percent AS OfferPercent, num_trades AS NumTrades,
        gspread_approx_fraction AS GspreadApproxFraction, is_floater AS IsFloater,
        updated_at AS UpdatedAt";

    public async Task<IReadOnlyList<BondUniverseEntry>> GetAllAsync(CancellationToken ct = default)
    {
        using var conn = CreateConnection();
        var rows = await conn.QueryAsync<BondUniverseEntry>($"SELECT {SelectColumns} FROM bond_universe");
        return rows.ToList();
    }

    public async Task<DateTime?> GetLastRefreshUtcAsync(CancellationToken ct = default)
    {
        using var conn = CreateConnection();
        return await conn.ExecuteScalarAsync<DateTime?>("SELECT MAX(updated_at) FROM bond_universe");
    }

    public async Task<int> CountAsync(CancellationToken ct = default)
    {
        using var conn = CreateConnection();
        return await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM bond_universe");
    }

    public async Task UpsertSnapshotBatchAsync(IReadOnlyList<BondUniverseEntry> entries, CancellationToken ct = default)
    {
        if (entries.Count == 0) return;

        using var conn = CreateConnection();
        conn.Open();

        foreach (var chunk in Chunk(entries, BatchSize))
        {
            var sql = BuildUpsertSql(chunk.Count);
            var parameters = new DynamicParameters();
            for (var i = 0; i < chunk.Count; i++)
            {
                var e = chunk[i];
                parameters.Add($"Secid{i}", e.Secid);
                parameters.Add($"Isin{i}", e.Isin);
                parameters.Add($"ShortName{i}", e.ShortName);
                parameters.Add($"SecName{i}", e.SecName);
                parameters.Add($"FaceValue{i}", e.FaceValue);
                parameters.Add($"LotValue{i}", e.LotValue);
                parameters.Add($"CouponPercent{i}", e.CouponPercent);
                parameters.Add($"MaturityDate{i}", e.MaturityDate?.ToDateTime(TimeOnly.MinValue), DbType.Date);
                parameters.Add($"OfferDate{i}", e.OfferDate?.ToDateTime(TimeOnly.MinValue), DbType.Date);
                parameters.Add($"ListLevel{i}", e.ListLevel);
                parameters.Add($"Sector{i}", e.Sector);
                parameters.Add($"YieldFraction{i}", e.YieldFraction);
                parameters.Add($"DurationYears{i}", e.DurationYears);
                parameters.Add($"PricePercent{i}", e.PricePercent);
                parameters.Add($"TurnoverRub{i}", e.TurnoverRub);
                parameters.Add($"BidPercent{i}", e.BidPercent);
                parameters.Add($"OfferPercent{i}", e.OfferPercent);
                parameters.Add($"NumTrades{i}", e.NumTrades);
                parameters.Add($"GspreadApproxFraction{i}", e.GspreadApproxFraction);
                parameters.Add($"IsFloater{i}", e.IsFloater);
            }

            await conn.ExecuteAsync(sql, parameters);
        }
    }

    private static string BuildUpsertSql(int rowCount)
    {
        var sb = new StringBuilder();
        sb.Append(@"
            INSERT INTO bond_universe
                (secid, isin, short_name, sec_name, face_value, lot_value, coupon_percent,
                 maturity_date, offer_date, list_level, sector, yield_fraction, duration_years,
                 price_percent, turnover_rub, bid_percent, offer_percent, num_trades,
                 gspread_approx_fraction, is_floater)
            VALUES ");

        for (var i = 0; i < rowCount; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append($@"
                (@Secid{i}, @Isin{i}, @ShortName{i}, @SecName{i}, @FaceValue{i}, @LotValue{i}, @CouponPercent{i},
                 @MaturityDate{i}, @OfferDate{i}, @ListLevel{i}, @Sector{i}, @YieldFraction{i}, @DurationYears{i},
                 @PricePercent{i}, @TurnoverRub{i}, @BidPercent{i}, @OfferPercent{i}, @NumTrades{i},
                 @GspreadApproxFraction{i}, @IsFloater{i})");
        }

        sb.Append(@"
            ON DUPLICATE KEY UPDATE
                isin = VALUES(isin), short_name = VALUES(short_name), sec_name = VALUES(sec_name),
                face_value = VALUES(face_value), lot_value = VALUES(lot_value),
                coupon_percent = VALUES(coupon_percent), maturity_date = VALUES(maturity_date),
                offer_date = VALUES(offer_date), list_level = VALUES(list_level), sector = VALUES(sector),
                yield_fraction = VALUES(yield_fraction), duration_years = VALUES(duration_years),
                price_percent = VALUES(price_percent), turnover_rub = VALUES(turnover_rub),
                bid_percent = VALUES(bid_percent), offer_percent = VALUES(offer_percent),
                num_trades = VALUES(num_trades), gspread_approx_fraction = VALUES(gspread_approx_fraction),
                is_floater = VALUES(is_floater)");

        return sb.ToString();
    }

    private static List<List<T>> Chunk<T>(IReadOnlyList<T> source, int size)
    {
        var chunks = new List<List<T>>();
        for (var i = 0; i < source.Count; i += size)
        {
            chunks.Add(source.Skip(i).Take(size).ToList());
        }
        return chunks;
    }

    public async Task<bool> HasHistoryForDateAsync(DateOnly date, CancellationToken ct = default)
    {
        using var conn = CreateConnection();
        var count = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM bond_universe_history WHERE snapshot_date = @Date",
            new { Date = date });
        return count > 0;
    }

    public async Task AppendDailyHistorySnapshotAsync(DateOnly date, int retentionDays, CancellationToken ct = default)
    {
        using var conn = CreateConnection();
        conn.Open();
        using var tx = conn.BeginTransaction();

        try
        {
            // INSERT ... SELECT одним запросом из текущего снимка — не построчно из C# (план часть B.3).
            const string insertSql = @"
                INSERT INTO bond_universe_history
                    (snapshot_date, secid, yield_fraction, duration_years, gspread_approx_fraction,
                     turnover_rub, price_percent)
                SELECT @Date, secid, yield_fraction, duration_years, gspread_approx_fraction,
                       turnover_rub, price_percent
                FROM bond_universe
                ON DUPLICATE KEY UPDATE
                    yield_fraction = VALUES(yield_fraction), duration_years = VALUES(duration_years),
                    gspread_approx_fraction = VALUES(gspread_approx_fraction),
                    turnover_rub = VALUES(turnover_rub), price_percent = VALUES(price_percent)";

            await conn.ExecuteAsync(insertSql, new { Date = date }, tx);

            var cutoff = date.AddDays(-retentionDays);
            await conn.ExecuteAsync(
                "DELETE FROM bond_universe_history WHERE snapshot_date < @Cutoff",
                new { Cutoff = cutoff }, tx);

            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public async Task<int> GetHistoryDaysCountAsync(CancellationToken ct = default)
    {
        using var conn = CreateConnection();
        return await conn.ExecuteScalarAsync<int>("SELECT COUNT(DISTINCT snapshot_date) FROM bond_universe_history");
    }

    public async Task<IReadOnlyList<BondUniverseHistoryPoint>> GetRecentHistoryAsync(int tradingDaysBack, CancellationToken ct = default)
    {
        using var conn = CreateConnection();

        // Топ-N САМЫХ ПОСЛЕДНИХ отличных дат снимка (не календарных дней — выходные/праздники не
        // пишут историю, см. BondUniverseRefreshService.MaybeWriteHistorySnapshotAsync), затем все
        // строки за эти даты одним запросом (план часть B.1 — "медиана дневных медиан").
        const string sql = @"
            SELECT snapshot_date AS SnapshotDate, secid AS Secid, yield_fraction AS YieldFraction,
                   duration_years AS DurationYears, gspread_approx_fraction AS GspreadApproxFraction,
                   turnover_rub AS TurnoverRub, price_percent AS PricePercent
            FROM bond_universe_history
            WHERE snapshot_date IN (
                SELECT snapshot_date FROM (
                    SELECT DISTINCT snapshot_date FROM bond_universe_history
                    ORDER BY snapshot_date DESC
                    LIMIT @TradingDaysBack
                ) AS recent_dates
            )";

        var rows = await conn.QueryAsync<BondUniverseHistoryPoint>(sql, new { TradingDaysBack = tradingDaysBack });
        return rows.ToList();
    }
}

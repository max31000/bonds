using System.Data;
using System.Text;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Dapper;
using MySqlConnector;

namespace Bonds.Infrastructure.Repositories;

/// <summary>
/// Снимок вселенной облигаций MOEX + дневная история. Батчевый upsert
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

    /// <summary>Строк на один INSERT-батч — 500 строк × ~27 колонок ≈ 13500 параметров, с запасом
    /// ниже дефолтного лимита MySQL max_prepared_stmt_count/packet, но крупными пачками, чтобы
    /// снимок ~3000 строк укладывался в считанные round-trip'ы.</summary>
    private const int BatchSize = 500;

    /// <summary>Строк на один UPDATE-JOIN эмитентов (4 параметра на строку).</summary>
    private const int EmitentBatchSize = 500;

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
        accrued_rub AS AccruedRub, issue_size AS IssueSize,
        effective_yield_fraction AS EffectiveYieldFraction, yield_to_offer_fraction AS YieldToOfferFraction,
        yield_date_type AS YieldDateType, gspread_official_fraction AS GspreadOfficialFraction,
        zspread_official_fraction AS ZspreadOfficialFraction,
        emitent_id AS EmitentId, emitent_inn AS EmitentInn, emitent_title AS EmitentTitle,
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
                parameters.Add($"AccruedRub{i}", e.AccruedRub);
                parameters.Add($"IssueSize{i}", e.IssueSize);
                parameters.Add($"EffectiveYieldFraction{i}", e.EffectiveYieldFraction);
                parameters.Add($"YieldToOfferFraction{i}", e.YieldToOfferFraction);
                parameters.Add($"YieldDateType{i}", e.YieldDateType);
                parameters.Add($"GspreadOfficialFraction{i}", e.GspreadOfficialFraction);
                parameters.Add($"ZspreadOfficialFraction{i}", e.ZspreadOfficialFraction);
            }

            await conn.ExecuteAsync(sql, parameters);
        }
    }

    /// <summary>
    /// Задача 43: UPDATE эмитентов в существующих строках банка. Один запрос на чанк:
    /// <c>UPDATE bond_universe u JOIN (SELECT @s0 AS secid, ... UNION ALL ...) v ON u.secid = v.secid</c>.
    /// JOIN (а не INSERT ... ON DUPLICATE KEY) — иначе в банк попали бы ~7 тыс. бумаг bulk-справочника,
    /// которых там нет (погашенные/нерублёвые). Значения — только через DynamicParameters.
    /// </summary>
    public async Task<int> UpdateEmitentsAsync(IReadOnlyList<UniverseEmitent> emitents, CancellationToken ct = default)
    {
        if (emitents.Count == 0) return 0;

        using var conn = CreateConnection();
        conn.Open();

        var updated = 0;
        foreach (var chunk in Chunk(emitents, EmitentBatchSize))
        {
            var sb = new StringBuilder("UPDATE bond_universe u JOIN (");
            var parameters = new DynamicParameters();
            for (var i = 0; i < chunk.Count; i++)
            {
                if (i > 0) sb.Append(" UNION ALL ");
                sb.Append($"SELECT @Secid{i} AS secid, @EmitentId{i} AS emitent_id, @EmitentInn{i} AS emitent_inn, @EmitentTitle{i} AS emitent_title");
                var e = chunk[i];
                parameters.Add($"Secid{i}", e.Secid);
                parameters.Add($"EmitentId{i}", e.EmitentId);
                parameters.Add($"EmitentInn{i}", e.EmitentInn);
                parameters.Add($"EmitentTitle{i}", e.EmitentTitle);
            }

            sb.Append(@") v ON u.secid = v.secid
                SET u.emitent_id = v.emitent_id, u.emitent_inn = v.emitent_inn, u.emitent_title = v.emitent_title");

            updated += await conn.ExecuteAsync(sb.ToString(), parameters);
        }

        return updated;
    }

    public async Task<int> CountWithEmitentAsync(CancellationToken ct = default)
    {
        using var conn = CreateConnection();
        return await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM bond_universe WHERE emitent_id IS NOT NULL OR emitent_inn IS NOT NULL");
    }

    private static string BuildUpsertSql(int rowCount)
    {
        var sb = new StringBuilder();
        sb.Append(@"
            INSERT INTO bond_universe
                (secid, isin, short_name, sec_name, face_value, lot_value, coupon_percent,
                 maturity_date, offer_date, list_level, sector, yield_fraction, duration_years,
                 price_percent, turnover_rub, bid_percent, offer_percent, num_trades,
                 gspread_approx_fraction, is_floater, accrued_rub, issue_size, effective_yield_fraction,
                 yield_to_offer_fraction, yield_date_type, gspread_official_fraction, zspread_official_fraction)
            VALUES ");

        for (var i = 0; i < rowCount; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append($@"
                (@Secid{i}, @Isin{i}, @ShortName{i}, @SecName{i}, @FaceValue{i}, @LotValue{i}, @CouponPercent{i},
                 @MaturityDate{i}, @OfferDate{i}, @ListLevel{i}, @Sector{i}, @YieldFraction{i}, @DurationYears{i},
                 @PricePercent{i}, @TurnoverRub{i}, @BidPercent{i}, @OfferPercent{i}, @NumTrades{i},
                 @GspreadApproxFraction{i}, @IsFloater{i}, @AccruedRub{i}, @IssueSize{i}, @EffectiveYieldFraction{i},
                 @YieldToOfferFraction{i}, @YieldDateType{i}, @GspreadOfficialFraction{i}, @ZspreadOfficialFraction{i})");
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
                is_floater = VALUES(is_floater), accrued_rub = VALUES(accrued_rub), issue_size = VALUES(issue_size),
                effective_yield_fraction = VALUES(effective_yield_fraction),
                yield_to_offer_fraction = VALUES(yield_to_offer_fraction), yield_date_type = VALUES(yield_date_type),
                gspread_official_fraction = VALUES(gspread_official_fraction),
                zspread_official_fraction = VALUES(zspread_official_fraction)");

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
            // Задача 42: bid/offer (% от номинала, как bid_percent/offer_percent), list_level и
            // gspread_effective_fraction = COALESCE(биржевой, приближённый) — тот же приоритет, что
            // BondUniverseEntry.EffectiveGspreadFraction (ДОЛЯ, конверсий нет — обе колонки уже доли).
            const string insertSql = @"
                INSERT INTO bond_universe_history
                    (snapshot_date, secid, yield_fraction, duration_years, gspread_approx_fraction,
                     turnover_rub, price_percent, bid, offer, list_level, gspread_effective_fraction)
                SELECT @Date, secid, yield_fraction, duration_years, gspread_approx_fraction,
                       turnover_rub, price_percent, bid_percent, offer_percent, list_level,
                       COALESCE(gspread_official_fraction, gspread_approx_fraction)
                FROM bond_universe
                ON DUPLICATE KEY UPDATE
                    yield_fraction = VALUES(yield_fraction), duration_years = VALUES(duration_years),
                    gspread_approx_fraction = VALUES(gspread_approx_fraction),
                    turnover_rub = VALUES(turnover_rub), price_percent = VALUES(price_percent),
                    bid = VALUES(bid), offer = VALUES(offer), list_level = VALUES(list_level),
                    gspread_effective_fraction = VALUES(gspread_effective_fraction)";

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
                   turnover_rub AS TurnoverRub, price_percent AS PricePercent,
                   bid AS Bid, offer AS Offer, list_level AS ListLevel,
                   gspread_effective_fraction AS GspreadEffectiveFraction
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

    public async Task<IReadOnlyList<BondUniverseEntry>> GetByIsinsAsync(IReadOnlyCollection<string> isins, CancellationToken ct = default)
    {
        if (isins.Count == 0) return [];

        using var conn = CreateConnection();
        var rows = new List<BondUniverseEntry>();
        foreach (var chunk in isins.Distinct(StringComparer.OrdinalIgnoreCase).Chunk(1000))
        {
            rows.AddRange(await conn.QueryAsync<BondUniverseEntry>(
                $"SELECT {SelectColumns} FROM bond_universe WHERE isin IN @Isins", new { Isins = chunk }));
        }

        return rows;
    }

    public async Task<IReadOnlyList<BondUniverseHistoryPoint>> GetRecentHistoryForSecidsAsync(
        IReadOnlyCollection<string> secids, int tradingDaysBack, CancellationToken ct = default)
    {
        if (secids.Count == 0 || tradingDaysBack <= 0) return [];

        using var conn = CreateConnection();

        // Даты — топ-N отличных snapshot_date по ВСЕЙ таблице (общий календарь торговых дней, не зависит от набора
        // бумаг), строки — только запрошенных secid. Один запрос на чанк secid.
        const string sql = @"
            SELECT snapshot_date AS SnapshotDate, secid AS Secid, yield_fraction AS YieldFraction,
                   duration_years AS DurationYears, gspread_approx_fraction AS GspreadApproxFraction,
                   turnover_rub AS TurnoverRub, price_percent AS PricePercent,
                   bid AS Bid, offer AS Offer, list_level AS ListLevel,
                   gspread_effective_fraction AS GspreadEffectiveFraction
            FROM bond_universe_history
            WHERE secid IN @Secids
              AND snapshot_date IN (
                SELECT snapshot_date FROM (
                    SELECT DISTINCT snapshot_date FROM bond_universe_history
                    ORDER BY snapshot_date DESC
                    LIMIT @TradingDaysBack
                ) AS recent_dates
            )";

        var rows = new List<BondUniverseHistoryPoint>();
        foreach (var chunk in secids.Distinct(StringComparer.OrdinalIgnoreCase).Chunk(1000))
        {
            rows.AddRange(await conn.QueryAsync<BondUniverseHistoryPoint>(sql, new { Secids = chunk, TradingDaysBack = tradingDaysBack }));
        }

        return rows;
    }
}

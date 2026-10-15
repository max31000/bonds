using System.Data;
using System.Text;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Dapper;
using MySqlConnector;

namespace Bonds.Infrastructure.Repositories;

/// <summary>
/// Задача 45: отчётность эмитентов (<c>issuer_financials</c>) и статусы проверки ГИР БО
/// (<c>issuer_financials_status</c>). Образец — <see cref="SecurityDetailsRepository"/>: батчевый multi-row
/// upsert, значения только через DynamicParameters. Единицы: все денежные колонки — РУБЛИ (см.
/// <see cref="IssuerFinancials"/>), время — UTC.
/// </summary>
public class IssuerFinancialsRepository : IIssuerFinancialsRepository
{
    private readonly string _connectionString;

    /// <summary>Строк на один INSERT-батч (16 параметров на строку — с большим запасом под лимиты MySQL).</summary>
    private const int BatchSize = 100;

    public IssuerFinancialsRepository(string connectionString) => _connectionString = connectionString;

    private IDbConnection CreateConnection() => new MySqlConnection(_connectionString);

    private const string SelectColumns = @"
        inn AS Inn, period AS Period, source AS Source, fetched_at AS FetchedAt,
        revenue AS Revenue, operating_profit AS OperatingProfit, interest_paid AS InterestPaid,
        net_profit AS NetProfit, equity AS Equity, total_assets AS TotalAssets,
        current_assets AS CurrentAssets, current_liabilities AS CurrentLiabilities, cash AS Cash,
        long_debt AS LongDebt, short_debt AS ShortDebt, retained_earnings AS RetainedEarnings,
        has_audit AS HasAudit";

    public async Task UpsertAsync(IReadOnlyList<IssuerFinancials> items, CancellationToken ct = default)
    {
        if (items.Count == 0) return;

        // Дубликаты ключа внутри батча схлопываем (последний выигрывает).
        var unique = items
            .GroupBy(i => (i.Inn, i.Period, i.Source))
            .Select(g => g.Last())
            .ToList();

        using var conn = CreateConnection();
        conn.Open();

        for (var offset = 0; offset < unique.Count; offset += BatchSize)
        {
            var chunk = unique.Skip(offset).Take(BatchSize).ToList();
            var sb = new StringBuilder(@"
                INSERT INTO issuer_financials
                    (inn, period, source, fetched_at, revenue, operating_profit, interest_paid, net_profit, equity,
                     total_assets, current_assets, current_liabilities, cash, long_debt, short_debt,
                     retained_earnings, has_audit)
                VALUES ");
            var parameters = new DynamicParameters();
            for (var i = 0; i < chunk.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append($@"
                    (@Inn{i}, @Period{i}, @Source{i}, @FetchedAt{i}, @Revenue{i}, @OperatingProfit{i}, @InterestPaid{i},
                     @NetProfit{i}, @Equity{i}, @TotalAssets{i}, @CurrentAssets{i}, @CurrentLiabilities{i}, @Cash{i},
                     @LongDebt{i}, @ShortDebt{i}, @RetainedEarnings{i}, @HasAudit{i})");

                var f = chunk[i];
                parameters.Add($"Inn{i}", f.Inn);
                parameters.Add($"Period{i}", f.Period);
                parameters.Add($"Source{i}", f.Source);
                parameters.Add($"FetchedAt{i}", f.FetchedAt);
                parameters.Add($"Revenue{i}", f.Revenue);
                parameters.Add($"OperatingProfit{i}", f.OperatingProfit);
                parameters.Add($"InterestPaid{i}", f.InterestPaid);
                parameters.Add($"NetProfit{i}", f.NetProfit);
                parameters.Add($"Equity{i}", f.Equity);
                parameters.Add($"TotalAssets{i}", f.TotalAssets);
                parameters.Add($"CurrentAssets{i}", f.CurrentAssets);
                parameters.Add($"CurrentLiabilities{i}", f.CurrentLiabilities);
                parameters.Add($"Cash{i}", f.Cash);
                parameters.Add($"LongDebt{i}", f.LongDebt);
                parameters.Add($"ShortDebt{i}", f.ShortDebt);
                parameters.Add($"RetainedEarnings{i}", f.RetainedEarnings);
                parameters.Add($"HasAudit{i}", f.HasAudit);
            }

            sb.Append(@"
                ON DUPLICATE KEY UPDATE
                    fetched_at = VALUES(fetched_at), revenue = VALUES(revenue),
                    operating_profit = VALUES(operating_profit), interest_paid = VALUES(interest_paid),
                    net_profit = VALUES(net_profit), equity = VALUES(equity), total_assets = VALUES(total_assets),
                    current_assets = VALUES(current_assets), current_liabilities = VALUES(current_liabilities),
                    cash = VALUES(cash), long_debt = VALUES(long_debt), short_debt = VALUES(short_debt),
                    retained_earnings = VALUES(retained_earnings), has_audit = VALUES(has_audit)");

            await conn.ExecuteAsync(sb.ToString(), parameters);
        }
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<IssuerFinancials>>> GetLatestByInnsAsync(
        IReadOnlyCollection<string> inns, int yearsPerInn = 3, CancellationToken ct = default)
    {
        if (inns.Count == 0 || yearsPerInn <= 0) return new Dictionary<string, IReadOnlyList<IssuerFinancials>>();

        using var conn = CreateConnection();

        // Один запрос на все ИНН. Шаг 1 (picked): на каждый (inn, period) остаётся одна запись — 'manual'
        // приоритетнее 'girbo'. Шаг 2 (ranked): нумерация лет внутри ИНН по убыванию. Шаг 3: первые N лет.
        var sql = $@"
            WITH picked AS (
                SELECT {SelectColumns},
                       ROW_NUMBER() OVER (PARTITION BY inn, period
                                          ORDER BY CASE source WHEN '{IssuerFinancialsSources.Manual}' THEN 0 ELSE 1 END) AS src_rn
                  FROM issuer_financials
                 WHERE inn IN @Inns
            ), ranked AS (
                SELECT picked.*, ROW_NUMBER() OVER (PARTITION BY Inn ORDER BY Period DESC) AS period_rn
                  FROM picked
                 WHERE src_rn = 1
            )
            SELECT Inn, Period, Source, FetchedAt, Revenue, OperatingProfit, InterestPaid, NetProfit, Equity,
                   TotalAssets, CurrentAssets, CurrentLiabilities, Cash, LongDebt, ShortDebt, RetainedEarnings, HasAudit
              FROM ranked
             WHERE period_rn <= @PerInn
             ORDER BY Inn, Period DESC";

        var rows = await conn.QueryAsync<IssuerFinancials>(sql, new { Inns = inns.ToArray(), PerInn = yearsPerInn });
        return rows
            .GroupBy(r => r.Inn)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<IssuerFinancials>)g.ToList());
    }

    public async Task UpsertStatusAsync(IssuerFinancialsStatus status, CancellationToken ct = default)
    {
        using var conn = CreateConnection();
        await conn.ExecuteAsync(@"
            INSERT INTO issuer_financials_status (inn, status, checked_at)
            VALUES (@Inn, @Status, @CheckedAt)
            ON DUPLICATE KEY UPDATE status = VALUES(status), checked_at = VALUES(checked_at)",
            new { status.Inn, status.Status, status.CheckedAt });
    }

    public async Task<IReadOnlyDictionary<string, IssuerFinancialsStatus>> GetStatusesAsync(
        IReadOnlyCollection<string> inns, CancellationToken ct = default)
    {
        if (inns.Count == 0) return new Dictionary<string, IssuerFinancialsStatus>();

        using var conn = CreateConnection();
        var rows = await conn.QueryAsync<StatusRow>(
            "SELECT inn AS Inn, status AS Status, checked_at AS CheckedAt FROM issuer_financials_status WHERE inn IN @Inns",
            new { Inns = inns.ToArray() });
        return rows.ToDictionary(r => r.Inn, r => new IssuerFinancialsStatus(r.Inn, r.Status, r.CheckedAt));
    }

    public async Task<IReadOnlyList<string>> GetRefreshQueueAsync(
        int limit, DateTime staleBeforeUtc, DateTime errorRetryBeforeUtc, CancellationToken ct = default)
    {
        if (limit <= 0) return [];

        using var conn = CreateConnection();

        // ИНН эмитентов портфеля (позиции с quantity > 0) и watchlist: ISIN → bond_universe.emitent_inn
        // (заполняется суточным UniverseEmitentEnricher). Ограничение формата — ИНН юрлица (10) или ИП (12)
        // цифр, мусор в справочнике не уходит во внешний сервис. Нужна проверка, если статуса нет, либо
        // он 'error' и старше errorRetryBefore, либо иной статус старше staleBefore.
        var sql = $@"
            SELECT c.inn
              FROM (
                    SELECT bu.emitent_inn AS inn
                      FROM instruments i
                      JOIN positions p ON p.instrument_id = i.id AND p.quantity > 0
                      JOIN bond_universe bu ON bu.isin = i.isin
                     WHERE bu.emitent_inn IS NOT NULL
                    UNION
                    SELECT bu.emitent_inn
                      FROM watchlist_items w
                      JOIN bond_universe bu ON bu.isin = w.isin
                     WHERE bu.emitent_inn IS NOT NULL
                   ) c
              LEFT JOIN issuer_financials_status s ON s.inn = c.inn
             WHERE c.inn REGEXP '^([0-9]{{10}}|[0-9]{{12}})$'
               AND (s.inn IS NULL
                    OR (s.status = '{IssuerFinancialsStatuses.Error}' AND s.checked_at < @ErrorRetryBefore)
                    OR (s.status <> '{IssuerFinancialsStatuses.Error}' AND s.checked_at < @StaleBefore))
             ORDER BY (s.inn IS NULL) DESC, s.checked_at ASC, c.inn ASC
             LIMIT @Limit";

        var rows = await conn.QueryAsync<string>(sql, new
        {
            StaleBefore = staleBeforeUtc,
            ErrorRetryBefore = errorRetryBeforeUtc,
            Limit = limit,
        });
        return rows.ToList();
    }

    private sealed class StatusRow
    {
        public string Inn { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public DateTime CheckedAt { get; set; }
    }
}

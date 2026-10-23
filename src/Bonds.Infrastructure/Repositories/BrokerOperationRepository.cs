using System.Data;
using System.Text;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Dapper;
using MySqlConnector;

namespace Bonds.Infrastructure.Repositories;

/// <summary>
/// Журнал не-T-Invest счетов (<c>broker_operations</c>, задача 60). <c>amount</c> — в <c>currency</c> операции СО ЗНАКОМ
/// (плюс — деньги пришли). Идемпотентность — UNIQUE (account_id, external_id).
/// </summary>
public class BrokerOperationRepository : IBrokerOperationRepository
{
    private readonly string _connectionString;

    private const int BatchSize = 200;

    public BrokerOperationRepository(string connectionString) => _connectionString = connectionString;

    private IDbConnection CreateConnection() => new MySqlConnection(_connectionString);

    private const string SelectColumns = @"
        id AS Id, account_id AS AccountId, external_id AS ExternalId, op_date AS Date, type AS TypeName, ticker AS Ticker,
        quantity AS Quantity, price AS Price, amount AS Amount, currency AS Currency, raw_type AS RawType, comment AS Comment";

    public async Task<int> UpsertManyAsync(IReadOnlyCollection<BrokerOperation> operations)
    {
        if (operations.Count == 0) return 0;

        var all = operations.ToList();
        using var conn = CreateConnection();
        conn.Open();
        for (var offset = 0; offset < all.Count; offset += BatchSize)
        {
            var chunk = all.Skip(offset).Take(BatchSize).ToList();
            var sb = new StringBuilder(
                "INSERT INTO broker_operations (account_id, external_id, op_date, type, ticker, quantity, price, amount, currency, raw_type, comment) VALUES ");
            var parameters = new DynamicParameters();
            for (var i = 0; i < chunk.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append($"(@A{i}, @E{i}, @D{i}, @T{i}, @K{i}, @Q{i}, @P{i}, @M{i}, @C{i}, @R{i}, @N{i})");
                var op = chunk[i];
                parameters.Add($"A{i}", op.AccountId);
                parameters.Add($"E{i}", op.ExternalId);
                parameters.Add($"D{i}", op.Date);
                parameters.Add($"T{i}", op.Type.ToString()); // enum в параметры — явной строкой (см. DapperTypeHandlers)
                parameters.Add($"K{i}", op.Ticker);
                parameters.Add($"Q{i}", op.Quantity);
                parameters.Add($"P{i}", op.Price);
                parameters.Add($"M{i}", op.Amount);
                parameters.Add($"C{i}", op.Currency);
                parameters.Add($"R{i}", op.RawType);
                parameters.Add($"N{i}", op.Comment);
            }

            sb.Append(@" ON DUPLICATE KEY UPDATE op_date = VALUES(op_date), type = VALUES(type), ticker = VALUES(ticker),
                quantity = VALUES(quantity), price = VALUES(price), amount = VALUES(amount), currency = VALUES(currency),
                raw_type = VALUES(raw_type), comment = VALUES(comment)");
            await conn.ExecuteAsync(sb.ToString(), parameters);
        }

        return all.Count;
    }

    public async Task<IReadOnlyList<BrokerOperation>> GetByAccountAsync(ulong accountId, DateOnly? from, DateOnly? to)
    {
        using var conn = CreateConnection();
        var rows = await conn.QueryAsync<Row>(
            $@"SELECT {SelectColumns} FROM broker_operations
               WHERE account_id = @AccountId
                 AND (@From IS NULL OR op_date >= @From)
                 AND (@ToExclusive IS NULL OR op_date < @ToExclusive)
               ORDER BY op_date DESC, id DESC",
            new
            {
                AccountId = accountId,
                From = from?.ToDateTime(TimeOnly.MinValue),
                ToExclusive = to?.AddDays(1).ToDateTime(TimeOnly.MinValue),
            });
        return rows.Select(r => r.ToModel()).ToList();
    }

    public async Task<IReadOnlyList<BrokerOperation>> GetByTypeAsync(ulong accountId, BrokerOperationType type)
    {
        using var conn = CreateConnection();
        var rows = await conn.QueryAsync<Row>(
            $"SELECT {SelectColumns} FROM broker_operations WHERE account_id = @AccountId AND type = @Type ORDER BY op_date",
            new { AccountId = accountId, Type = type.ToString() });
        return rows.Select(r => r.ToModel()).ToList();
    }

    // type читается строкой и разбирается явно: неизвестное значение в БД не должно ронять весь запрос.
    private sealed class Row
    {
        public ulong Id { get; set; }
        public ulong AccountId { get; set; }
        public string ExternalId { get; set; } = string.Empty;
        public DateTime Date { get; set; }
        public string TypeName { get; set; } = string.Empty;
        public string? Ticker { get; set; }
        public decimal? Quantity { get; set; }
        public decimal? Price { get; set; }
        public decimal Amount { get; set; }
        public string Currency { get; set; } = string.Empty;
        public string RawType { get; set; } = string.Empty;
        public string? Comment { get; set; }

        public BrokerOperation ToModel() => new()
        {
            Id = Id, AccountId = AccountId, ExternalId = ExternalId, Date = Date,
            Type = Enum.TryParse<BrokerOperationType>(TypeName, out var t) ? t : BrokerOperationType.Other,
            Ticker = Ticker, Quantity = Quantity, Price = Price, Amount = Amount, Currency = Currency, RawType = RawType, Comment = Comment,
        };
    }
}

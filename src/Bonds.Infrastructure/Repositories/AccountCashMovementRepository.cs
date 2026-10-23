using System.Data;
using System.Text;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Dapper;
using MySqlConnector;

namespace Bonds.Infrastructure.Repositories;

/// <summary>
/// Пополнения и выводы счёта T-Invest (<c>account_cash_movements</c>, задача 61). <c>amount</c> — в <c>currency</c> СО ЗНАКОМ брокера,
/// <c>amount_rub</c> — РУБЛИ (null — курса не нашлось). Идемпотентность — UNIQUE (account_id, external_id).
/// </summary>
public class AccountCashMovementRepository : IAccountCashMovementRepository
{
    private readonly string _connectionString;

    private const int BatchSize = 200;

    public AccountCashMovementRepository(string connectionString) => _connectionString = connectionString;

    private IDbConnection CreateConnection() => new MySqlConnection(_connectionString);

    public async Task<int> UpsertManyAsync(IReadOnlyCollection<AccountCashMovement> movements)
    {
        if (movements.Count == 0) return 0;

        var all = movements.ToList();
        using var conn = CreateConnection();
        conn.Open();
        for (var offset = 0; offset < all.Count; offset += BatchSize)
        {
            var chunk = all.Skip(offset).Take(BatchSize).ToList();
            var sb = new StringBuilder("INSERT INTO account_cash_movements (account_id, external_id, op_date, type, amount_rub, currency, amount) VALUES ");
            var parameters = new DynamicParameters();
            for (var i = 0; i < chunk.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append($"(@A{i}, @E{i}, @D{i}, @T{i}, @R{i}, @C{i}, @M{i})");
                var m = chunk[i];
                parameters.Add($"A{i}", m.AccountId);
                parameters.Add($"E{i}", m.ExternalId);
                parameters.Add($"D{i}", m.Date);
                parameters.Add($"T{i}", m.Type.ToString()); // enum в параметры — явной строкой (см. DapperTypeHandlers)
                parameters.Add($"R{i}", m.AmountRub);
                parameters.Add($"C{i}", m.Currency);
                parameters.Add($"M{i}", m.Amount);
            }

            sb.Append(@" ON DUPLICATE KEY UPDATE op_date = VALUES(op_date), type = VALUES(type), amount_rub = VALUES(amount_rub),
                currency = VALUES(currency), amount = VALUES(amount)");
            await conn.ExecuteAsync(sb.ToString(), parameters);
        }

        return all.Count;
    }

    public async Task<IReadOnlyList<AccountCashMovement>> GetRecentAsync(ulong accountId, int limit)
    {
        using var conn = CreateConnection();
        var rows = await conn.QueryAsync<Row>(
            @"SELECT id AS Id, account_id AS AccountId, external_id AS ExternalId, op_date AS Date, type AS TypeName,
                     amount_rub AS AmountRub, currency AS Currency, amount AS Amount
              FROM account_cash_movements WHERE account_id = @AccountId
              ORDER BY op_date DESC, id DESC LIMIT @Limit",
            new { AccountId = accountId, Limit = Math.Max(1, limit) });
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
        public decimal? AmountRub { get; set; }
        public string Currency { get; set; } = string.Empty;
        public decimal Amount { get; set; }

        public AccountCashMovement ToModel() => new()
        {
            Id = Id, AccountId = AccountId, ExternalId = ExternalId, Date = Date,
            Type = Enum.TryParse<CashMovementType>(TypeName, out var t) ? t : (Amount < 0m ? CashMovementType.Withdrawal : CashMovementType.Deposit),
            AmountRub = AmountRub, Currency = Currency, Amount = Amount,
        };
    }
}

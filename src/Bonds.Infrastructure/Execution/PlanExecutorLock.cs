using Dapper;
using MySqlConnector;

namespace Bonds.Infrastructure.Execution;

/// <summary>
/// Взаимоисключение исполнителя между экземплярами сервиса (ревью задачи 65, MINOR 1): тик и смена торгового токена выполняются только под этой блокировкой.
/// null из <see cref="TryAcquireAsync"/> — занято (тик пропускается / смена токена отвечает 409).
/// </summary>
public interface IPlanExecutorLock
{
    Task<IAsyncDisposable?> TryAcquireAsync(TimeSpan wait, CancellationToken ct = default);
}

/// <summary>
/// MySQL <c>GET_LOCK('bonds-plan-executor', wait)</c> на ОТДЕЛЬНОМ соединении, которое держится всё время владения (блокировка MySQL живёт в сессии);
/// освобождение — <c>RELEASE_LOCK</c> и закрытие соединения (обрыв сессии тоже снимает блокировку). Singleton.
/// </summary>
public sealed class MySqlPlanExecutorLock : IPlanExecutorLock
{
    public const string LockName = "bonds-plan-executor";

    private readonly string _connectionString;

    public MySqlPlanExecutorLock(string connectionString) => _connectionString = connectionString;

    public async Task<IAsyncDisposable?> TryAcquireAsync(TimeSpan wait, CancellationToken ct = default)
    {
        var conn = new MySqlConnection(_connectionString);
        try
        {
            await conn.OpenAsync(ct);
            var got = await conn.ExecuteScalarAsync<long?>(
                new CommandDefinition("SELECT GET_LOCK(@Name, @Wait)", new { Name = LockName, Wait = (int)Math.Max(0, Math.Ceiling(wait.TotalSeconds)) }, cancellationToken: ct));
            if (got == 1) return new Handle(conn);
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }

        await conn.DisposeAsync();
        return null;
    }

    private sealed class Handle : IAsyncDisposable
    {
        private MySqlConnection? _conn;

        public Handle(MySqlConnection conn) => _conn = conn;

        public async ValueTask DisposeAsync()
        {
            var conn = Interlocked.Exchange(ref _conn, null);
            if (conn is null) return;
            try
            {
                await conn.ExecuteScalarAsync<long?>("SELECT RELEASE_LOCK(@Name)", new { Name = LockName });
            }
            catch
            {
                // Соединение не вернётся в пул с висящей блокировкой: ClearPool закрывает физические соединения, сессия MySQL снимает GET_LOCK сама.
                MySqlConnection.ClearPool(conn);
            }
            finally
            {
                await conn.DisposeAsync();
            }
        }
    }
}

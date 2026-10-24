using Dapper;
using MySqlConnector;

namespace Bonds.Infrastructure.Policy;

/// <summary>
/// Задача 67 (#13): взаимоисключение построения планов ОДНОГО пользователя — прогон правил (<see cref="PolicyRunner.RunForUserAsync"/>) и ручной план
/// (<see cref="PolicyService.CreateManualAsync"/>: inflow/withdraw/exit) читают свободные деньги за вычетом резерва и сохраняют план; параллельно оба увидели
/// бы одни и те же деньги. null из <see cref="TryAcquireAsync"/> — занято дольше <c>wait</c>.
/// </summary>
public interface IPolicyUserLock
{
    Task<IAsyncDisposable?> TryAcquireAsync(ulong userId, TimeSpan wait, CancellationToken ct = default);
}

/// <summary>MySQL <c>GET_LOCK('bonds-policy-user-{id}', wait)</c> на отдельном соединении, удерживаемом всё время владения (как <c>MySqlPlanExecutorLock</c>). Singleton.</summary>
public sealed class MySqlPolicyUserLock : IPolicyUserLock
{
    public const string LockPrefix = "bonds-policy-user-";

    private readonly string _connectionString;

    public MySqlPolicyUserLock(string connectionString) => _connectionString = connectionString;

    public static string NameOf(ulong userId) => LockPrefix + userId;

    public async Task<IAsyncDisposable?> TryAcquireAsync(ulong userId, TimeSpan wait, CancellationToken ct = default)
    {
        var name = NameOf(userId);
        var conn = new MySqlConnection(_connectionString);
        try
        {
            await conn.OpenAsync(ct);
            var got = await conn.ExecuteScalarAsync<long?>(
                new CommandDefinition("SELECT GET_LOCK(@Name, @Wait)", new { Name = name, Wait = (int)Math.Max(0, Math.Ceiling(wait.TotalSeconds)) }, cancellationToken: ct));
            if (got == 1) return new Handle(conn, name);
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
        private readonly string _name;
        private MySqlConnection? _conn;

        public Handle(MySqlConnection conn, string name)
        {
            _conn = conn;
            _name = name;
        }

        public async ValueTask DisposeAsync()
        {
            var conn = Interlocked.Exchange(ref _conn, null);
            if (conn is null) return;
            try
            {
                await conn.ExecuteScalarAsync<long?>("SELECT RELEASE_LOCK(@Name)", new { Name = _name });
            }
            catch
            {
                MySqlConnection.ClearPool(conn); // сессия MySQL снимет GET_LOCK сама
            }
            finally
            {
                await conn.DisposeAsync();
            }
        }
    }
}

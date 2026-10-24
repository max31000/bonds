using System.Security.Cryptography;
using System.Text;
using Tinkoff.InvestApi;

namespace Bonds.Infrastructure.Execution;

/// <summary>
/// Кэш клиентов T-Invest по торговому токену (ревью задачи 65, M2) — singleton на процесс. Ключ — пользователь; рядом хранится SHA-256 токена (только в памяти,
/// никуда не пишется и не логируется): тот же токен → тот же клиент (и его gRPC-канал) на все тики; токен сменился → старый клиент выбрасывается из кэша и
/// создаётся новый. Канал НЕ создаётся на каждый тик.
/// <para>
/// Освобождение: SDK (<c>Tinkoff.InvestApi</c> 0.6.22.1) создаёт <c>GrpcChannel</c> внутри <c>InvestApiClientFactory</c> и наружу его не отдаёт, сам
/// <see cref="InvestApiClient"/> не <see cref="IDisposable"/> — заменённый клиент освобождает сборщик мусора. Если будущая версия SDK сделает клиента
/// <see cref="IDisposable"/>, он будет освобождён при замене (проверка ниже).
/// </para>
/// </summary>
public sealed class TInvestTradeClientCache
{
    private readonly object _lock = new();
    private readonly Dictionary<ulong, (string Hash, InvestApiClient Client)> _clients = new();

    /// <summary>Клиент для токена пользователя: из кэша, если токен тот же, иначе — <paramref name="create"/> (старый клиент удаляется).</summary>
    public InvestApiClient GetOrCreate(ulong userId, string token, Func<string, InvestApiClient> create)
    {
        var hash = Hash(token);
        IDisposable? replaced = null;
        InvestApiClient client;
        lock (_lock)
        {
            if (_clients.TryGetValue(userId, out var cached) && cached.Hash == hash) return cached.Client;
            if (_clients.TryGetValue(userId, out var old)) replaced = old.Client as IDisposable;
            client = create(token);
            _clients[userId] = (hash, client);
        }

        replaced?.Dispose();
        return client;
    }

    /// <summary>Убрать клиента пользователя (токен удалён).</summary>
    public void Invalidate(ulong userId)
    {
        IDisposable? removed = null;
        lock (_lock)
        {
            if (_clients.Remove(userId, out var old)) removed = old.Client as IDisposable;
        }

        removed?.Dispose();
    }

    /// <summary>Сколько клиентов в кэше (тесты).</summary>
    public int Count
    {
        get
        {
            lock (_lock) return _clients.Count;
        }
    }

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}

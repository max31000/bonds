using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Bonds.Infrastructure.Agents.Mcp;

/// <summary>Кому выдан bearer MCP: пользователь и запуск агента, срок — UTC.</summary>
public sealed record AgentMcpGrant(ulong UserId, ulong RunId, DateTime ExpiresAtUtc);

/// <summary>
/// Одноразовые bearer-токены MCP-сервера bonds (задача 68; singleton, только память процесса). Токен — 32 случайных байта (base64url), выдаётся на ОДИН запуск
/// агента, привязан к пользователю и id запуска (адрес <c>/internal/mcp/{runId}</c>), живёт не дольше таймаута запуска и отзывается по его окончании.
/// Хранится только SHA-256 токена. Отдельно от cookie-авторизации пользователей; после рестарта API все токены недействительны (запуски тоже прерваны).
/// </summary>
public sealed class AgentMcpTokens
{
    private readonly ConcurrentDictionary<string, AgentMcpGrant> _grants = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;

    public AgentMcpTokens(TimeProvider time) => _time = time;

    public string Issue(ulong userId, ulong runId, TimeSpan ttl)
    {
        PurgeExpired();
        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        _grants[Hash(token)] = new AgentMcpGrant(userId, runId, _time.GetUtcNow().UtcDateTime.Add(ttl));
        return token;
    }

    /// <summary>Действующий токен ЭТОГО запуска → пользователь. Чужой запуск, истёкший, отозванный или неизвестный токен — null.</summary>
    public ulong? Validate(string? token, ulong runId)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 128) return null;
        var key = Hash(token);
        if (!_grants.TryGetValue(key, out var grant)) return null;
        if (grant.ExpiresAtUtc <= _time.GetUtcNow().UtcDateTime)
        {
            _grants.TryRemove(key, out _);
            return null;
        }

        return grant.RunId == runId ? grant.UserId : null;
    }

    public void Revoke(string token)
    {
        if (_grants.TryRemove(Hash(token), out var grant)) _proposals.TryRemove(grant.RunId, out _);
    }

    private readonly ConcurrentDictionary<ulong, int> _proposals = new();

    /// <summary>Можно ли запуску ещё предложить план (<c>propose_plan</c>): созданных меньше <paramref name="max"/>. Неудачные попытки (ошибка валидации) не считаются.</summary>
    public bool CanPropose(ulong runId, int max) => _proposals.GetValueOrDefault(runId) < max;

    /// <summary>Запуск создал план.</summary>
    public void RecordProposal(ulong runId) => _proposals.AddOrUpdate(runId, 1, (_, n) => n + 1);

    private void PurgeExpired()
    {
        var now = _time.GetUtcNow().UtcDateTime;
        foreach (var (key, grant) in _grants)
        {
            if (grant.ExpiresAtUtc <= now && _grants.TryRemove(key, out _)) _proposals.TryRemove(grant.RunId, out _);
        }
    }

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Bonds.Core.Agents;

namespace Bonds.Infrastructure.Agents.Mcp;

/// <summary>Кому выдан bearer MCP: пользователь, запуск агента и его вид (от вида зависит доступ к <c>propose_plan</c>), срок — UTC.</summary>
public sealed record AgentMcpGrant(ulong UserId, ulong RunId, AgentTaskKind Kind, DateTime ExpiresAtUtc);

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

    public string Issue(ulong userId, ulong runId, TimeSpan ttl, AgentTaskKind kind)
    {
        PurgeExpired();
        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        var grant = new AgentMcpGrant(userId, runId, kind, _time.GetUtcNow().UtcDateTime.Add(ttl));
        _grants[Hash(token)] = grant;
        _runKinds[runId] = kind;
        return token;
    }

    private readonly ConcurrentDictionary<ulong, AgentTaskKind> _runKinds = new();

    /// <summary>Вид задачи действующего запуска (из выданного ему bearer) или null — запуск неизвестен/завершён.</summary>
    public AgentTaskKind? KindOf(ulong runId) => _runKinds.TryGetValue(runId, out var kind) ? kind : null;

    /// <summary>Виды задач, которым разрешён <c>propose_plan</c> (ревью MINOR 2): разбор события и вопрос пользователя. Дайджест и ежемесячный обзор — только чтение.</summary>
    public static bool KindMayPropose(AgentTaskKind? kind) => kind is AgentTaskKind.EventReview or AgentTaskKind.OnDemand;

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
        if (_grants.TryRemove(Hash(token), out var grant)) ForgetRun(grant.RunId);
    }

    private readonly ConcurrentDictionary<ulong, int> _proposals = new();

    /// <summary>
    /// Атомарно занимает слот <c>propose_plan</c> запуска (CAS, ревью MINOR 1): true — занят (слотов было меньше <paramref name="max"/>), false — лимит исчерпан.
    /// Параллельные вызовы одного запуска не превысят <paramref name="max"/>. Если план в итоге не создан (валидация, ошибка) — <see cref="ReleaseProposal"/>.
    /// </summary>
    public bool TryReserveProposal(ulong runId, int max)
    {
        while (true)
        {
            if (_proposals.TryGetValue(runId, out var current))
            {
                if (current >= max) return false;
                if (_proposals.TryUpdate(runId, current + 1, current)) return true;
            }
            else
            {
                if (max <= 0) return false;
                if (_proposals.TryAdd(runId, 1)) return true;
            }
        }
    }

    /// <summary>Возвращает слот, занятый <see cref="TryReserveProposal"/>, если план не создан.</summary>
    public void ReleaseProposal(ulong runId)
    {
        while (_proposals.TryGetValue(runId, out var current) && current > 0)
        {
            if (_proposals.TryUpdate(runId, current - 1, current)) return;
        }
    }

    /// <summary>Сколько слотов занято сейчас (для тестов).</summary>
    public int ReservedProposals(ulong runId) => _proposals.GetValueOrDefault(runId);

    private void ForgetRun(ulong runId)
    {
        _proposals.TryRemove(runId, out _);
        _runKinds.TryRemove(runId, out _);
    }

    private void PurgeExpired()
    {
        var now = _time.GetUtcNow().UtcDateTime;
        foreach (var (key, grant) in _grants)
        {
            if (grant.ExpiresAtUtc <= now && _grants.TryRemove(key, out _)) ForgetRun(grant.RunId);
        }
    }

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

using System.Collections.Concurrent;
using Bonds.Core.Agents;

namespace Bonds.IntegrationTests.Infrastructure;

/// <summary>Подмена <see cref="IAgentRunner"/> (задача 68): реального CLI в тестах нет — запоминает задания и токены, отвечает заданным текстом.</summary>
public sealed class FakeAgentRunner : IAgentRunner
{
    public sealed record Call(AgentTask Task, string Token);

    public ConcurrentQueue<Call> Calls { get; } = new();

    public Func<AgentTask, AgentResult> Respond { get; set; } = t => new AgentResult($"ответ агента для {t.Kind} <b>&", 0.02m, 0, 1234);

    public IReadOnlyList<Call> CallsFor(ulong userId) => Calls.Where(c => c.Task.UserId == userId).ToList();

    public Task<AgentResult> RunAsync(AgentTask task, AgentCredentials credentials, CancellationToken ct = default)
    {
        Calls.Enqueue(new Call(task, credentials.OAuthToken));
        return Task.FromResult(Respond(task));
    }
}

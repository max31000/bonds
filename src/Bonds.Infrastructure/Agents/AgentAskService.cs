using Bonds.Core.Agents;
using Bonds.Core.Telegram;
using Microsoft.Extensions.Options;

namespace Bonds.Infrastructure.Agents;

/// <summary>
/// <c>/ask &lt;вопрос&gt;</c> (задача 68): проверяет ворота (подключение, <c>agent_enabled</c>, суточный лимит), паузу между вопросами
/// (<see cref="AgentOptions.AskCooldown"/>) и ставит задание в очередь — ответ придёт отдельным сообщением. Возвращает текст ответа боту (без HTML).
/// </summary>
public sealed class AgentAskService
{
    private readonly AgentService _service;
    private readonly AgentWorkQueue _queue;
    private readonly IOptionsMonitor<AgentOptions> _options;

    public AgentAskService(AgentService service, AgentWorkQueue queue, IOptionsMonitor<AgentOptions> options)
    {
        _service = service;
        _queue = queue;
        _options = options;
    }

    public async Task<string> SubmitAsync(ulong userId, string question)
    {
        var trimmed = question.Trim();
        if (trimmed.Length == 0) return "Нужен вопрос. Пример: " + AgentMessages.AskUsage;
        var max = _options.CurrentValue.MaxQuestionChars;
        if (trimmed.Length > max) return $"Вопрос длиннее {max} символов — сократите его.";

        var gate = await _service.EvaluateGateAsync(userId, AgentTaskKind.OnDemand);
        if (!gate.Allowed) return "🤖 " + gate.Reason;
        if (!_queue.TryMarkAsk(userId)) return "🤖 Не так часто: следующий вопрос — через минуту.";
        return _queue.TryEnqueue(new AgentWorkItem(userId, AgentTaskKind.OnDemand, trimmed)) ? AgentMessages.AskAccepted : AgentMessages.AskQueueFull;
    }
}

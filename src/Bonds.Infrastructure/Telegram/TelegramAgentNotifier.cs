using Bonds.Core.Agents;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Telegram;
using Bonds.Infrastructure.Agents;
using Microsoft.Extensions.Logging;

namespace Bonds.Infrastructure.Telegram;

/// <summary>
/// Итог запуска агента — в личку пользователя (задача 68). Текст агента недоверенный: только <see cref="AgentMessages.Format"/> (HTML-escape, ≤ 4096), без кнопок.
/// Сбой отправки — только лог.
/// </summary>
public sealed class TelegramAgentNotifier : IAgentNotifier
{
    private readonly ITelegramBotClient _bot;
    private readonly IUserRepository _users;
    private readonly ILogger<TelegramAgentNotifier> _logger;

    public TelegramAgentNotifier(ITelegramBotClient bot, IUserRepository users, ILogger<TelegramAgentNotifier> logger)
    {
        _bot = bot;
        _users = users;
        _logger = logger;
    }

    public async Task NotifyAsync(ulong userId, AgentTaskKind kind, string? question, AgentResult result, CancellationToken ct = default)
    {
        try
        {
            var user = await _users.GetByIdAsync(userId);
            if (user is null || user.TelegramId == 0) return;
            await _bot.SendMessageAsync(user.TelegramId, AgentMessages.Format(kind, question, result), null, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Agent result notification for user {UserId} failed: {ErrorType}", userId, ex.GetType().Name);
        }
    }
}

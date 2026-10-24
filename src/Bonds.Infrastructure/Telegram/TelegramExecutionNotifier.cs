using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Telegram;
using Bonds.Infrastructure.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bonds.Infrastructure.Telegram;

/// <summary>
/// Уведомления исполнителя (задача 65) через тот же бот и клиент, что и планы (<see cref="ITelegramBotClient"/>; страховочная обрезка 4096 — в клиенте).
/// Получатель — Telegram id пользователя. Ограничено <c>TelegramBot:NotifyTimeout</c>; никогда не бросает (кроме отмены вызывающего).
/// </summary>
public sealed class TelegramExecutionNotifier : IExecutionNotifier
{
    private readonly ITelegramBotClient _bot;
    private readonly IUserRepository _users;
    private readonly TelegramBotOptions _options;
    private readonly ILogger<TelegramExecutionNotifier> _logger;

    public TelegramExecutionNotifier(ITelegramBotClient bot, IUserRepository users, IOptions<TelegramBotOptions> options, ILogger<TelegramExecutionNotifier> logger)
    {
        _bot = bot;
        _users = users;
        _options = options.Value;
        _logger = logger;
    }

    public async Task NotifyAsync(ulong userId, BotMessage message, CancellationToken ct = default)
    {
        if (!_bot.IsConfigured) return;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_options.NotifyTimeout);
            var user = await _users.GetByIdAsync(userId);
            if (user is null) return;
            await _bot.SendMessageAsync(user.TelegramId, message.Html, message.Keyboard, timeout.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Telegram execution notification failed for user {UserId}: {ErrorType}: {Message}", userId, ex.GetType().Name, ex.Message);
        }
    }
}

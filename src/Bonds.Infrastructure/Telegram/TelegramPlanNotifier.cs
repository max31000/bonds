using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Policy;
using Bonds.Core.Telegram;
using Bonds.Infrastructure.Policy;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bonds.Infrastructure.Telegram;

/// <summary>
/// Уведомление о новом плане в Telegram (задача 64), вызывается <see cref="PolicyRunner"/> после сохранения плана: <c>PendingApproval</c> — сообщение с кнопками
/// «Подтвердить / Отклонить / Детали», <c>AutoApproved</c> — информационное без кнопок, остальные статусы не уведомляются. Получатель — Telegram id пользователя-владельца
/// плана. Никогда не бросает: сбой отправки логируется предупреждением, прогон правил продолжается. Один план — одно сообщение (дубликаты планов правила не создаёт).
/// </summary>
public sealed class TelegramPlanNotifier : IPlanNotifier
{
    private readonly ITelegramBotClient _bot;
    private readonly IUserRepository _users;
    private readonly TelegramBotOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<TelegramPlanNotifier> _logger;

    public TelegramPlanNotifier(
        ITelegramBotClient bot, IUserRepository users, IOptions<TelegramBotOptions> options, TimeProvider time, ILogger<TelegramPlanNotifier> logger)
    {
        _bot = bot;
        _users = users;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    public async Task NotifyPlanCreatedAsync(TradePlan plan, CancellationToken ct = default)
    {
        if (plan.Status is not (PlanStatuses.PendingApproval or PlanStatuses.AutoApproved)) return;
        if (!_bot.IsConfigured) return;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_options.NotifyTimeout);
            var user = await _users.GetByIdAsync(plan.UserId);
            if (user is null) return;

            var message = PlanMessageFormatter.ForNewPlan(plan, _time.GetUtcNow().UtcDateTime, _options.WebBaseUrl);
            await _bot.SendMessageAsync(user.TelegramId, message.Html, message.Keyboard, timeout.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Telegram plan notification failed for plan {PlanId}: {ErrorType}: {Message}", plan.Id, ex.GetType().Name, ex.Message);
        }
    }
}

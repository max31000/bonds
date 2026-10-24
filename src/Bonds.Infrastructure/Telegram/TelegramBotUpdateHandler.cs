using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Bonds.Core.Policy;
using Bonds.Core.Telegram;
using Bonds.Core.Time;
using Bonds.Infrastructure.Analytics;
using Bonds.Infrastructure.Policy;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bonds.Infrastructure.Telegram;

/// <summary>Обработчик одного апдейта (отдельный интерфейс — чтобы цикл опроса тестировался без БД и движка правил).</summary>
public interface ITelegramUpdateHandler
{
    /// <summary>Не бросает исключений обработки (кроме отмены): сбой логируется, апдейт считается обработанным.</summary>
    Task HandleAsync(BotUpdate update, CancellationToken ct = default);
}

/// <summary>
/// Обработка входящих апдейтов бота (задача 64). Бот брокеров не вызывает: «Подтвердить» — это <see cref="PolicyService.ApproveAsync"/> (план → <c>Approved</c>),
/// ровно как в вебе; исполняет план (если он не dry-run) только исполнитель. «Отменить» (задача 65) лишь ставит флаг <c>cancel_requested</c>. Пользователь определяется по Telegram id в таблице <c>users</c> (любой зарегистрированный
/// управляет СВОИМИ планами, владелец не зашит); чужие и незнакомые id получают «Нет доступа» и дальше не обрабатываются. Команды — только в личном чате с ботом.
/// Нажатие кнопки проверяет: пользователь — владелец плана, nonce совпал, статус <c>PendingApproval</c>, срок не истёк; повторное нажатие безопасно
/// (решение атомарно в <see cref="ITradePlanRepository.TransitionAsync"/>).
/// </summary>
public sealed class TelegramBotUpdateHandler : ITelegramUpdateHandler
{
    private const string PrivateChat = "private";

    private readonly ITelegramBotClient _bot;
    private readonly IUserRepository _users;
    private readonly ITradePlanRepository _plans;
    private readonly IUserSettingsRepository _settings;
    private readonly PolicyService _policy;
    private readonly CapitalOverviewBuilder _capital;
    private readonly Bonds.Infrastructure.Execution.IExecutionGateResolver _gate;
    private readonly TelegramBotOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<TelegramBotUpdateHandler> _logger;

    public TelegramBotUpdateHandler(
        ITelegramBotClient bot,
        IUserRepository users,
        ITradePlanRepository plans,
        IUserSettingsRepository settings,
        PolicyService policy,
        CapitalOverviewBuilder capital,
        Bonds.Infrastructure.Execution.IExecutionGateResolver gate,
        IOptions<TelegramBotOptions> options,
        TimeProvider time,
        ILogger<TelegramBotUpdateHandler> logger)
    {
        _bot = bot;
        _users = users;
        _plans = plans;
        _settings = settings;
        _policy = policy;
        _capital = capital;
        _gate = gate;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    public async Task HandleAsync(BotUpdate update, CancellationToken ct = default)
    {
        try
        {
            if (update.Callback is { } callback) await HandleCallbackAsync(callback, ct);
            else if (update.Message is { } message) await HandleMessageAsync(message, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Без объекта исключения целиком: в нём может оказаться что угодно из сетевого слоя. Тип + сообщение (в TelegramApiException/HttpRequestException оно очищено).
            _logger.LogWarning("Telegram update {UpdateId} failed: {ErrorType}: {Message}", update.UpdateId, ex.GetType().Name, ex.Message);
        }
    }

    // ─── Сообщения и команды ───────────────────────────────────────────────────────────────

    private async Task HandleMessageAsync(BotIncomingMessage message, CancellationToken ct)
    {
        // Только личка с самим собой: группы и каналы молча игнорируем (бот не должен раскрывать капитал в общем чате).
        if (message.ChatType != PrivateChat || message.ChatId != message.FromId) return;

        var user = await _users.GetByTelegramIdAsync(message.FromId);
        if (user is null)
        {
            await ReplyAsync(message.ChatId, BotTexts.NoAccess, null, ct);
            return;
        }

        var command = BotCommandParser.Parse(message.Text);
        var now = _time.GetUtcNow().UtcDateTime;
        try
        {
            switch (command.Kind)
            {
                case BotCommandKind.Help:
                    await ReplyAsync(message.ChatId, BotTexts.Help(await _settings.IsExecutionPausedAsync(user.Id)), null, ct);
                    break;
                case BotCommandKind.Status:
                    await ReplyAsync(message.ChatId, await BuildStatusAsync(user.Id, now, ct), null, ct);
                    break;
                case BotCommandKind.Plans:
                    await ReplyPlansAsync(message.ChatId, user.Id, now, ct);
                    break;
                case BotCommandKind.Inflow:
                    await CreatePlanAsync(message.ChatId, user.Id, PlanRules.P2Inflow, new PlanParams(Amount: command.Amount, Portfolio: command.Portfolio), now, ct);
                    break;
                case BotCommandKind.Withdraw:
                    await CreatePlanAsync(
                        message.ChatId, user.Id, PlanRules.P5Withdraw, new PlanParams(Amount: command.Amount, Fraction: command.Fraction, Portfolio: command.Portfolio), now, ct);
                    break;
                case BotCommandKind.Exit:
                    await CreatePlanAsync(message.ChatId, user.Id, PlanRules.Manual, new PlanParams(Code: command.Code), now, ct);
                    break;
                case BotCommandKind.Stop:
                    await _settings.SetExecutionPausedAsync(user.Id, true);
                    await ReplyAsync(message.ChatId, "⏸ Пауза включена: авто-подтверждение и исполнение остановлены (висящие заявки снимаются), новые планы ждут вашего решения. Снять — /resume.", null, ct);
                    break;
                case BotCommandKind.Resume:
                    await _settings.SetExecutionPausedAsync(user.Id, false);
                    await ReplyAsync(message.ChatId, "▶ Пауза снята: авто-подтверждение работает в пределах лимитов.", null, ct);
                    break;
                case BotCommandKind.Invalid:
                    await ReplyAsync(message.ChatId, BotFormat.Html(command.Error), null, ct);
                    break;
                default:
                    await ReplyAsync(message.ChatId, "Не понял сообщение. Список команд — /help.", null, ct);
                    break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (PolicyValidationException ex)
        {
            await ReplyAsync(message.ChatId, "Не получилось построить план: " + BotFormat.Html(ex.Message), null, ct);
        }
        catch (Exception ex) when (ex is not TelegramApiException and not HttpRequestException)
        {
            _logger.LogWarning(ex, "Telegram command {Command} failed for user {UserId}", command.Kind, user.Id);
            await ReplyAsync(message.ChatId, "Не удалось выполнить команду, попробуйте позже.", null, ct);
        }
    }

    private async Task ReplyAsync(long chatId, string html, InlineKeyboard? keyboard, CancellationToken ct) =>
        await _bot.SendMessageAsync(chatId, html, keyboard, ct);

    private async Task CreatePlanAsync(long chatId, ulong userId, string rule, PlanParams parameters, DateTime now, CancellationToken ct)
    {
        var (plan, outcome) = await _policy.CreateManualAsync(userId, rule, parameters, now, ct);
        if (plan is null)
        {
            var notes = outcome.Notes.Count == 0 ? string.Empty : "\n" + string.Join("\n", outcome.Notes.Take(5).Select(n => "• " + BotFormat.Html(n)));
            await ReplyAsync(chatId, "План не создан." + notes, null, ct);
            return;
        }

        var message = PlanMessageFormatter.ForNewPlan(plan, now, _options.WebBaseUrl);
        await ReplyAsync(chatId, message.Html, message.Keyboard, ct);
    }

    private async Task<string> BuildStatusAsync(ulong userId, DateTime now, CancellationToken ct)
    {
        var overview = await _capital.BuildAsync(userId, BusinessClock.MoscowDate(now), ct);
        var portfolios = overview
            .Where(p => p.HasAccount)
            .Select(p => new StatusPortfolio(p.Portfolio, p.Currency, p.Drift, p.FreeCash, p.DataIncomplete))
            .ToList();
        var open = await OpenPlansAsync(userId, now);
        return BotTexts.Status(portfolios, open.Count, await _settings.IsExecutionPausedAsync(userId), (await _gate.ResolveAsync(userId)).DryRun);
    }

    /// <summary>«Открытые» для бота — ждущие решения (<c>PendingApproval</c>) и авто-подтверждённые (<c>AutoApproved</c>), срок которых не истёк.</summary>
    private async Task<IReadOnlyList<TradePlan>> OpenPlansAsync(ulong userId, DateTime now)
    {
        var plans = await _plans.ListAsync(userId, null, 200);
        return plans
            .Where(p => p.Status is PlanStatuses.PendingApproval or PlanStatuses.AutoApproved && p.ExpiresAt > now)
            .OrderByDescending(p => p.CreatedAt)
            .ToList();
    }

    private async Task ReplyPlansAsync(long chatId, ulong userId, DateTime now, CancellationToken ct)
    {
        var message = BotTexts.Plans(await OpenPlansAsync(userId, now), _options.WebBaseUrl, _options.MaxListedPlans);
        await ReplyAsync(chatId, message.Html, message.Keyboard, ct);
    }

    // ─── Кнопки ────────────────────────────────────────────────────────────────────────────

    private async Task HandleCallbackAsync(BotIncomingCallback callback, CancellationToken ct)
    {
        // Кнопки нажимают в личке с ботом; вне лички — молча закрываем часики.
        if (callback.ChatType is not null && callback.ChatType != PrivateChat || callback.ChatId is { } chat && chat != callback.FromId)
        {
            await _bot.AnswerCallbackQueryAsync(callback.Id, null, false, ct);
            return;
        }

        var user = await _users.GetByTelegramIdAsync(callback.FromId);
        if (user is null)
        {
            await _bot.AnswerCallbackQueryAsync(callback.Id, BotTexts.NoAccess, true, ct);
            return;
        }

        if (!BotCallback.TryParse(callback.Data, out var data) || data is null)
        {
            await _bot.AnswerCallbackQueryAsync(callback.Id, "Кнопка недействительна.", false, ct);
            return;
        }

        var now = _time.GetUtcNow().UtcDateTime;

        // Планы читаются в разрезе пользователя: чужой план неотличим от несуществующего.
        var plan = await _plans.GetAsync(user.Id, data.PlanId);
        if (plan is null || !BotCallback.NonceEquals(plan.BotNonce, data.Nonce))
        {
            await _bot.AnswerCallbackQueryAsync(callback.Id, "План не найден или кнопка недействительна.", true, ct);
            return;
        }

        if (data.Action == BotPlanAction.CancelExecution)
        {
            // «Отменить» у висящей заявки (задача 65): только флаг — заявки снимает исполнитель на ближайшем тике (без гонки с перевыставлением).
            var requested = await _plans.RequestCancelAsync(user.Id, plan.Id);
            await _bot.AnswerCallbackQueryAsync(
                callback.Id, requested ? "Отмена запрошена — заявки будут сняты в течение минуты." : "Нечего отменять: план уже не исполняется.", !requested, ct);
            return;
        }

        if (plan.Status != PlanStatuses.PendingApproval)
        {
            await _bot.AnswerCallbackQueryAsync(callback.Id, "План уже обработан: " + StatusText(plan.Status) + ".", false, ct);
            await EditAsync(callback, plan, now, $"ℹ Уже обработан: {StatusText(plan.Status)}", ct);
            return;
        }

        if (plan.ExpiresAt <= now)
        {
            await _bot.AnswerCallbackQueryAsync(callback.Id, "Срок действия плана истёк.", true, ct);
            await EditAsync(callback, plan, now, "⌛ Срок действия истёк — план не подтверждён", ct);
            return;
        }

        var result = data.Action == BotPlanAction.Approve
            ? await _policy.ApproveAsync(user.Id, plan.Id, DecidedVia.Telegram, now)
            : await _policy.RejectAsync(user.Id, plan.Id, DecidedVia.Telegram, now);

        switch (result.Kind)
        {
            case PlanDecisionKind.Ok when result.Plan is { } decided:
                var verb = data.Action == BotPlanAction.Approve ? "✅ Подтверждено" : "❌ Отклонено";
                await _bot.AnswerCallbackQueryAsync(callback.Id, data.Action == BotPlanAction.Approve ? "Подтверждено." : "Отклонено.", false, ct);
                await EditAsync(callback, decided, now, $"{verb} {BotFormat.MoscowTime(decided.DecidedAt ?? now)}", ct);
                break;
            case PlanDecisionKind.Expired:
                await _bot.AnswerCallbackQueryAsync(callback.Id, "Срок действия плана истёк.", true, ct);
                await EditAsync(callback, result.Plan ?? plan, now, "⌛ Срок действия истёк — план не подтверждён", ct);
                break;
            default:
                await _bot.AnswerCallbackQueryAsync(callback.Id, "План уже обработан: " + StatusText(result.Plan?.Status) + ".", false, ct);
                if (result.Plan is not null) await EditAsync(callback, result.Plan, now, $"ℹ Уже обработан: {StatusText(result.Plan.Status)}", ct);
                break;
        }
    }

    /// <summary>Переписывает сообщение с кнопкой: текст плана + итог решения, кнопок больше нет. Сбой правки не отменяет принятого решения.</summary>
    private async Task EditAsync(BotIncomingCallback callback, TradePlan plan, DateTime now, string footer, CancellationToken ct)
    {
        if (callback.ChatId is not { } chatId || callback.MessageId is not { } messageId) return;
        try
        {
            await _bot.EditMessageTextAsync(chatId, messageId, PlanMessageFormatter.FormatHtml(plan, now, footer), null, ct);
        }
        catch (Exception ex) when (ex is TelegramApiException or HttpRequestException)
        {
            _logger.LogWarning("Telegram message edit failed: {Message}", ex.Message);
        }
    }

    private static string StatusText(string? status) => status switch
    {
        PlanStatuses.Approved => "подтверждён",
        PlanStatuses.Rejected => "отклонён",
        PlanStatuses.Expired => "срок истёк",
        PlanStatuses.AutoApproved => "авто-подтверждён",
        PlanStatuses.PendingApproval => "ждёт решения",
        PlanStatuses.Submitting => "исполняется",
        PlanStatuses.Done => "исполнен",
        PlanStatuses.PartiallyDone => "исполнен частично",
        PlanStatuses.Failed => "не исполнен",
        null => "не найден",
        _ => status,
    };
}

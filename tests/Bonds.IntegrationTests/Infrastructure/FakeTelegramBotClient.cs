using System.Collections.Concurrent;
using Bonds.Core.Telegram;
using Bonds.Infrastructure.Telegram;

namespace Bonds.IntegrationTests.Infrastructure;

/// <summary>
/// Подмена <see cref="ITelegramBotClient"/> для интеграционных тестов: сети нет, всё отправленное записывается. Общий на фабрику singleton — тесты фильтруют по chat id
/// (у каждого тестового пользователя случайный Telegram id).
/// </summary>
public sealed class FakeTelegramBotClient : ITelegramBotClient
{
    public sealed record Sent(long ChatId, string Html, InlineKeyboard? Keyboard);

    public sealed record Edited(long ChatId, long MessageId, string Html, InlineKeyboard? Keyboard);

    public sealed record Answered(string CallbackId, string? Text, bool Alert);

    public ConcurrentQueue<Sent> Messages { get; } = new();
    public ConcurrentQueue<Edited> Edits { get; } = new();
    public ConcurrentQueue<Answered> Answers { get; } = new();

    public bool IsConfigured => true;

    public IReadOnlyList<Sent> SentTo(long chatId) => Messages.Where(m => m.ChatId == chatId).ToList();

    public Task<long?> SendMessageAsync(long chatId, string html, InlineKeyboard? keyboard = null, CancellationToken ct = default)
    {
        Messages.Enqueue(new Sent(chatId, html, keyboard));
        return Task.FromResult<long?>(Messages.Count);
    }

    public Task EditMessageTextAsync(long chatId, long messageId, string html, InlineKeyboard? keyboard = null, CancellationToken ct = default)
    {
        Edits.Enqueue(new Edited(chatId, messageId, html, keyboard));
        return Task.CompletedTask;
    }

    public Task AnswerCallbackQueryAsync(string callbackQueryId, string? text = null, bool showAlert = false, CancellationToken ct = default)
    {
        Answers.Enqueue(new Answered(callbackQueryId, text, showAlert));
        return Task.CompletedTask;
    }

    public Task<GetUpdatesResult> GetUpdatesAsync(long offset, int timeoutSeconds, CancellationToken ct = default) =>
        Task.FromResult(new GetUpdatesResult([], false));
}

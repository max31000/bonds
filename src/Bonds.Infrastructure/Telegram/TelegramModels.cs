using Bonds.Core.Telegram;

namespace Bonds.Infrastructure.Telegram;

/// <summary>Входящее текстовое сообщение (только нужные поля Bot API).</summary>
public sealed record BotIncomingMessage(long ChatId, string ChatType, long FromId, string? FromName, string? Text);

/// <summary>Нажатие inline-кнопки. <see cref="MessageId"/>/<see cref="ChatId"/> — сообщение с кнопкой (null для inline-сообщений).</summary>
public sealed record BotIncomingCallback(string Id, long FromId, long? ChatId, string? ChatType, long? MessageId, string? Data);

/// <summary>Один апдейт Bot API: сообщение либо нажатие кнопки (остальные типы игнорируются, но offset двигается).</summary>
public sealed record BotUpdate(long UpdateId, BotIncomingMessage? Message, BotIncomingCallback? Callback);

/// <summary>Ответ <c>getUpdates</c>: <see cref="Conflict"/> = HTTP 409 (другой потребитель getUpdates или установлен webhook).</summary>
public sealed record GetUpdatesResult(IReadOnlyList<BotUpdate> Updates, bool Conflict);

/// <summary>Ошибка Bot API. Сообщение содержит только метод, код и description от Telegram — НИКОГДА url запроса (в нём токен).</summary>
public sealed class TelegramApiException(string method, int statusCode, string? description)
    : Exception($"Telegram {method}: HTTP {statusCode}{(string.IsNullOrWhiteSpace(description) ? string.Empty : ": " + description)}")
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>
/// Клиент Bot API (задача 64): отправка с inline-клавиатурой и <c>parse_mode=HTML</c>, правка сообщения, ответ на callback, long polling. Тот же бот и токен
/// (<c>Telegram:BotToken</c>), что у <see cref="Bonds.Infrastructure.Scheduling.ITelegramAlertSender"/> (он остаётся как был). Токен в логи и тексты исключений не попадает.
/// </summary>
public interface ITelegramBotClient
{
    /// <summary>false — токен не задан (или плейсхолдер): бот простаивает.</summary>
    bool IsConfigured { get; }

    /// <summary>Отправляет сообщение; возвращает <c>message_id</c>. Бросает <see cref="TelegramApiException"/>/<see cref="HttpRequestException"/> при сбое.</summary>
    Task<long?> SendMessageAsync(long chatId, string html, InlineKeyboard? keyboard = null, CancellationToken ct = default);

    /// <summary>Меняет текст сообщения; клавиатура <c>null</c> убирает кнопки. «Сообщение не изменилось» не считается ошибкой.</summary>
    Task EditMessageTextAsync(long chatId, long messageId, string html, InlineKeyboard? keyboard = null, CancellationToken ct = default);

    /// <summary>Закрывает «часики» на кнопке; <paramref name="text"/> — короткое всплывающее уведомление.</summary>
    Task AnswerCallbackQueryAsync(string callbackQueryId, string? text = null, bool showAlert = false, CancellationToken ct = default);

    /// <summary>Long polling. <paramref name="offset"/> — id первого нужного апдейта (последний обработанный + 1).</summary>
    Task<GetUpdatesResult> GetUpdatesAsync(long offset, int timeoutSeconds, CancellationToken ct = default);
}

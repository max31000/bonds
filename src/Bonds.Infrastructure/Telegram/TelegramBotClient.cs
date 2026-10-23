using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bonds.Core.Telegram;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Bonds.Infrastructure.Telegram;

/// <summary>
/// Реализация <see cref="ITelegramBotClient"/> поверх Bot API. Именованные <see cref="HttpClient"/> без секрета в base address: токен — часть ПУТИ запроса
/// (<c>/bot{token}/method</c>), читается из конфига на каждый вызов. Токен и url запроса не пишутся ни в логи, ни в исключения (ошибки — только метод, код и
/// description Telegram); логирование стандартного обработчика HttpClient для этих клиентов приглушено в DI (оно печатает путь с токеном).
/// </summary>
public sealed class TelegramBotClient : ITelegramBotClient
{
    /// <summary>Клиент для long polling: таймаут больше <c>timeout</c> getUpdates.</summary>
    public const string PollHttpClientName = "telegram-bot-poll";

    private const string TokenPlaceholderPrefix = "REPLACE_WITH";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TelegramBotClient> _logger;

    public TelegramBotClient(IHttpClientFactory httpClientFactory, IConfiguration configuration, ILogger<TelegramBotClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    private string? Token
    {
        get
        {
            var token = _configuration["Telegram:BotToken"];
            return string.IsNullOrWhiteSpace(token) || token.StartsWith(TokenPlaceholderPrefix, StringComparison.Ordinal) ? null : token.Trim();
        }
    }

    public bool IsConfigured => Token is not null;

    /// <summary>Страховка: Telegram отвергает текст длиннее 4096 символов (400) — длинное обрезается (теги закрываются), факт логируется.</summary>
    private string Limit(string method, string html)
    {
        if (html.Length <= PlanMessageFormatter.TelegramLimit) return html;
        _logger.LogWarning("Telegram {Method}: text of {Length} chars exceeds {Limit}, truncated", method, html.Length, PlanMessageFormatter.TelegramLimit);
        return BotFormat.TruncateHtml(html, PlanMessageFormatter.TelegramLimit);
    }

    public async Task<long?> SendMessageAsync(long chatId, string html, InlineKeyboard? keyboard = null, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["chat_id"] = chatId,
            ["text"] = Limit("sendMessage", html),
            ["parse_mode"] = "HTML",
            ["disable_web_page_preview"] = true,
        };
        if (ToMarkup(keyboard) is { } markup) body["reply_markup"] = markup;

        using var doc = await CallAsync("sendMessage", body, TelegramAlertSenderClientName, ct);
        return doc.RootElement.TryGetProperty("result", out var result) && result.TryGetProperty("message_id", out var id) && id.TryGetInt64(out var value) ? value : null;
    }

    public async Task EditMessageTextAsync(long chatId, long messageId, string html, InlineKeyboard? keyboard = null, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["chat_id"] = chatId,
            ["message_id"] = messageId,
            ["text"] = Limit("editMessageText", html),
            ["parse_mode"] = "HTML",
            ["disable_web_page_preview"] = true,
            // Пустая клавиатура убирает кнопки (без reply_markup Telegram оставляет прежние).
            ["reply_markup"] = ToMarkup(keyboard) ?? new { inline_keyboard = Array.Empty<object>() },
        };

        try
        {
            using var _ = await CallAsync("editMessageText", body, TelegramAlertSenderClientName, ct);
        }
        catch (TelegramApiException ex) when (ex.Message.Contains("message is not modified", StringComparison.OrdinalIgnoreCase))
        {
            // Повторная правка тем же текстом — не ошибка.
        }
    }

    public async Task AnswerCallbackQueryAsync(string callbackQueryId, string? text = null, bool showAlert = false, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?> { ["callback_query_id"] = callbackQueryId, ["show_alert"] = showAlert };
        if (!string.IsNullOrEmpty(text)) body["text"] = text.Length > 190 ? text[..190] : text;
        using var _ = await CallAsync("answerCallbackQuery", body, TelegramAlertSenderClientName, ct);
    }

    public async Task<GetUpdatesResult> GetUpdatesAsync(long offset, int timeoutSeconds, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["offset"] = offset,
            ["timeout"] = timeoutSeconds,
            ["allowed_updates"] = new[] { "message", "callback_query" },
        };

        JsonDocument doc;
        try
        {
            doc = await CallAsync("getUpdates", body, PollHttpClientName, ct);
        }
        catch (TelegramApiException ex) when (ex.StatusCode == (int)HttpStatusCode.Conflict)
        {
            // 409: «terminated by other getUpdates request» (второй потребитель) либо у бота установлен webhook.
            return new GetUpdatesResult([], Conflict: true);
        }

        using (doc)
        {
            var updates = new List<BotUpdate>();
            if (doc.RootElement.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in result.EnumerateArray())
                {
                    if (ParseUpdate(item) is { } update) updates.Add(update);
                }
            }

            return new GetUpdatesResult(updates, Conflict: false);
        }
    }

    // Имя клиента отправки совпадает с клиентом алертов (один и тот же api.telegram.org, таймаут 10 с).
    private const string TelegramAlertSenderClientName = Scheduling.TelegramAlertSender.HttpClientName;

    private async Task<JsonDocument> CallAsync(string method, object body, string clientName, CancellationToken ct)
    {
        var token = Token ?? throw new InvalidOperationException("Telegram:BotToken is not configured");
        var http = _httpClientFactory.CreateClient(clientName);

        HttpResponseMessage response;
        try
        {
            response = await http.PostAsJsonAsync($"/bot{token}/{method}", body, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Исключение HttpClient не содержит url по контракту, но на всякий случай не пробрасываем его как есть: тип + метод.
            _logger.LogWarning("Telegram {Method} request failed: {ErrorType}", method, ex.GetType().Name);
            throw new HttpRequestException($"Telegram {method}: request failed ({ex.GetType().Name})");
        }

        using (response)
        {
            var raw = await response.Content.ReadAsStringAsync(ct);
            JsonDocument? doc = null;
            try
            {
                doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(raw) ? "{}" : raw);
            }
            catch (JsonException)
            {
                // Не JSON (прокси, 502): ниже сработает проверка статуса.
            }

            if (!response.IsSuccessStatusCode || (doc is not null && doc.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False))
            {
                string? description = null;
                if (doc is not null && doc.RootElement.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String) description = d.GetString();
                var status = (int)response.StatusCode;
                if (doc is not null && response.IsSuccessStatusCode && doc.RootElement.TryGetProperty("error_code", out var code) && code.TryGetInt32(out var parsed)) status = parsed;
                doc?.Dispose();
                throw new TelegramApiException(method, status, description);
            }

            return doc ?? JsonDocument.Parse("{}");
        }
    }

    private static object? ToMarkup(InlineKeyboard? keyboard)
    {
        if (keyboard is null || keyboard.Rows.Count == 0) return null;
        var rows = keyboard.Rows
            .Select(row => row.Select(b => b.Url is not null
                ? (object)new Dictionary<string, string> { ["text"] = b.Text, ["url"] = b.Url }
                : new Dictionary<string, string> { ["text"] = b.Text, ["callback_data"] = b.CallbackData ?? "noop" }).ToArray())
            .ToArray();
        return new { inline_keyboard = rows };
    }

    internal static BotUpdate? ParseUpdate(JsonElement item)
    {
        if (!item.TryGetProperty("update_id", out var idEl) || !idEl.TryGetInt64(out var updateId)) return null;

        BotIncomingMessage? message = null;
        BotIncomingCallback? callback = null;

        if (item.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.Object)
        {
            var chat = m.TryGetProperty("chat", out var c) ? c : default;
            var from = m.TryGetProperty("from", out var f) ? f : default;
            if (TryLong(chat, "id", out var chatId) && TryLong(from, "id", out var fromId))
            {
                message = new BotIncomingMessage(chatId, Str(chat, "type") ?? string.Empty, fromId, Str(from, "first_name"), Str(m, "text"));
            }
        }
        else if (item.TryGetProperty("callback_query", out var q) && q.ValueKind == JsonValueKind.Object)
        {
            var from = q.TryGetProperty("from", out var f) ? f : default;
            if (TryLong(from, "id", out var fromId) && Str(q, "id") is { } queryId)
            {
                long? chatId = null, messageId = null;
                string? chatType = null;
                if (q.TryGetProperty("message", out var qm) && qm.ValueKind == JsonValueKind.Object)
                {
                    if (qm.TryGetProperty("chat", out var qc))
                    {
                        if (TryLong(qc, "id", out var cid)) chatId = cid;
                        chatType = Str(qc, "type");
                    }

                    if (TryLong(qm, "message_id", out var mid)) messageId = mid;
                }

                callback = new BotIncomingCallback(queryId, fromId, chatId, chatType, messageId, Str(q, "data"));
            }
        }

        return new BotUpdate(updateId, message, callback);
    }

    private static bool TryLong(JsonElement element, string name, out long value)
    {
        value = 0;
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var p) && p.TryGetInt64(out value);
    }

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
}

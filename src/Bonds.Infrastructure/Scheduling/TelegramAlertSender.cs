using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Bonds.Infrastructure.Scheduling;

/// <summary>
/// Реализация <see cref="ITelegramAlertSender"/> — прямой HTTP-вызов Bot API
/// <c>sendMessage</c> (тот же паттерн, что <c>.github/workflows/*.yml</c> использует для
/// CI-алертов деплоя, только из бэкенда, а не из workflow). Именованный <see cref="HttpClient"/>
/// без секрета в base address (bot token — часть URL пути, читается из конфига при каждом
/// вызове, не логируется — spec §11).
/// </summary>
public sealed class TelegramAlertSender : ITelegramAlertSender
{
    public const string HttpClientName = "telegram-bot";

    private readonly HttpClient _http;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TelegramAlertSender> _logger;

    public TelegramAlertSender(IHttpClientFactory httpClientFactory, IConfiguration configuration, ILogger<TelegramAlertSender> logger)
    {
        _http = httpClientFactory.CreateClient(HttpClientName);
        _configuration = configuration;
        _logger = logger;
    }

    public async Task SendAsync(string message, CancellationToken ct = default)
    {
        var botToken = _configuration["Telegram:BotToken"];
        var ownerId = _configuration["Telegram:OwnerId"];
        if (string.IsNullOrWhiteSpace(botToken) || string.IsNullOrWhiteSpace(ownerId))
        {
            _logger.LogWarning("Telegram alert skipped: Telegram:BotToken or Telegram:OwnerId is not configured");
            return;
        }

        try
        {
            var response = await _http.PostAsJsonAsync(
                $"/bot{botToken}/sendMessage",
                new { chat_id = ownerId, text = message },
                ct);

            if (!response.IsSuccessStatusCode)
            {
                // Тело ответа Bot API на ошибку не содержит наш секрет (это ответ Telegram, не
                // наш запрос) — безопасно логировать код статуса без тела на всякий случай.
                _logger.LogWarning("Telegram alert failed with status {StatusCode}", response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            // Алерт — лучше-если-получится: сбой отправки не должен ронять планировщик синка.
            _logger.LogWarning(ex, "Failed to send Telegram alert");
        }
    }
}

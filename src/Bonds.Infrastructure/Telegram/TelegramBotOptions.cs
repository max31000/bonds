namespace Bonds.Infrastructure.Telegram;

/// <summary>
/// Настройки Telegram-бота (раздел <c>TelegramBot</c>, задача 64). Токен бота — НЕ здесь, а в <c>Telegram:BotToken</c> (тот же бот, что у алертов).
/// Тайминги — для тестов (в проде дефолты).
/// </summary>
public sealed class TelegramBotOptions
{
    public const string SectionName = "TelegramBot";

    /// <summary>Таймаут long polling <c>getUpdates</c>, секунды (Bot API держит запрос до этого времени, если апдейтов нет).</summary>
    public int PollTimeoutSeconds { get; set; } = 25;

    /// <summary>Пауза после сбоя итерации (сеть, 5xx, БД).</summary>
    public TimeSpan ErrorDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Пауза после HTTP 409 Conflict: другой потребитель getUpdates того же бота или установлен webhook.</summary>
    public TimeSpan ConflictBackoff { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Не чаще одного алерта о конфликте за это время.</summary>
    public TimeSpan ConflictAlertCooldown { get; set; } = TimeSpan.FromHours(6);

    /// <summary>Пауза, пока токен бота не задан (dev/тесты): сервис простаивает и периодически проверяет заново.</summary>
    public TimeSpan IdleDelay { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Публичный адрес веба (например <c>https://example.org</c>): кнопка «Детали» ведёт на <c>{WebBaseUrl}/bonds/plans/{id}</c>. Пусто — кнопки «Детали» нет.
    /// </summary>
    public string? WebBaseUrl { get; set; }

    /// <summary>Сколько самых новых открытых планов показывает <c>/plans</c> (остальные — строкой «… и ещё N — см. веб»; у клавиатуры Telegram лимит 100 кнопок).</summary>
    public int MaxListedPlans { get; set; } = 10;

    /// <summary>Предел ожидания отправки уведомления о новом плане: сбой/зависание Telegram не должны тормозить прогон правил.</summary>
    public TimeSpan NotifyTimeout { get; set; } = TimeSpan.FromSeconds(5);
}

namespace Bonds.Infrastructure.Scheduling;

/// <summary>
/// Отправка алерта владельцу продукта через Telegram Bot API (plan/13 часть D) — переиспользует
/// того же бота, что и авторизация (<c>Telegram:BotToken</c>) и CI-алерты деплоя. Вынесен за
/// интерфейс, чтобы <see cref="SyncSchedulerHostedService"/> не ходил в реальную сеть в тестах
/// (repo-конвенция: без сети из тестов, мокай HttpMessageHandler/клиент — plan/13 преамбула).
/// </summary>
public interface ITelegramAlertSender
{
    /// <summary>
    /// Отправляет текстовое сообщение владельцу (<c>Telegram:OwnerId</c>). Не бросает исключение
    /// наружу при сетевой/HTTP-ошибке — алерт "лучше-если-получится", не должен уронить сам
    /// планировщик синка (см. вызывающий код в <see cref="SyncSchedulerHostedService"/>).
    /// </summary>
    Task SendAsync(string message, CancellationToken ct = default);
}

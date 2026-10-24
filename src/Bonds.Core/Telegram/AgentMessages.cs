using Bonds.Core.Agents;

namespace Bonds.Core.Telegram;

/// <summary>
/// Тексты бота для ИИ-агента (задача 68). Ответ агента — НЕДОВЕРЕННЫЙ текст: всегда через HTML-escape (<see cref="BotFormat.HtmlTruncated"/>), никакой
/// разметки или кнопок из него не строится. Итог — ≤ 4096 символов после экранирования.
/// </summary>
public static class AgentMessages
{
    private const int TelegramLimit = 4096;
    private const int HeaderReserve = 300;

    public const string AskUsage = "/ask вопрос — например: /ask какие оферты у меня в ближайший месяц?";
    public const string AskAccepted = "🤖 Вопрос принят — ответ придёт отдельным сообщением (обычно в течение нескольких минут).";
    public const string AskQueueFull = "🤖 Агент сейчас занят — попробуйте позже.";
    public const string AskAlreadyQueued = "🤖 Предыдущий вопрос ещё в очереди — дождитесь ответа.";
    public const string Footer = "Оценка ИИ-агента, не инвестиционная рекомендация. Агент не отправляет заявки.";

    public static string Title(AgentTaskKind kind) => kind switch
    {
        AgentTaskKind.DailyDigest => "🤖 Дайджест дня",
        AgentTaskKind.EventReview => "🤖 Разбор события",
        AgentTaskKind.MonthlyReview => "🤖 Обзор месяца",
        AgentTaskKind.OnDemand => "🤖 Ответ агента",
        _ => "🤖 Агент",
    };

    /// <summary>Сообщение с итогом запуска: заголовок, (вопрос), текст ответа или ошибка — всё экранировано.</summary>
    public static string Format(AgentTaskKind kind, string? question, AgentResult result)
    {
        var head = $"<b>{BotFormat.Html(Title(kind))}</b>\n";
        if (!string.IsNullOrWhiteSpace(question) && kind == AgentTaskKind.OnDemand) head += $"<i>{BotFormat.HtmlTruncated(question, 200)}</i>\n";
        var footer = $"\n\n<i>{BotFormat.Html(Footer)}</i>";

        var budget = TelegramLimit - HeaderReserve - footer.Length;
        var body = result.Succeeded
            ? BotFormat.HtmlTruncated(string.IsNullOrWhiteSpace(result.Text) ? "(пустой ответ)" : result.Text, budget)
            : "Не удалось выполнить: " + BotFormat.HtmlTruncated(result.Error ?? "ошибка запуска", 500);
        return BotFormat.TruncateHtml(head + "\n" + body + footer, TelegramLimit);
    }
}

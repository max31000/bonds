using System.Text.RegularExpressions;

namespace Bonds.Core.Agents;

/// <summary>
/// Параметры ИИ-агентов (секция <c>Agent</c>, задача 68). Пользовательские настройки (включение, модель, лимит в сутки) — в <c>agent_settings</c>;
/// здесь — серверные пределы и расписание. Время расписания — МСК (UTC+3, как <c>TradingHours</c>).
/// </summary>
public sealed class AgentOptions
{
    public const string SectionName = "Agent";

    /// <summary>Серверный выключатель всех агентов (env <c>Agent__Enabled=false</c> — ни одного запуска, независимо от настроек пользователей).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Исполняемый файл Claude Code CLI (в образе — <c>/usr/local/bin/claude</c>, ищется по PATH).</summary>
    public string CliPath { get; set; } = "claude";

    /// <summary>
    /// Базовый адрес API для MCP-сервера bonds, видимый процессу агента. ТОЛЬКО loopback (эндпоинт отвечает лишь запросам с 127.0.0.1/::1):
    /// в контейнере API слушает 5001.
    /// </summary>
    public string McpBaseUrl { get; set; } = "http://127.0.0.1:5001";

    /// <summary>Предел ходов модели за запуск (<c>--max-turns</c>).</summary>
    public int MaxTurns { get; set; } = 15;

    /// <summary>Таймаут запуска: по нему процесс убивается вместе с деревом, а MCP-токен запуска истекает.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Верхняя граница пользовательского лимита запусков в сутки.</summary>
    public int MaxDailyRunLimit { get; set; } = 20;

    /// <summary>Разборов Critical-событий (<see cref="AgentTaskKind.EventReview"/>) не больше стольких в сутки МСК.</summary>
    public int EventReviewMaxPerDay { get; set; } = 3;

    /// <summary>Время ежедневного дайджеста по будням (МСК) и ежемесячного обзора 1-го числа.</summary>
    public TimeSpan DigestAtMoscow { get; set; } = new(19, 0, 0);

    /// <summary>Окно после <see cref="DigestAtMoscow"/>, в котором пропущенный (рестарт) дайджест ещё запускается; позже — уже не догоняем.</summary>
    public TimeSpan ScheduleWindow { get; set; } = TimeSpan.FromHours(3);

    /// <summary>
    /// Разрешить встроенные WebSearch/WebFetch исследовательским задачам (разбор события, ежемесячный обзор). По умолчанию ВЫКЛ.: веб-инструменты в одной сессии с
    /// данными портфеля — канал утечки (инъекция в найденной странице может попросить «открыть» адрес с данными в запросе).
    /// </summary>
    public bool AllowWebResearch { get; set; }

    /// <summary>Сколько планов агент может предложить (<c>propose_plan</c>) за один запуск.</summary>
    public int MaxProposalsPerRun { get; set; } = 1;

    /// <summary>Пауза между двумя <c>/ask</c> одного пользователя.</summary>
    public TimeSpan AskCooldown { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Длина вопроса <c>/ask</c>, символов.</summary>
    public int MaxQuestionChars { get; set; } = 1000;

    /// <summary>Сколько символов ответа агента хранится и отправляется (остальное обрезается).</summary>
    public int MaxResultChars { get; set; } = 20_000;

    /// <summary>Предел ответа одного MCP-инструмента, символов JSON (больше — обрезается с пометкой).</summary>
    public int McpMaxResponseChars { get; set; } = 40_000;

    /// <summary>Сколько байт stdout/stderr процесса читается (защита памяти от бесконечного вывода).</summary>
    public int MaxOutputBytes { get; set; } = 4 * 1024 * 1024;
}

/// <summary>Проверка алиаса/имени модели: только безопасные символы (уходит аргументом <c>--model</c>, без оболочки, но всё равно whitelist).</summary>
public static partial class AgentModelName
{
    [GeneratedRegex(@"^[a-z0-9][a-z0-9.\-\[\]]{1,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    public static bool IsValid(string? model) => !string.IsNullOrWhiteSpace(model) && Pattern().IsMatch(model);
}

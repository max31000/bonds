namespace Bonds.Core.Agents;

/// <summary>Вид задачи агента (задача 68). Хранится строкой в <c>agent_runs.kind</c>.</summary>
public enum AgentTaskKind
{
    /// <summary>Будни 19:00 МСК: что изменилось за день, события, планы — кратко в бот.</summary>
    DailyDigest,

    /// <summary>Новое Critical-событие (оферта, дефолт эмитента, просадка): разбор и, если уместно, <c>propose_plan</c>.</summary>
    EventReview,

    /// <summary>1-е число месяца: дрейф корзин, комиссии против модели, кандидаты P3b.</summary>
    MonthlyReview,

    /// <summary><c>/ask &lt;вопрос&gt;</c> в боте.</summary>
    OnDemand,
}

/// <summary>
/// Один запуск агента. <see cref="McpUrl"/>/<see cref="McpBearer"/> — одноразовый доступ к MCP-серверу bonds на этот запуск (привязан к пользователю и
/// запуску, живёт не дольше <see cref="Timeout"/>). <see cref="AllowWebResearch"/> — разрешить встроенные WebSearch/WebFetch (только исследовательские задачи).
/// </summary>
public sealed record AgentTask(
    AgentTaskKind Kind,
    ulong UserId,
    string Prompt,
    string Model,
    int MaxTurns,
    TimeSpan Timeout,
    string McpUrl,
    string McpBearer,
    bool AllowWebResearch = false)
{
    /// <summary>Без bearer-токена MCP (он секрет запуска) — для логов и исключений.</summary>
    public override string ToString() => $"AgentTask {{ Kind = {Kind}, UserId = {UserId}, Model = {Model}, MaxTurns = {MaxTurns}, Timeout = {Timeout} }}";
}

/// <summary>
/// Секрет провайдера ИИ, расшифрованный ТОЛЬКО на время запуска (OAuth-токен <c>claude setup-token</c>). Передаётся процессу агента исключительно через env —
/// не в argv и не в логи. <see cref="ToString"/> секрет не печатает.
/// </summary>
public sealed class AgentCredentials
{
    public AgentCredentials(string oauthToken) => OAuthToken = oauthToken;

    public string OAuthToken { get; }

    public override string ToString() => "AgentCredentials { OAuthToken = *** }";
}

/// <summary>
/// Итог запуска. <see cref="Text"/> — НЕДОВЕРЕННЫЙ текст модели (в вебе экранируется, в бот уходит HTML-escape, ничего из него не исполняется).
/// <see cref="CostUsd"/> — оценка стоимости, которую печатает CLI (USD; при подписке — справочно), <see cref="Tokens"/> — вход + выход.
/// </summary>
public sealed record AgentResult(
    string Text,
    decimal? CostUsd,
    int ExitCode,
    long? Tokens = null,
    bool IsError = false,
    string? Error = null,
    bool TimedOut = false)
{
    public bool Succeeded => ExitCode == 0 && !IsError && !TimedOut;
}

/// <summary>
/// Провайдер ИИ-агента (задача 68). Первая реализация — Claude Code CLI; OpenAI и другие — новые реализации этого интерфейса. Реализация НЕ получает доступа
/// к заявкам, лимитам, настройкам или записи секретов: агент видит только MCP-инструменты bonds (чтение + <c>propose_plan</c>).
/// </summary>
public interface IAgentRunner
{
    Task<AgentResult> RunAsync(AgentTask task, AgentCredentials credentials, CancellationToken ct = default);
}

/// <summary>
/// Настройки агента пользователя (<c>agent_settings</c>). По умолчанию выключен. Модели — алиасы CLI без жёстких версий: <see cref="Model"/> — рутина
/// (дайджест, разбор события, <c>/ask</c>), <see cref="MonthlyModel"/> — ежемесячный обзор. <see cref="DailyRunLimit"/> — запусков в сутки МСК (все виды вместе).
/// </summary>
public sealed record AgentSettings(bool Enabled, string Model, string MonthlyModel, int DailyRunLimit)
{
    public const string DefaultModel = "sonnet";
    public const string DefaultMonthlyModel = "opus";
    public const int DefaultDailyRunLimit = 5;

    public static AgentSettings Default { get; } = new(false, DefaultModel, DefaultMonthlyModel, DefaultDailyRunLimit);

    /// <summary>Модель для вида задачи.</summary>
    public string ModelFor(AgentTaskKind kind) => kind == AgentTaskKind.MonthlyReview ? MonthlyModel : Model;
}

/// <summary>Строка журнала <c>agent_runs</c>. Время — UTC. <see cref="CostUsd"/> — USD (оценка CLI).</summary>
public sealed class AgentRun
{
    public ulong Id { get; set; }
    public ulong UserId { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string? Model { get; set; }
    public string? Question { get; set; }
    public DateTime Started { get; set; }
    public DateTime? Finished { get; set; }
    public int? ExitCode { get; set; }
    public decimal? CostUsd { get; set; }
    public long? Tokens { get; set; }
    public string? ResultText { get; set; }
    public string? Error { get; set; }
}

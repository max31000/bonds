using Bonds.Core.Agents;
using Bonds.Core.Credentials;
using Bonds.Core.Interfaces.Repositories;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bonds.Infrastructure.Agents;

/// <summary>Итог попытки запуска: <see cref="SkipReason"/> — почему не запущен (ворота), иначе строка журнала и результат.</summary>
public sealed record AgentRunOutcome(string? SkipReason, AgentRun? Run, AgentResult? Result)
{
    public bool Started => Run is not null;
}

/// <summary>Доставка итога запуска пользователю (реализация — Telegram; HTML-escape внутри). Не бросает исключений.</summary>
public interface IAgentNotifier
{
    Task NotifyAsync(ulong userId, AgentTaskKind kind, string? question, AgentResult result, CancellationToken ct = default);
}

public sealed class NullAgentNotifier : IAgentNotifier
{
    public Task NotifyAsync(ulong userId, AgentTaskKind kind, string? question, AgentResult result, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>
/// Оркестратор запуска агента (задача 68, scoped). Порядок: ворота (<see cref="AgentRunGate"/>: сервер, подключение <c>claude_code</c>, <c>agent_enabled</c>,
/// суточный лимит, ≤ 3 разборов событий) → строка <c>agent_runs</c> (до процесса — входит в лимит даже при падении) → расшифровка токена ТОЛЬКО сейчас →
/// одноразовый MCP-bearer на этот запуск → <see cref="IAgentRunner"/> → отзыв bearer → итог в журнал (текст обрезан) → уведомление.
/// Токен не логируется и не сохраняется нигде, кроме <c>user_credentials</c>. Агент получает только MCP-инструменты bonds.
/// </summary>
public sealed class AgentService
{
    public const string McpPath = "/internal/mcp";

    private readonly IAgentRunner _runner;
    private readonly IUserCredentialStore _credentials;
    private readonly IAgentSettingsRepository _settings;
    private readonly IAgentRunRepository _runs;
    private readonly Mcp.AgentMcpTokens _tokens;
    private readonly IAgentNotifier _notifier;
    private readonly IOptionsMonitor<AgentOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<AgentService> _logger;

    public AgentService(
        IAgentRunner runner,
        IUserCredentialStore credentials,
        IAgentSettingsRepository settings,
        IAgentRunRepository runs,
        Mcp.AgentMcpTokens tokens,
        IAgentNotifier notifier,
        IOptionsMonitor<AgentOptions> options,
        TimeProvider time,
        ILogger<AgentService> logger)
    {
        _runner = runner;
        _credentials = credentials;
        _settings = settings;
        _runs = runs;
        _tokens = tokens;
        _notifier = notifier;
        _options = options;
        _time = time;
        _logger = logger;
    }

    /// <summary>Решение ворот для пользователя сейчас (без запуска) — его же используют расписание и <c>/ask</c>.</summary>
    public async Task<AgentGateDecision> EvaluateGateAsync(ulong userId, AgentTaskKind kind)
    {
        var options = _options.CurrentValue;
        var now = _time.GetUtcNow().UtcDateTime;
        var dayStart = AgentRunGate.MoscowDayStartUtc(now);
        var settings = await _settings.GetAsync(userId);
        var capability = await _credentials.HasValidAsync(userId, CredentialProviders.ClaudeCode);
        var runsToday = settings.Enabled && capability ? await _runs.CountStartedSinceAsync(userId, dayStart) : 0;
        var reviewsToday = kind == AgentTaskKind.EventReview && settings.Enabled && capability
            ? await _runs.CountStartedSinceAsync(userId, dayStart, AgentTaskKind.EventReview)
            : 0;
        return AgentRunGate.Decide(new AgentGateInput(options.Enabled, capability, settings, runsToday, reviewsToday, kind, options.EventReviewMaxPerDay));
    }

    public async Task<AgentRunOutcome> RunAsync(ulong userId, AgentTaskKind kind, string? question = null, CancellationToken ct = default)
    {
        var gate = await EvaluateGateAsync(userId, kind);
        if (!gate.Allowed)
        {
            _logger.LogInformation("Agent run {Kind} for user {UserId} skipped: {Reason}", kind, userId, gate.Reason);
            return new AgentRunOutcome(gate.Reason, null, null);
        }

        var secret = await _credentials.GetSecretAsync(userId, CredentialProviders.ClaudeCode);
        if (secret is null || !secret.TryGetValue("oauthToken", out var token) || string.IsNullOrWhiteSpace(token))
        {
            _logger.LogWarning("Agent run {Kind} for user {UserId} skipped: credential unavailable", kind, userId);
            return new AgentRunOutcome("Токен Claude Code недоступен — введите его заново в «Подключениях»", null, null);
        }

        var options = _options.CurrentValue;
        var settings = await _settings.GetAsync(userId);
        var model = settings.ModelFor(kind);
        if (!AgentModelName.IsValid(model)) model = kind == AgentTaskKind.MonthlyReview ? AgentSettings.DefaultMonthlyModel : AgentSettings.DefaultModel;

        var run = new AgentRun
        {
            UserId = userId,
            Kind = kind.ToString(),
            Model = model,
            Question = question,
            Started = _time.GetUtcNow().UtcDateTime,
        };
        run.Id = await _runs.InsertStartedAsync(run);

        // Bearer MCP живёт не дольше запуска (+ запас на завершение) и отзывается сразу по окончании.
        var bearer = _tokens.Issue(userId, run.Id, options.Timeout + TimeSpan.FromMinutes(1));
        AgentResult result;
        try
        {
            var task = new AgentTask(
                kind,
                userId,
                AgentPrompts.For(kind, question),
                model,
                Math.Clamp(options.MaxTurns, 1, 100),
                options.Timeout,
                options.McpBaseUrl.TrimEnd('/') + McpPath + "/" + run.Id,
                bearer,
                AllowWebResearch: options.AllowWebResearch && kind is AgentTaskKind.EventReview or AgentTaskKind.MonthlyReview);
            result = await _runner.RunAsync(task, new AgentCredentials(token), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await _runs.FinishAsync(run.Id, _time.GetUtcNow().UtcDateTime, -1, null, null, null, "Запуск прерван (остановка сервиса)");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Agent run {RunId} failed: {ErrorType}", run.Id, ex.GetType().Name);
            result = new AgentResult(string.Empty, null, -1, IsError: true, Error: "Внутренняя ошибка запуска агента");
        }
        finally
        {
            _tokens.Revoke(bearer);
        }

        var text = result.Text.Length > options.MaxResultChars ? result.Text[..options.MaxResultChars] + "…" : result.Text;
        result = result with { Text = text };
        var finished = _time.GetUtcNow().UtcDateTime;
        await _runs.FinishAsync(run.Id, finished, result.ExitCode, result.CostUsd, result.Tokens, result.Succeeded ? text : null, result.Succeeded ? null : result.Error);
        run.Finished = finished;
        run.ExitCode = result.ExitCode;
        run.CostUsd = result.CostUsd;
        run.Tokens = result.Tokens;
        run.ResultText = result.Succeeded ? text : null;
        run.Error = result.Succeeded ? null : result.Error;

        await _notifier.NotifyAsync(userId, kind, question, result, ct);
        return new AgentRunOutcome(null, run, result);
    }
}

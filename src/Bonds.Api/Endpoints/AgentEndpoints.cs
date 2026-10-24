using Bonds.Api.Auth;
using Bonds.Core.Agents;
using Bonds.Core.Credentials;
using Bonds.Core.Interfaces.Repositories;
using Microsoft.Extensions.Options;

namespace Bonds.Api.Endpoints;

/// <summary>
/// Раздел «Агент» (задача 68):
/// <list type="bullet">
/// <item><c>GET /api/agent</c> — настройки (включён, модели, лимит в сутки), доступность (подключение <c>claude_code</c>, серверный выключатель), запуски сегодня и
/// история (последние 30). <c>resultText</c> — НЕДОВЕРЕННЫЙ текст модели: фронт рендерит его только как текст.</item>
/// <item><c>PUT /api/agent/settings</c> — сохранить настройки; включить можно только при проверенном подключении (422 иначе), модель — алиас CLI
/// (<see cref="AgentModelName"/>), лимит — 1…<see cref="AgentOptions.MaxDailyRunLimit"/>.</item>
/// </list>
/// Запуск агента отсюда не делается (расписание, события и <c>/ask</c> в боте).
/// </summary>
public static class AgentEndpoints
{
    public const string Disclaimer = "Ответы ИИ-агента — оценки, не инвестиционные рекомендации. Агент не отправляет заявки и не меняет настройки; предложенный им план ждёт вашего решения.";

    public static void MapAgentEndpoints(this WebApplication app)
    {
        app.MapGet("/api/agent", GetAgent);
        app.MapPut("/api/agent/settings", PutSettings);
    }

    private static async Task<IResult> GetAgent(
        ICurrentUser currentUser, IAgentSettingsRepository settings, IAgentRunRepository runs, IUserCredentialStore credentials, IOptionsMonitor<AgentOptions> options,
        TimeProvider time)
    {
        if (await currentUser.GetUserIdAsync() is not { } userId) return Results.NotFound();
        return Results.Ok(await BuildAsync(userId, settings, runs, credentials, options.CurrentValue, time));
    }

    private static async Task<IResult> PutSettings(
        AgentSettingsRequest request, ICurrentUser currentUser, IAgentSettingsRepository settings, IAgentRunRepository runs, IUserCredentialStore credentials,
        IOptionsMonitor<AgentOptions> options, TimeProvider time)
    {
        if (await currentUser.GetUserIdAsync() is not { } userId) return Results.NotFound();
        var opts = options.CurrentValue;

        var model = request.Model?.Trim() ?? string.Empty;
        var monthly = request.MonthlyModel?.Trim() ?? AgentSettings.DefaultMonthlyModel;
        if (!AgentModelName.IsValid(model) || !AgentModelName.IsValid(monthly))
            return AnalyticsSupport.ValidationError("Модель — алиас Claude Code (sonnet, opus, haiku) или полное имя модели: латиница в нижнем регистре, цифры, «.», «-»");
        if (request.DailyRunLimit < 1 || request.DailyRunLimit > opts.MaxDailyRunLimit)
            return AnalyticsSupport.ValidationError($"Лимит запусков в сутки — от 1 до {opts.MaxDailyRunLimit}");
        if (request.Enabled && !await credentials.HasValidAsync(userId, CredentialProviders.ClaudeCode))
            return AnalyticsSupport.ValidationError("Сначала подключите Claude Code: Настройки → Подключения");

        await settings.UpsertAsync(userId, new AgentSettings(request.Enabled, model, monthly, request.DailyRunLimit));
        return Results.Ok(await BuildAsync(userId, settings, runs, credentials, opts, time));
    }

    private static async Task<AgentStateDto> BuildAsync(
        ulong userId, IAgentSettingsRepository settings, IAgentRunRepository runs, IUserCredentialStore credentials, AgentOptions options, TimeProvider time)
    {
        var s = await settings.GetAsync(userId);
        var now = time.GetUtcNow().UtcDateTime;
        var history = await runs.ListAsync(userId, 30);
        return new AgentStateDto
        {
            Enabled = s.Enabled,
            Model = s.Model,
            MonthlyModel = s.MonthlyModel,
            DailyRunLimit = s.DailyRunLimit,
            MaxDailyRunLimit = options.MaxDailyRunLimit,
            CredentialOk = await credentials.HasValidAsync(userId, CredentialProviders.ClaudeCode),
            ServerEnabled = options.Enabled,
            RunsToday = await runs.CountStartedSinceAsync(userId, AgentRunGate.MoscowDayStartUtc(now)),
            Runs = history.Select(r => new AgentRunDto
            {
                Id = r.Id,
                Kind = r.Kind,
                Model = r.Model,
                Question = r.Question,
                Started = DateTime.SpecifyKind(r.Started, DateTimeKind.Utc),
                Finished = r.Finished is { } f ? DateTime.SpecifyKind(f, DateTimeKind.Utc) : null,
                ExitCode = r.ExitCode,
                CostUsd = r.CostUsd,
                Tokens = r.Tokens,
                ResultText = r.ResultText,
                Error = r.Error,
            }).ToList(),
            Disclaimer = Disclaimer,
        };
    }
}

public sealed record AgentSettingsRequest
{
    public bool Enabled { get; init; }
    public string? Model { get; init; }
    public string? MonthlyModel { get; init; }
    public int DailyRunLimit { get; init; } = AgentSettings.DefaultDailyRunLimit;
}

/// <summary>Состояние раздела «Агент». <see cref="AgentRunDto.CostUsd"/> — USD (оценка CLI).</summary>
public sealed record AgentStateDto
{
    public required bool Enabled { get; init; }
    public required string Model { get; init; }
    public required string MonthlyModel { get; init; }
    public required int DailyRunLimit { get; init; }
    public required int MaxDailyRunLimit { get; init; }

    /// <summary>Подключение <c>claude_code</c> проверено (status ok).</summary>
    public required bool CredentialOk { get; init; }

    public required bool ServerEnabled { get; init; }
    public required int RunsToday { get; init; }
    public required IReadOnlyList<AgentRunDto> Runs { get; init; }
    public required string Disclaimer { get; init; }
}

public sealed record AgentRunDto
{
    public required ulong Id { get; init; }

    /// <summary><c>DailyDigest</c> / <c>EventReview</c> / <c>MonthlyReview</c> / <c>OnDemand</c>.</summary>
    public required string Kind { get; init; }

    public string? Model { get; init; }
    public string? Question { get; init; }
    public required DateTime Started { get; init; }
    public DateTime? Finished { get; init; }
    public int? ExitCode { get; init; }
    public decimal? CostUsd { get; init; }
    public long? Tokens { get; init; }

    /// <summary>Недоверенный текст модели — только как текст (без HTML/Markdown-рендера).</summary>
    public string? ResultText { get; init; }

    public string? Error { get; init; }
}

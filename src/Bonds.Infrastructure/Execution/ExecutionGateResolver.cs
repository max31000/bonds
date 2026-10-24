using System.Text.Json;
using Bonds.Core.Credentials;
using Bonds.Core.Execution;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Policy;
using Microsoft.Extensions.Options;

namespace Bonds.Infrastructure.Execution;

/// <summary>Ворота исполнения пользователя из живых данных (env + БД). Без кэша: каждая проверка читает текущее состояние.</summary>
public interface IExecutionGateResolver
{
    Task<ExecutionGateResult> ResolveAsync(ulong userId);
}

/// <summary>
/// <see cref="ExecutionGate.Evaluate"/> по: <c>Execution:Enabled</c> (env <c>Execution__Enabled</c>, <see cref="IOptionsMonitor{T}"/> — без рестарта не меняется, но читается
/// при каждом вызове), <c>user_settings.execution_mode</c>, секрету <c>tinvest_trade</c> (status ok И в meta отметка сетевой проверки
/// <see cref="TradeCredentialMeta.NetworkVerification"/> — запись, сохранённая формальным валидатором до задачи 65, НЕ годится), <c>user_settings.execution_paused</c>.
/// </summary>
public sealed class ExecutionGateResolver : IExecutionGateResolver
{
    private readonly IOptionsMonitor<ExecutionOptions> _options;
    private readonly IUserSettingsRepository _settings;
    private readonly IUserCredentialStore _credentials;

    public ExecutionGateResolver(IOptionsMonitor<ExecutionOptions> options, IUserSettingsRepository settings, IUserCredentialStore credentials)
    {
        _options = options;
        _settings = settings;
        _credentials = credentials;
    }

    public async Task<ExecutionGateResult> ResolveAsync(ulong userId)
    {
        var mode = await _settings.GetExecutionModeAsync(userId);
        var paused = await _settings.IsExecutionPausedAsync(userId);
        var statuses = await _credentials.ListStatusesAsync(userId);
        var trade = statuses.FirstOrDefault(s => s.Provider == CredentialProviders.TInvestTrade);
        var credentialOk = trade is { Status: CredentialStatus.Ok } && TradeCredentialMeta.IsNetworkVerified(trade.MetaJson);
        return ExecutionGate.Evaluate(_options.CurrentValue.Enabled, mode, credentialOk, paused);
    }
}

/// <summary>Несекретный meta торгового токена (<c>user_credentials.meta_json</c>): маска счёта, уровень доступа и отметка сетевой проверки.</summary>
public static class TradeCredentialMeta
{
    public const string NetworkVerification = "network";

    public static string Build(string accountId) => JsonSerializer.Serialize(new
    {
        accountIdLast4 = accountId.Length >= 4 ? accountId[^4..] : accountId,
        accessLevel = "full",
        verification = NetworkVerification,
    });

    public static bool IsNetworkVerified(string? metaJson)
    {
        if (string.IsNullOrWhiteSpace(metaJson)) return false;
        try
        {
            using var doc = JsonDocument.Parse(metaJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("verification", out var v) && v.ValueKind == JsonValueKind.String && v.GetString() == NetworkVerification
                   && doc.RootElement.TryGetProperty("accessLevel", out var a) && a.ValueKind == JsonValueKind.String && a.GetString() == "full";
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

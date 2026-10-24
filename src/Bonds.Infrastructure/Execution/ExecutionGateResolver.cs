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
    /// <summary>Ворота T-Invest (торговый токен <c>tinvest_trade</c>).</summary>
    Task<ExecutionGateResult> ResolveAsync(ulong userId);

    /// <summary>
    /// Ворота брокера (задача 66): T-Invest — <see cref="ResolveAsync(ulong)"/>, Freedom — те же env/режим/пауза и ключ <c>freedom_trade</c>. Реализация по умолчанию
    /// (двойники тестов): T-Invest как есть, любой другой брокер ЗАКРЫТ.
    /// </summary>
    Task<ExecutionGateResult> ResolveForBrokerAsync(ulong userId, string broker) =>
        string.Equals(broker, Bonds.Core.Models.Brokers.TInvest, StringComparison.OrdinalIgnoreCase)
            ? ResolveAsync(userId)
            : Task.FromResult(ExecutionGate.Evaluate(false, null, false, false, "Исполнение для брокера " + broker + " не настроено"));
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

    /// <summary>
    /// Freedom: ключ <c>freedom_trade</c> в статусе ok, прошёл сетевую сверку (<see cref="FreedomTradeCredentialMeta"/>) И его счёт совпадает со счётом ключа на чтение
    /// (<c>freedom</c>, тоже ok) — ключ на чтение могли заменить на другой счёт. Неизвестный брокер — закрыто.
    /// </summary>
    public async Task<ExecutionGateResult> ResolveForBrokerAsync(ulong userId, string broker)
    {
        if (string.Equals(broker, Bonds.Core.Models.Brokers.TInvest, StringComparison.OrdinalIgnoreCase)) return await ResolveAsync(userId);
        if (!string.Equals(broker, Bonds.Core.Models.Brokers.Freedom, StringComparison.OrdinalIgnoreCase))
            return ExecutionGate.Evaluate(false, null, false, false, "Исполнение для брокера " + broker + " не настроено");

        var mode = await _settings.GetExecutionModeAsync(userId);
        var paused = await _settings.IsExecutionPausedAsync(userId);
        var statuses = await _credentials.ListStatusesAsync(userId);
        var trade = statuses.FirstOrDefault(s => s.Provider == CredentialProviders.FreedomTrade);
        var read = statuses.FirstOrDefault(s => s.Provider == CredentialProviders.Freedom);
        var tradeAccount = trade is { Status: CredentialStatus.Ok } ? FreedomTradeCredentialMeta.AccountNumberOf(trade.MetaJson) : null;
        var readAccount = read is { Status: CredentialStatus.Ok } ? FreedomTradeCredentialMeta.ReadKeyAccountNumber(read.MetaJson) : null;
        var credentialOk = tradeAccount is not null && readAccount is not null && string.Equals(tradeAccount, readAccount, StringComparison.Ordinal);
        return ExecutionGate.Evaluate(_options.CurrentValue.Enabled, mode, credentialOk, paused, ExecutionGate.NoFreedomTradeCredential);
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

/// <summary>
/// Несекретный meta торгового ключа Freedom (<c>user_credentials.meta_json</c>, задача 66): номер счёта (<c>trader_systems_id</c>, как у ключа на чтение), тариф,
/// отметка сетевой проверки и честное <c>tradeRight: "unverified"</c> — право торговать проверится на первой заявке.
/// </summary>
public static class FreedomTradeCredentialMeta
{
    public const string TradeRightNote = "торговое право проверится на первой заявке";

    public static string Build(string accountNumber, int tariffId) => JsonSerializer.Serialize(new
    {
        accountNumber,
        tariffId,
        verification = TradeCredentialMeta.NetworkVerification,
        tradeRight = "unverified",
        note = TradeRightNote,
    });

    /// <summary>Сетевая проверка пройдена: отметка <c>verification = network</c> и непустой номер счёта.</summary>
    public static bool IsNetworkVerified(string? metaJson) => AccountNumberOf(metaJson) is not null;

    /// <summary>Номер счёта из meta торгового ключа, если запись прошла сетевую проверку; иначе null.</summary>
    public static string? AccountNumberOf(string? metaJson)
    {
        var root = Parse(metaJson);
        if (root is not { } r) return null;
        if (!r.TryGetProperty("verification", out var v) || v.ValueKind != JsonValueKind.String || v.GetString() != TradeCredentialMeta.NetworkVerification) return null;
        return ReadAccountNumber(r);
    }

    /// <summary>Номер счёта (<c>accountNumber</c>) из meta ключа Freedom НА ЧТЕНИЕ (валидатор <c>freedom</c>), null — нет.</summary>
    public static string? ReadKeyAccountNumber(string? metaJson) => Parse(metaJson) is { } r ? ReadAccountNumber(r) : null;

    /// <summary>Тариф (<c>tariffId</c>) из meta ключа Freedom, null — нет.</summary>
    public static int? TariffIdOf(string? metaJson)
    {
        if (Parse(metaJson) is not { } r || !r.TryGetProperty("tariffId", out var t)) return null;
        if (t.ValueKind == JsonValueKind.Number && t.TryGetInt32(out var n)) return n;
        return t.ValueKind == JsonValueKind.String && int.TryParse(t.GetString(), out var s) ? s : null;
    }

    private static JsonElement? Parse(string? metaJson)
    {
        if (string.IsNullOrWhiteSpace(metaJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(metaJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadAccountNumber(JsonElement root)
    {
        if (!root.TryGetProperty("accountNumber", out var a)) return null;
        var value = a.ValueKind switch
        {
            JsonValueKind.String => a.GetString(),
            JsonValueKind.Number => a.GetRawText(),
            _ => null,
        };
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}

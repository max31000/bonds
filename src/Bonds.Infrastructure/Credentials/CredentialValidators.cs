using Bonds.Core.Credentials;
using Bonds.Infrastructure.Connectors.TInvest;

namespace Bonds.Infrastructure.Credentials;

internal static class CredentialFieldReader
{
    /// <summary>Значение поля либо null, если его нет/пусто/содержит пробельные символы внутри.</summary>
    public static string? SingleToken(IReadOnlyDictionary<string, string> fields, string name)
    {
        if (!fields.TryGetValue(name, out var raw) || string.IsNullOrWhiteSpace(raw)) return null;
        var value = raw.Trim();
        return value.Any(char.IsWhiteSpace) ? null : value;
    }
}

/// <summary>
/// <c>tinvest_read</c>: обёртка существующего <see cref="ITInvestTokenValidator"/> (пробный gRPC-вызов), поведение не меняется.
/// Несекретный meta — маска Id счёта; полный Id счёта уходит в <see cref="CredentialValidationResult.BrokerAccountId"/>
/// (для привязки локального счёта), но не сохраняется.
/// </summary>
public sealed class TInvestReadCredentialValidator : ICredentialValidator
{
    private readonly ITInvestTokenValidator _inner;

    public TInvestReadCredentialValidator(ITInvestTokenValidator inner) => _inner = inner;

    public string ProviderId => CredentialProviders.TInvestRead;

    public async Task<CredentialValidationResult> ValidateAsync(IReadOnlyDictionary<string, string> fields, CancellationToken ct = default)
    {
        var token = CredentialFieldReader.SingleToken(fields, "token");
        if (token is null) return CredentialValidationResult.Invalid("Токен не должен быть пустым или содержать пробелы.");

        var result = await _inner.ValidateAsync(token, ct);
        if (!result.IsValid)
        {
            return CredentialValidationResult.Invalid(result.ErrorMessage ?? "Токен не прошёл проверку T-Invest");
        }

        var accountId = result.AccountId ?? string.Empty;
        var accountLast4 = accountId.Length >= 4 ? accountId[^4..] : accountId;
        var meta = string.IsNullOrEmpty(accountLast4)
            ? null
            : System.Text.Json.JsonSerializer.Serialize(new { accountIdLast4 = accountLast4 });
        return CredentialValidationResult.Valid(meta, brokerAccountId: result.AccountId);
    }
}

/// <summary>
/// <c>tinvest_trade</c>: только формальная проверка (непустой, без пробелов). Провайдер Experimental.
/// TODO(фаза 3): сетевая проверка прав токена на торговлю — см. docs/2026-10-23-capital-automation-design.md §5.
/// </summary>
public sealed class TInvestTradeCredentialValidator : ICredentialValidator
{
    public string ProviderId => CredentialProviders.TInvestTrade;

    public Task<CredentialValidationResult> ValidateAsync(IReadOnlyDictionary<string, string> fields, CancellationToken ct = default) =>
        Task.FromResult(CredentialFieldReader.SingleToken(fields, "token") is null
            ? CredentialValidationResult.Invalid("Токен не должен быть пустым или содержать пробелы.")
            : CredentialValidationResult.Valid());
}

/// <summary>
/// <c>claude_code</c>: формальная проверка токена из <c>claude setup-token</c> (подписка Claude, не API-ключ) —
/// непустой, без пробелов, длина не менее <see cref="MinLength"/>, префикс <see cref="Prefix"/>. В сеть не ходит.
/// </summary>
public sealed class ClaudeCodeCredentialValidator : ICredentialValidator
{
    public const string Prefix = "sk-ant-oat";
    public const int MinLength = 20;

    public string ProviderId => CredentialProviders.ClaudeCode;

    public Task<CredentialValidationResult> ValidateAsync(IReadOnlyDictionary<string, string> fields, CancellationToken ct = default)
    {
        var token = CredentialFieldReader.SingleToken(fields, "oauthToken");
        if (token is null)
        {
            return Task.FromResult(CredentialValidationResult.Invalid("Токен не должен быть пустым или содержать пробелы."));
        }

        if (token.Length < MinLength || !token.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return Task.FromResult(CredentialValidationResult.Invalid(
                "Ожидается токен из `claude setup-token` (начинается с sk-ant-oat)."));
        }

        return Task.FromResult(CredentialValidationResult.Valid());
    }
}

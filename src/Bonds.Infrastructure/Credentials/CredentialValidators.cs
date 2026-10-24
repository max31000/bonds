using Bonds.Core.Credentials;
using Bonds.Infrastructure.Connectors.TInvest;
using Bonds.Infrastructure.Execution;

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
/// <c>tinvest_trade</c> (задача 65): СЕТЕВАЯ проверка через <see cref="ITInvestOrderGateway.ProbeAccountsAsync"/> (<c>GetAccounts</c> по введённому токену). Валиден,
/// только если токен видит РОВНО ОДИН счёт, у него <c>ACCOUNT_ACCESS_LEVEL_FULL_ACCESS</c>, счёт открыт и совпадает с основным счётом пользователя
/// (<c>accounts.broker_account_id</c>). В meta — только последние 4 символа счёта, уровень доступа и отметка сетевой проверки
/// (<see cref="TradeCredentialMeta"/>; без неё ворота исполнения токен не принимают). Без контекста пользователя — невалиден (счёт не с чем сверить).
/// </summary>
public sealed class TInvestTradeCredentialValidator : ICredentialValidator
{
    private readonly ITInvestOrderGateway _gateway;

    public TInvestTradeCredentialValidator(ITInvestOrderGateway gateway) => _gateway = gateway;

    public string ProviderId => CredentialProviders.TInvestTrade;

    public Task<CredentialValidationResult> ValidateAsync(IReadOnlyDictionary<string, string> fields, CancellationToken ct = default) =>
        Task.FromResult(CredentialValidationResult.Invalid("Проверка торгового токена требует основного счёта пользователя."));

    public async Task<CredentialValidationResult> ValidateAsync(
        IReadOnlyDictionary<string, string> fields, CredentialValidationContext context, CancellationToken ct = default)
    {
        var token = CredentialFieldReader.SingleToken(fields, "token");
        if (token is null) return CredentialValidationResult.Invalid("Токен не должен быть пустым или содержать пробелы.");
        if (string.IsNullOrWhiteSpace(context.PrimaryTInvestBrokerAccountId))
            return CredentialValidationResult.Invalid("Сначала подключите read-only токен: основной счёт T-Invest не определён.");

        IReadOnlyList<TInvestTradeAccount> accounts;
        try
        {
            accounts = await _gateway.ProbeAccountsAsync(token, ct);
        }
        catch (TInvestGatewayException ex)
        {
            return CredentialValidationResult.Invalid(ex.Code is "Unauthenticated" or "PermissionDenied"
                ? "Токен не принят T-Invest (недействителен или отозван)."
                : "T-Invest недоступен для проверки токена — попробуйте позже.");
        }

        return Evaluate(accounts, context.PrimaryTInvestBrokerAccountId!);
    }

    /// <summary>Правила проверки по списку счетов токена (чистая функция — закреплена тестами).</summary>
    public static CredentialValidationResult Evaluate(IReadOnlyList<TInvestTradeAccount> accounts, string primaryBrokerAccountId)
    {
        if (accounts.Count == 0) return CredentialValidationResult.Invalid("Токен не видит ни одного счёта.");
        if (accounts.Count > 1) return CredentialValidationResult.Invalid("Токен на все счета — выпустите токен на один счёт (основной).");
        var account = accounts[0];
        if (!account.FullAccess) return CredentialValidationResult.Invalid("Нет прав на торговлю: выпустите токен с полным доступом на основной счёт.");
        if (!account.Open) return CredentialValidationResult.Invalid("Счёт токена закрыт.");
        if (!string.Equals(account.Id, primaryBrokerAccountId, StringComparison.Ordinal))
            return CredentialValidationResult.Invalid($"Токен выпущен на другой счёт (…{Last4(account.Id)}), основной счёт — …{Last4(primaryBrokerAccountId)}.");
        return CredentialValidationResult.Valid(TradeCredentialMeta.Build(account.Id));
    }

    private static string Last4(string id) => id.Length >= 4 ? id[^4..] : id;
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

/// <summary>
/// <c>freedom</c>: пробный вызов <c>GetAllUserTexInfo</c> с введёнными ключами. Ok → meta
/// <c>{ accountNumber, tariffId, tariffName }</c> (несекретно), номер счёта также в
/// <see cref="CredentialValidationResult.BrokerAccountId"/>. Сообщение об ошибке не содержит ключей.
/// </summary>
public sealed class FreedomCredentialValidator : ICredentialValidator
{
    private readonly Bonds.Infrastructure.Connectors.Freedom.IFreedomApiClient _client;

    public FreedomCredentialValidator(Bonds.Infrastructure.Connectors.Freedom.IFreedomApiClient client) => _client = client;

    public string ProviderId => CredentialProviders.Freedom;

    public async Task<CredentialValidationResult> ValidateAsync(IReadOnlyDictionary<string, string> fields, CancellationToken ct = default)
    {
        var pub = CredentialFieldReader.SingleToken(fields, "publicKey");
        var priv = CredentialFieldReader.SingleToken(fields, "privateKey");
        if (pub is null || priv is null)
        {
            return CredentialValidationResult.Invalid("Оба ключа должны быть заданы, без пробелов.");
        }

        try
        {
            var info = await _client.GetUserInfoAsync(new Bonds.Infrastructure.Connectors.Freedom.FreedomCredentials(pub, priv), ct);
            var meta = System.Text.Json.JsonSerializer.Serialize(new
            {
                accountNumber = info.AccountNumber,
                tariffId = info.TariffId,
                tariffName = info.TariffName,
            });
            return CredentialValidationResult.Valid(meta, brokerAccountId: info.AccountNumber);
        }
        catch (Bonds.Infrastructure.Connectors.Freedom.FreedomApiException)
        {
            // Текст брокера не пробрасываем: безопасное фиксированное сообщение.
            return CredentialValidationResult.Invalid("Freedom не принял ключи. Проверьте публичный и секретный ключ.");
        }
    }
}

/// <summary>
/// <c>freedom_trade</c> (задача 66): СЕТЕВАЯ проверка — <c>GetAllUserTexInfo</c> с введёнными ключами должен вернуть тот же <c>trader_systems_id</c>, что ПРОВЕРЕННЫЙ
/// ключ на чтение (<c>freedom</c>, meta <c>accountNumber</c>). Право торговать без реальной заявки проверить нельзя — в meta честная отметка
/// <c>tradeRight: "unverified"</c> (UI: «торговое право проверится на первой заявке»). Без ключа на чтение — невалиден (счёт не с чем сверить).
/// Сообщения об ошибках не содержат ключей.
/// </summary>
public sealed class FreedomTradeCredentialValidator : ICredentialValidator
{
    private readonly Bonds.Infrastructure.Connectors.Freedom.IFreedomApiClient _client;

    public FreedomTradeCredentialValidator(Bonds.Infrastructure.Connectors.Freedom.IFreedomApiClient client) => _client = client;

    public string ProviderId => CredentialProviders.FreedomTrade;

    public Task<CredentialValidationResult> ValidateAsync(IReadOnlyDictionary<string, string> fields, CancellationToken ct = default) =>
        Task.FromResult(CredentialValidationResult.Invalid("Проверка торгового ключа Freedom требует проверенного ключа на чтение."));

    public async Task<CredentialValidationResult> ValidateAsync(
        IReadOnlyDictionary<string, string> fields, CredentialValidationContext context, CancellationToken ct = default)
    {
        var pub = CredentialFieldReader.SingleToken(fields, "publicKey");
        var priv = CredentialFieldReader.SingleToken(fields, "privateKey");
        if (pub is null || priv is null) return CredentialValidationResult.Invalid("Оба ключа должны быть заданы, без пробелов.");
        if (string.IsNullOrWhiteSpace(context.FreedomReadAccountNumber))
            return CredentialValidationResult.Invalid("Сначала подключите ключ Freedom на чтение: торговый ключ сверяется с его счётом.");

        Bonds.Infrastructure.Connectors.Freedom.Models.FreedomUserInfo info;
        try
        {
            info = await _client.GetUserInfoAsync(new Bonds.Infrastructure.Connectors.Freedom.FreedomCredentials(pub, priv), ct);
        }
        catch (Bonds.Infrastructure.Connectors.Freedom.FreedomApiException)
        {
            return CredentialValidationResult.Invalid("Freedom не принял торговые ключи. Проверьте публичный и секретный ключ.");
        }

        return Evaluate(info.AccountNumber, info.TariffId, context.FreedomReadAccountNumber!);
    }

    /// <summary>Правило сверки счёта (чистая функция — закреплена тестами): тот же <c>trader_systems_id</c>, что у ключа на чтение.</summary>
    public static CredentialValidationResult Evaluate(string? tradeAccountNumber, int tariffId, string readAccountNumber)
    {
        var trade = tradeAccountNumber?.Trim();
        if (string.IsNullOrEmpty(trade)) return CredentialValidationResult.Invalid("Freedom не вернул номер счёта для торгового ключа.");
        if (!string.Equals(trade, readAccountNumber.Trim(), StringComparison.Ordinal))
            return CredentialValidationResult.Invalid($"Торговый ключ от другого счёта (…{Last4(trade)}), ключ на чтение — от счёта …{Last4(readAccountNumber.Trim())}.");
        return CredentialValidationResult.Valid(FreedomTradeCredentialMeta.Build(trade, tariffId));
    }

    private static string Last4(string id) => id.Length >= 4 ? id[^4..] : id;
}

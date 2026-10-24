namespace Bonds.Core.Credentials;

/// <summary>
/// Проверка секрета провайдера ПЕРЕД сохранением (невалидный не сохраняется). Реализация на каждого
/// провайдера реестра; регистрируется в DI как <see cref="ICredentialValidator"/>, выбирается по <see cref="ProviderId"/>.
/// Не бросает на «плохой секрет» — возвращает <see cref="CredentialValidationResult"/> с человекочитаемым
/// сообщением БЕЗ значения секрета.
/// </summary>
public interface ICredentialValidator
{
    string ProviderId { get; }

    Task<CredentialValidationResult> ValidateAsync(IReadOnlyDictionary<string, string> fields, CancellationToken ct = default);

    /// <summary>
    /// Проверка с контекстом пользователя (задача 65: торговому токену нужно сверить счёт с основным счётом пользователя). По умолчанию контекст не нужен —
    /// вызывается проверка без него. <c>PUT /api/credentials/{provider}</c> всегда вызывает ЭТУ перегрузку.
    /// </summary>
    Task<CredentialValidationResult> ValidateAsync(IReadOnlyDictionary<string, string> fields, CredentialValidationContext context, CancellationToken ct = default) =>
        ValidateAsync(fields, ct);
}

/// <summary>
/// Контекст проверки секрета: пользователь и Id брокерского счёта его ОСНОВНОГО счёта T-Invest (<c>accounts.broker_account_id</c>, null — не привязан);
/// <paramref name="FreedomReadAccountNumber"/> — номер счёта (<c>trader_systems_id</c>) из ПРОВЕРЕННОГО ключа Freedom на чтение (meta <c>accountNumber</c>, задача 66),
/// null — ключа на чтение нет или он не в статусе ok.
/// </summary>
public sealed record CredentialValidationContext(ulong UserId, string? PrimaryTInvestBrokerAccountId, string? FreedomReadAccountNumber = null);

/// <summary>
/// Результат валидации. <see cref="MetaJson"/> — НЕсекретные данные для UI (тариф, scope), сохраняются в
/// <c>meta_json</c>. <see cref="Last4"/> — переопределение маски (иначе берётся из mask-source-поля).
/// <see cref="BrokerAccountId"/> — только для вызывающего кода (привязка счёта), не сохраняется и не отдаётся в API.
/// </summary>
public sealed record CredentialValidationResult(
    bool Ok,
    string? Message = null,
    string? MetaJson = null,
    string? Last4 = null,
    string? BrokerAccountId = null)
{
    public static CredentialValidationResult Valid(string? metaJson = null, string? last4 = null, string? brokerAccountId = null) =>
        new(true, null, metaJson, last4, brokerAccountId);

    public static CredentialValidationResult Invalid(string message) => new(false, message);
}

namespace Bonds.Infrastructure.Connectors.TInvest;

/// <summary>
/// Пробный вызов T-Invest с явным (ещё не сохранённым) токеном — plan/13 часть C:
/// <c>PUT /api/settings/tinvest-token</c> должен проверить токен ДО сохранения, чтобы невалидный
/// токен не попадал в БД молча (пользователь узнавал бы об этом только по деградировавшему синку,
/// та же тишина, что и корневой баг протухших ключей DataProtection). Отдельный интерфейс (а не
/// расширение <see cref="ITInvestPortfolioClient"/>) — тот берёт токен ТОЛЬКО из
/// <c>ITInvestTokenProvider</c> (БД на пользователя, см. его doc-comment), тогда как здесь токен —
/// явный аргумент вызова, ещё не сохранённый нигде.
/// </summary>
public interface ITInvestTokenValidator
{
    /// <summary>
    /// Делает пробный вызов T-Invest с переданным токеном (получение счетов). Не бросает исключение
    /// на невалидном токене/сетевой ошибке — возвращает результат с человекочитаемым сообщением,
    /// чтобы вызывающий эндпоинт мог ответить 422 без try/catch вокруг чужого протокола ошибок.
    /// </summary>
    Task<TInvestTokenValidationResult> ValidateAsync(string token, CancellationToken ct = default);
}

/// <summary>Результат <see cref="ITInvestTokenValidator.ValidateAsync"/>.</summary>
public sealed record TInvestTokenValidationResult
{
    public required bool IsValid { get; init; }

    /// <summary>Id брокерского счёта, привязанного к токену — только при <see cref="IsValid"/> = true.</summary>
    public string? AccountId { get; init; }

    /// <summary>Человекочитаемая причина отказа — только при <see cref="IsValid"/> = false. Без токена внутри (spec §11).</summary>
    public string? ErrorMessage { get; init; }

    public static TInvestTokenValidationResult Valid(string accountId) => new() { IsValid = true, AccountId = accountId };

    public static TInvestTokenValidationResult Invalid(string errorMessage) => new() { IsValid = false, ErrorMessage = errorMessage };
}

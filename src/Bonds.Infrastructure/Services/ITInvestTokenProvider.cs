namespace Bonds.Infrastructure.Services;

/// <summary>
/// Источник токена T-Invest для синка (plan/00 §7, plan/08 "Токен T-Invest через UI") —
/// единая точка, через которую <c>TInvestPortfolioClient</c> резолвит токен. Используется и
/// эндпоинтом настроек для определения статуса "задан/не задан"/валидации нового токена перед
/// сохранением, и самим коннектором T-Invest при построении gRPC-клиента.
/// <para>
/// Токен хранится ТОЛЬКО в БД на аккаунт (явное решение владельца продукта) — без ENV-фолбэка.
/// Секреты CI/деплоя (GitHub Secrets) не содержат T-Invest токен; он заводится исключительно
/// через <c>PUT /api/settings/tinvest-token</c>.
/// </para>
/// </summary>
public interface ITInvestTokenProvider
{
    /// <summary>
    /// Возвращает токен, сохранённый в БД на аккаунт. Null, если токен не задан (например,
    /// чистая инсталляция до первого онбординга) или не удалось расшифровать сохранённое значение.
    /// </summary>
    Task<string?> GetTokenAsync(CancellationToken ct = default);
}

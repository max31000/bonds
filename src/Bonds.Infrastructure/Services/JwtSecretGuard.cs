namespace Bonds.Infrastructure.Services;

/// <summary>
/// Стартовая проверка Jwt:Secret вне Development/Testing: секрет обязан быть задан через
/// окружение (Jwt__Secret), быть не короче 32 символов и не совпадать с dev-значением из
/// закоммиченного appsettings.json — иначе любой, кто видел репозиторий, может подписать токен.
/// </summary>
public static class JwtSecretGuard
{
    /// <summary>Dev-секрет из appsettings.json — в проде запрещён.</summary>
    public const string CommittedDevSecret = "dev-secret-change-me-min-32-characters!!";

    public const int MinLength = 32;

    /// <summary>
    /// Dev-секреты, когда-либо закоммиченные в appsettings.json проектов cashpulse / bonds / credit_calc.
    /// Любой из них (а также любое значение, содержащее "dev-secret") в проде запрещён.
    /// </summary>
    public static readonly string[] KnownDevSecrets =
    [
        "dev-secret-change-in-production-min-32-chars!!",
        "dev-secret-change-me-min-32-characters!!",
        "dev-only-secret-key-do-not-use-in-prod-32chars",
    ];

    public static bool IsKnownDevSecret(string secret) =>
        KnownDevSecrets.Contains(secret, StringComparer.Ordinal)
        || secret.Contains("dev-secret", StringComparison.OrdinalIgnoreCase);

    public static void EnsureProductionSafe(string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret) || secret.Length < MinLength || IsKnownDevSecret(secret))
        {
            throw new InvalidOperationException(
                $"Jwt:Secret must be configured (env Jwt__Secret) with at least {MinLength} characters " +
                "and must not equal the committed development value.");
        }
    }
}

using Bonds.Core.Interfaces.Repositories;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Bonds.Infrastructure.Services;

/// <summary>
/// Реализация <see cref="ITInvestTokenProvider"/>: расшифровывает токен пользователя из
/// <see cref="IUserSettingsRepository"/> (если задан) через <see cref="IDataProtectionProvider"/>,
/// иначе фолбэк на ENV/appsettings (TInvest:Token). Single-user продукт (spec §2) — "пользователь"
/// определяется так же, как и везде (<see cref="IUserRepository.GetPrimaryUserIdAsync"/> — тот же
/// паттерн, что <see cref="IAccountRepository.GetPrimaryAccountIdAsync"/> использует для Account).
/// </summary>
public sealed class TInvestTokenProvider : ITInvestTokenProvider
{
    /// <summary>"Purpose" протектора DataProtection — стабильная строка, меняющая её сделает
    /// существующие зашифрованные токены нерасшифровываемыми (намеренно версионирована .v1).</summary>
    public const string ProtectorPurpose = "Bonds.TInvestToken.v1";

    private readonly IUserSettingsRepository _settingsRepo;
    private readonly IUserRepository _userRepo;
    private readonly IDataProtectionProvider _dataProtection;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TInvestTokenProvider> _logger;

    public TInvestTokenProvider(
        IUserSettingsRepository settingsRepo,
        IUserRepository userRepo,
        IDataProtectionProvider dataProtection,
        IConfiguration configuration,
        ILogger<TInvestTokenProvider> logger)
    {
        _settingsRepo = settingsRepo;
        _userRepo = userRepo;
        _dataProtection = dataProtection;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<string?> GetTokenAsync(CancellationToken ct = default)
    {
        var userId = await _userRepo.GetPrimaryUserIdAsync();
        if (userId is not null)
        {
            var settings = await _settingsRepo.GetByUserIdAsync(userId.Value);
            if (!string.IsNullOrEmpty(settings?.TInvestTokenEncrypted))
            {
                var protector = _dataProtection.CreateProtector(ProtectorPurpose);
                try
                {
                    return protector.Unprotect(settings.TInvestTokenEncrypted);
                }
                catch (Exception ex)
                {
                    // Не удалось расшифровать (например, сменились ключи DataProtection) — деградация
                    // на ENV, а не падение (spec §4.4 "деградация с пометками вместо падения").
                    // Само исключение/сообщение не содержит токен — безопасно логировать (spec §11).
                    _logger.LogWarning(ex, "Failed to decrypt stored T-Invest token — falling back to ENV/config");
                }
            }
        }

        return _configuration["TInvest:Token"];
    }
}

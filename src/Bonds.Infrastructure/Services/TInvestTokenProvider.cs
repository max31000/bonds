using Bonds.Core.Interfaces.Repositories;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;

namespace Bonds.Infrastructure.Services;

/// <summary>
/// Реализация <see cref="ITInvestTokenProvider"/>: расшифровывает токен пользователя из
/// <see cref="IUserSettingsRepository"/> через <see cref="IDataProtectionProvider"/>. Токен хранится
/// ТОЛЬКО в БД на аккаунт — без ENV/appsettings-фолбэка (явное решение владельца продукта: токен
/// заводится исключительно через <c>PUT /api/settings/tinvest-token</c>, секреты CI/деплоя его не
/// содержат). Single-user продукт (spec §2) — "пользователь" определяется так же, как и везде
/// (<see cref="IUserRepository.GetPrimaryUserIdAsync"/> — тот же паттерн, что
/// <see cref="IAccountRepository.GetPrimaryAccountIdAsync"/> использует для Account).
/// </summary>
public sealed class TInvestTokenProvider : ITInvestTokenProvider
{
    /// <summary>"Purpose" протектора DataProtection — стабильная строка, меняющая её сделает
    /// существующие зашифрованные токены нерасшифровываемыми (намеренно версионирована .v1).</summary>
    public const string ProtectorPurpose = "Bonds.TInvestToken.v1";

    private readonly IUserSettingsRepository _settingsRepo;
    private readonly IUserRepository _userRepo;
    private readonly IDataProtectionProvider _dataProtection;
    private readonly ILogger<TInvestTokenProvider> _logger;

    public TInvestTokenProvider(
        IUserSettingsRepository settingsRepo,
        IUserRepository userRepo,
        IDataProtectionProvider dataProtection,
        ILogger<TInvestTokenProvider> logger)
    {
        _settingsRepo = settingsRepo;
        _userRepo = userRepo;
        _dataProtection = dataProtection;
        _logger = logger;
    }

    public async Task<string?> GetTokenAsync(CancellationToken ct = default)
    {
        var userId = await _userRepo.GetPrimaryUserIdAsync();
        if (userId is null)
        {
            return null;
        }

        var settings = await _settingsRepo.GetByUserIdAsync(userId.Value);
        if (string.IsNullOrEmpty(settings?.TInvestTokenEncrypted))
        {
            return null;
        }

        var protector = _dataProtection.CreateProtector(ProtectorPurpose);
        try
        {
            return protector.Unprotect(settings.TInvestTokenEncrypted);
        }
        catch (Exception ex)
        {
            // Не удалось расшифровать (например, сменились ключи DataProtection) — возвращаем null,
            // вызывающий код (BondSyncService/SyncCycleService) деградирует на этом шаге синка без
            // падения всего цикла (spec §4.4). Само исключение/сообщение не содержит токен — безопасно
            // логировать (spec §11).
            _logger.LogWarning(ex, "Failed to decrypt stored T-Invest token");
            return null;
        }
    }
}

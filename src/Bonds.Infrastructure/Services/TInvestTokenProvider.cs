using Bonds.Core.Credentials;
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

    private readonly IUserCredentialStore _credentialStore;
    private readonly IUserSettingsRepository _settingsRepo;
    private readonly IUserRepository _userRepo;
    private readonly IDataProtectionProvider _dataProtection;
    private readonly ILogger<TInvestTokenProvider> _logger;
    private ulong? _userId;

    public TInvestTokenProvider(
        IUserCredentialStore credentialStore,
        IUserSettingsRepository settingsRepo,
        IUserRepository userRepo,
        IDataProtectionProvider dataProtection,
        ILogger<TInvestTokenProvider> logger)
    {
        _credentialStore = credentialStore;
        _settingsRepo = settingsRepo;
        _userRepo = userRepo;
        _dataProtection = dataProtection;
        _logger = logger;
    }

    /// <inheritdoc />
    public void UseUser(ulong userId) => _userId = userId;

    public async Task<string?> GetTokenAsync(CancellationToken ct = default)
    {
        var (_, token) = await ResolveAsync();
        return token;
    }

    public async Task<TInvestTokenStatus> GetTokenStatusAsync(CancellationToken ct = default)
    {
        var (status, _) = await ResolveAsync();
        return status;
    }

    /// <summary>
    /// Единая точка резолва — и <see cref="GetTokenAsync"/>, и <see cref="GetTokenStatusAsync"/>
    /// ("различать эти два случая текстом ошибки") идут через неё, чтобы не
    /// дублировать логику расшифровки/её try-catch.
    /// </summary>
    private async Task<(TInvestTokenStatus Status, string? Token)> ResolveAsync()
    {
        // Задача 67 (#5): скоуп, привязанный к пользователю (синк T-Invest по каждому владельцу tinvest_read), — его токен; иначе основной пользователь.
        var userId = _userId ?? await _userRepo.GetPrimaryUserIdAsync();
        if (userId is null)
        {
            return (TInvestTokenStatus.NotConfigured, null);
        }

        // Задача 58: основной источник — user_credentials (tinvest_read). Миграция 029 скопировала сюда
        // шифротекст из user_settings с тем же purpose, поэтому токен действующего пользователя читается как раньше.
        var stored = await _credentialStore.GetSecretResultAsync(userId.Value, CredentialProviders.TInvestRead);
        switch (stored.Outcome)
        {
            case CredentialReadOutcome.Ok:
                return (TInvestTokenStatus.Valid, stored.Fields!["token"]);
            case CredentialReadOutcome.DecryptionFailed:
                return (TInvestTokenStatus.DecryptionFailed, null);
        }

        // TODO удалить после 029+1: страховка на время выката — записи в user_credentials ещё нет, токен в user_settings.
        var settings = await _settingsRepo.GetByUserIdAsync(userId.Value);
        if (string.IsNullOrEmpty(settings?.TInvestTokenEncrypted))
        {
            return (TInvestTokenStatus.NotConfigured, null);
        }

        var protector = _dataProtection.CreateProtector(ProtectorPurpose);
        try
        {
            return (TInvestTokenStatus.Valid, protector.Unprotect(settings.TInvestTokenEncrypted));
        }
        catch (Exception ex)
        {
            // Не удалось расшифровать (например, сменились ключи DataProtection) — возвращаем null,
            // вызывающий код (BondSyncService/SyncCycleService) деградирует на этом шаге синка без
            // падения всего цикла (spec §4.4). Само исключение/сообщение не содержит токен — безопасно
            // логировать (spec §11).
            _logger.LogWarning(ex, "Failed to decrypt stored T-Invest token");
            return (TInvestTokenStatus.DecryptionFailed, null);
        }
    }
}

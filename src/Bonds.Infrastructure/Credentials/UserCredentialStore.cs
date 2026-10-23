using System.Text.Json;
using Bonds.Core.Credentials;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;

namespace Bonds.Infrastructure.Credentials;

/// <summary>
/// Единственная точка шифрования/дешифрования <c>user_credentials.secret_encrypted</c> (задача 58).
/// <para>
/// <b>Форматы.</b> Обычные провайдеры: purpose протектора <c>bonds.credentials.v1.{provider}</c>, открытый
/// текст — JSON <c>{"field":"value"}</c>. <b>Легаси-исключение <c>tinvest_read</c></b>: миграция 029 копирует
/// <c>user_settings.tinvest_token_encrypted</c> как есть, поэтому здесь purpose тот же, что у
/// <see cref="TInvestTokenProvider.ProtectorPurpose"/>, а открытый текст — ГОЛАЯ строка токена, не JSON
/// (иначе токен текущего пользователя перестал бы расшифровываться).
/// </para>
/// <para>
/// Секреты не логируются; исключения расшифровки не пробрасываются — запись помечается <c>invalid</c>
/// с сообщением «нужно ввести заново».
/// </para>
/// </summary>
public sealed class UserCredentialStore : IUserCredentialStore
{
    private const string PurposePrefix = "bonds.credentials.v1.";
    internal const string ReenterMessage = "Не удалось расшифровать сохранённое значение — нужно ввести заново.";

    /// <summary>provider → (purpose, поле для голой строки). Только легаси-провайдеры с форматом «голая строка токена».</summary>
    private static readonly IReadOnlyDictionary<string, (string Purpose, string Field)> LegacyBare =
        new Dictionary<string, (string, string)>
        {
            [CredentialProviders.TInvestRead] = (TInvestTokenProvider.ProtectorPurpose, "token"),
        };

    private readonly IUserCredentialRepository _repo;
    private readonly IDataProtectionProvider _dataProtection;
    private readonly IUserSettingsRepository _settingsRepo;
    private readonly ILogger<UserCredentialStore> _logger;

    public UserCredentialStore(
        IUserCredentialRepository repo,
        IDataProtectionProvider dataProtection,
        IUserSettingsRepository settingsRepo,
        ILogger<UserCredentialStore> logger)
    {
        _repo = repo;
        _dataProtection = dataProtection;
        _settingsRepo = settingsRepo;
        _logger = logger;
    }

    private IDataProtector ProtectorFor(string provider) =>
        _dataProtection.CreateProtector(
            LegacyBare.TryGetValue(provider, out var legacy) ? legacy.Purpose : PurposePrefix + provider);

    public async Task<IReadOnlyDictionary<string, string>?> GetSecretAsync(ulong userId, string provider) =>
        (await GetSecretResultAsync(userId, provider)).Fields;

    public async Task<CredentialSecretResult> GetSecretResultAsync(ulong userId, string provider)
    {
        var record = await _repo.GetAsync(userId, provider);
        if (record is null || string.IsNullOrEmpty(record.SecretEncrypted))
        {
            return new CredentialSecretResult(CredentialReadOutcome.Missing, null);
        }

        try
        {
            var plain = ProtectorFor(provider).Unprotect(record.SecretEncrypted);
            IReadOnlyDictionary<string, string> fields = LegacyBare.TryGetValue(provider, out var legacy)
                ? new Dictionary<string, string> { [legacy.Field] = plain }
                : JsonSerializer.Deserialize<Dictionary<string, string>>(plain)
                    ?? throw new JsonException("empty secret payload");
            return new CredentialSecretResult(CredentialReadOutcome.Ok, fields);
        }
        catch (Exception ex)
        {
            // Ни сообщение, ни значение секрета не логируем — только провайдер и тип исключения.
            _logger.LogWarning("Failed to decrypt credential {Provider} for user {UserId}: {ExceptionType}",
                provider, userId, ex.GetType().Name);
            if (record.Status != CredentialStatus.Invalid)
            {
                await _repo.UpdateStatusAsync(userId, provider, CredentialStatus.Invalid, ReenterMessage);
            }

            return new CredentialSecretResult(CredentialReadOutcome.DecryptionFailed, null);
        }
    }

    public async Task SaveAsync(
        ulong userId, string provider, IReadOnlyDictionary<string, string> fields, string? last4, string? metaJson)
    {
        var plain = LegacyBare.TryGetValue(provider, out var legacy)
            ? fields[legacy.Field]
            : JsonSerializer.Serialize(fields);

        await _repo.UpsertAsync(new UserCredentialRecord
        {
            UserId = userId,
            Provider = provider,
            SecretEncrypted = ProtectorFor(provider).Protect(plain),
            SecretLast4 = last4,
            MetaJson = metaJson,
            Status = CredentialStatus.Ok,
            StatusMessage = null,
            VerifiedAt = DateTime.UtcNow,
        });
    }

    public async Task DeleteAsync(ulong userId, string provider)
    {
        await _repo.DeleteAsync(userId, provider);

        if (provider == CredentialProviders.TInvestRead)
        {
            // Страховочный fallback TInvestTokenProvider читает user_settings — без очистки удалённый токен «воскрес» бы.
            var settings = await _settingsRepo.GetByUserIdAsync(userId);
            if (settings is not null && (settings.TInvestTokenEncrypted is not null || settings.TInvestTokenLast4 is not null))
            {
                settings.TInvestTokenEncrypted = null;
                settings.TInvestTokenLast4 = null;
                await _settingsRepo.UpsertAsync(settings);
            }
        }
    }

    public async Task<IReadOnlyList<CredentialStatusInfo>> ListStatusesAsync(ulong userId) =>
        (await _repo.ListAsync(userId))
        .Select(r => new CredentialStatusInfo(
            r.Provider, r.Status, r.StatusMessage, r.SecretLast4, r.MetaJson,
            r.VerifiedAt is { } v ? DateTime.SpecifyKind(v, DateTimeKind.Utc) : null))
        .ToList();

    public async Task<bool> HasValidAsync(ulong userId, string provider) =>
        (await _repo.GetAsync(userId, provider))?.Status == CredentialStatus.Ok;
}

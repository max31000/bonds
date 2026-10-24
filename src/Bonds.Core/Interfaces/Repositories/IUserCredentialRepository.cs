namespace Bonds.Core.Interfaces.Repositories;

/// <summary>Строка <c>user_credentials</c> как есть (включая шифротекст — наружу репозитория не выходит иначе как через <c>IUserCredentialStore</c>).</summary>
public sealed class UserCredentialRecord
{
    public ulong Id { get; set; }
    public ulong UserId { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string SecretEncrypted { get; set; } = string.Empty;
    public string? SecretLast4 { get; set; }
    public string? MetaJson { get; set; }
    public string Status { get; set; } = "unverified";
    public string? StatusMessage { get; set; }
    public DateTime? VerifiedAt { get; set; }
}

public interface IUserCredentialRepository
{
    Task<UserCredentialRecord?> GetAsync(ulong userId, string provider);
    Task<IReadOnlyList<UserCredentialRecord>> ListAsync(ulong userId);

    /// <summary>Вставка либо обновление по (user_id, provider).</summary>
    Task UpsertAsync(UserCredentialRecord record);

    /// <summary>Меняет только status/status_message существующей записи.</summary>
    Task UpdateStatusAsync(ulong userId, string provider, string status, string? statusMessage);

    Task DeleteAsync(ulong userId, string provider);

    /// <summary>Меняет только meta_json существующей записи (несекретные метаданные: тариф, номер счёта — задача 67, #6).</summary>
    Task UpdateMetaAsync(ulong userId, string provider, string? metaJson);

    /// <summary>Id пользователей, у которых есть запись провайдера в статусе <paramref name="status"/> (для фоновых синков по всем пользователям).</summary>
    Task<IReadOnlyList<ulong>> ListUserIdsAsync(string provider, string status);
}

namespace Bonds.Core.Credentials;

/// <summary>Исход чтения секрета из <see cref="IUserCredentialStore"/>.</summary>
public enum CredentialReadOutcome
{
    /// <summary>Записи нет.</summary>
    Missing,

    /// <summary>Запись есть и расшифрована.</summary>
    Ok,

    /// <summary>Запись есть, но не расшифровалась (смена ключей DataProtection или порча) — статус записи переведён в invalid.</summary>
    DecryptionFailed,
}

public sealed record CredentialSecretResult(CredentialReadOutcome Outcome, IReadOnlyDictionary<string, string>? Fields);

/// <summary>Несекретное состояние записи (без шифротекста) — то, что можно отдавать наружу.</summary>
public sealed record CredentialStatusInfo(
    string Provider,
    string Status,
    string? StatusMessage,
    string? Last4,
    string? MetaJson,
    DateTime? VerifiedAtUtc);

/// <summary>
/// Единственная точка шифрования/дешифрования секретов пользователя (<c>user_credentials</c>).
/// Всё по <c>userId</c> (из <c>ICurrentUser</c>); секреты наружу отдаёт только <see cref="GetSecretAsync"/>
/// серверному коду — в API они не попадают никогда.
/// </summary>
public interface IUserCredentialStore
{
    /// <summary>Поля секрета (имя → значение) либо null, если записи нет или она не расшифровалась (статус → invalid, исключение не пробрасывается).</summary>
    Task<IReadOnlyDictionary<string, string>?> GetSecretAsync(ulong userId, string provider);

    /// <summary>То же, но различает «нет записи» и «не расшифровалась» (нужно синку для диагностики).</summary>
    Task<CredentialSecretResult> GetSecretResultAsync(ulong userId, string provider);

    /// <summary>Шифрует и сохраняет (upsert) секрет со статусом ok, verified_at = сейчас (UTC).</summary>
    Task SaveAsync(ulong userId, string provider, IReadOnlyDictionary<string, string> fields, string? last4, string? metaJson);

    Task DeleteAsync(ulong userId, string provider);

    /// <summary>Статусы всех сохранённых секретов пользователя (без расшифровки).</summary>
    Task<IReadOnlyList<CredentialStatusInfo>> ListStatusesAsync(ulong userId);

    /// <summary>true — запись есть и status = ok.</summary>
    Task<bool> HasValidAsync(ulong userId, string provider);

    /// <summary>Id всех пользователей с записью провайдера в статусе ok — фоновые синки обходят их без HTTP-контекста (задача 60).</summary>
    Task<IReadOnlyList<ulong>> ListUserIdsWithValidAsync(string provider);

    /// <summary>Обновляет несекретные метаданные записи (задача 67, #6: тариф Freedom на каждом синке). Нет записи — ничего.</summary>
    Task UpdateMetaAsync(ulong userId, string provider, string? metaJson);

    /// <summary>Помечает запись невалидной с сообщением (задача 67, #16: брокер отверг ключ на синке, HTTP 401/403). Нет записи — ничего.</summary>
    Task MarkInvalidAsync(ulong userId, string provider, string message);
}

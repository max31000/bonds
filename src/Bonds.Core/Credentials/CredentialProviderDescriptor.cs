namespace Bonds.Core.Credentials;

/// <summary>Категория провайдера секретов — группировка в UI («Брокеры» / «ИИ»).</summary>
public enum CredentialCategory
{
    Broker,
    Ai,
}

/// <summary>
/// Поле секрета провайдера. <see cref="IsSecret"/> — рисовать как password-input;
/// <see cref="IsMaskSource"/> — из последних 4 символов этого поля строится маска для UI
/// (ровно одно такое поле на провайдера).
/// </summary>
public sealed record CredentialField(string Name, string Title, bool IsSecret, bool IsMaskSource);

/// <summary>
/// Описание провайдера секретов (задача 58). Расширение — новая запись в <see cref="CredentialProviders"/>
/// плюс <see cref="ICredentialValidator"/>, без миграций и новых эндпоинтов.
/// </summary>
/// <param name="Id">Стабильный id (snake_case, не более 48 символов) — хранится в <c>user_credentials.provider</c>.</param>
/// <param name="Unlocks">Человекочитаемо: какой функционал включает этот секрет.</param>
/// <param name="Experimental">Провайдер заведён заранее, функционал ещё не реализован/не доказан.</param>
public sealed record CredentialProviderDescriptor(
    string Id,
    string Title,
    CredentialCategory Category,
    IReadOnlyList<CredentialField> Fields,
    string Unlocks,
    bool Experimental = false)
{
    /// <summary>Поле, из которого строится маска (последние 4 символа).</summary>
    public CredentialField MaskSourceField => Fields.Single(f => f.IsMaskSource);

    /// <summary>Минимальная длина значения, при которой последние 4 символа не раскрывают секрет целиком.</summary>
    public const int MinLengthForMask = 8;

    /// <summary>
    /// Последние 4 символа главного поля — открытая маска для UI (не секрет сама по себе).
    /// Для значений короче <see cref="MinLengthForMask"/> маски нет: иначе маска = весь секрет.
    /// </summary>
    public string? ComputeLast4(IReadOnlyDictionary<string, string> fields)
    {
        if (!fields.TryGetValue(MaskSourceField.Name, out var value) || value.Length < MinLengthForMask) return null;
        return value[^4..];
    }
}

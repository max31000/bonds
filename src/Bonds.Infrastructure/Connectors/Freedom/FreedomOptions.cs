namespace Bonds.Infrastructure.Connectors.Freedom;

/// <summary>Настройки коннектора (секция конфигурации <c>Freedom</c>). Ключи пользователя сюда не кладутся — они в реестре секретов.</summary>
public sealed class FreedomOptions
{
    public const string SectionName = "Freedom";

    /// <summary>Хост API v3. Счёт Freedom Global / KZ живёт на tradernet.global (SDK по умолчанию — freedom24.com).</summary>
    public string BaseUrl { get; set; } = "https://tradernet.global";

    public int TimeoutSeconds { get; set; } = 30;
}

/// <summary>
/// Пара ключей API v3. Передаётся в КАЖДЫЙ вызов клиента (клиент без состояния, мультипользовательский).
/// <see cref="ToString"/> скрывает значения, чтобы ключи не попали в логи через record-форматирование.
/// </summary>
public sealed record FreedomCredentials(string PublicKey, string PrivateKey)
{
    public override string ToString() => "FreedomCredentials { *** }";
}

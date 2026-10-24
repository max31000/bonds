namespace Bonds.Core.Credentials;

/// <summary>
/// Реестр провайдеров секретов — ТОЛЬКО известные сейчас (Freedom — задача 59). Каждый секрет включает свою часть функционала
/// (см. <c>GET /api/capabilities</c>).
/// </summary>
public static class CredentialProviders
{
    public const string TInvestRead = "tinvest_read";
    public const string TInvestTrade = "tinvest_trade";
    public const string ClaudeCode = "claude_code";
    public const string Freedom = "freedom";

    /// <summary>Торговый ключ Freedom (задача 66): отдельная пара ключей только для исполнителя; ключ на чтение <see cref="Freedom"/> продолжает служить синку.</summary>
    public const string FreedomTrade = "freedom_trade";

    public static IReadOnlyList<CredentialProviderDescriptor> All { get; } = new[]
    {
        new CredentialProviderDescriptor(
            TInvestRead, "T-Invest (чтение)", CredentialCategory.Broker,
            new[] { new CredentialField("token", "Read-only токен", IsSecret: true, IsMaskSource: true) },
            Unlocks: "синхронизация портфеля и операций"),
        new CredentialProviderDescriptor(
            TInvestTrade, "T-Invest (торговля)", CredentialCategory.Broker,
            new[] { new CredentialField("token", "Торговый токен", IsSecret: true, IsMaskSource: true) },
            Unlocks: "исполнение сделок, фаза 3",
            Experimental: true),
        new CredentialProviderDescriptor(
            Freedom, "Freedom (Tradernet)", CredentialCategory.Broker,
            new[]
            {
                new CredentialField("publicKey", "Публичный ключ (API key)", IsSecret: true, IsMaskSource: true),
                new CredentialField("privateKey", "Секретный ключ (Secret key)", IsSecret: true, IsMaskSource: false),
            },
            Unlocks: "синхронизация портфеля Freedom (только чтение)"),
        new CredentialProviderDescriptor(
            FreedomTrade, "Freedom (торговля)", CredentialCategory.Broker,
            new[]
            {
                new CredentialField("publicKey", "Публичный ключ (API key) с правом торговли", IsSecret: true, IsMaskSource: true),
                new CredentialField("privateKey", "Секретный ключ (Secret key)", IsSecret: true, IsMaskSource: false),
            },
            Unlocks: "исполнение подтверждённых планов Freedom, фаза 3. Проверяется, что ключ от того же счёта, что ключ на чтение; торговое право проверится на первой заявке",
            Experimental: true),
        new CredentialProviderDescriptor(
            ClaudeCode, "Claude Code", CredentialCategory.Ai,
            new[] { new CredentialField("oauthToken", "OAuth-токен", IsSecret: true, IsMaskSource: true) },
            Unlocks: "ИИ-агенты: мониторинг и разборы"),
    };

    public static CredentialProviderDescriptor? Find(string? id) =>
        All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));
}

namespace Bonds.Core.Credentials;

/// <summary>
/// Реестр провайдеров секретов — ТОЛЬКО известные сейчас. Freedom (<c>freedom</c>: publicKey/privateKey)
/// добавит задача 59 записью сюда плюс валидатор. Каждый секрет включает свою часть функционала
/// (см. <c>GET /api/capabilities</c>).
/// </summary>
public static class CredentialProviders
{
    public const string TInvestRead = "tinvest_read";
    public const string TInvestTrade = "tinvest_trade";
    public const string ClaudeCode = "claude_code";

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
            ClaudeCode, "Claude Code", CredentialCategory.Ai,
            new[] { new CredentialField("oauthToken", "OAuth-токен", IsSecret: true, IsMaskSource: true) },
            Unlocks: "ИИ-агенты: мониторинг и разборы"),
    };

    public static CredentialProviderDescriptor? Find(string? id) =>
        All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));
}

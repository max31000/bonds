using Bonds.Core.Credentials;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Bonds.Infrastructure.Credentials;
using Bonds.Infrastructure.Services;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Bonds.Tests.Credentials;

public class CredentialProvidersTests
{
    [Fact]
    public void Registry_IdsAreUnique_AndFitColumn()
    {
        CredentialProviders.All.Select(p => p.Id).Should().OnlyHaveUniqueItems();
        CredentialProviders.All.Should().OnlyContain(p => p.Id.Length <= 48);
    }

    [Fact]
    public void Registry_EveryProviderHasExactlyOneMaskSourceField_AndFields()
    {
        foreach (var p in CredentialProviders.All)
        {
            p.Fields.Should().NotBeEmpty(p.Id);
            p.Fields.Count(f => f.IsMaskSource).Should().Be(1, $"{p.Id}: ровно одно mask-source поле");
            p.Fields.Select(f => f.Name).Should().OnlyHaveUniqueItems(p.Id);
        }
    }

    [Fact]
    public void Registry_ContainsKnownProviders()
    {
        CredentialProviders.Find("tinvest_read").Should().NotBeNull();
        CredentialProviders.Find("tinvest_trade")!.Experimental.Should().BeTrue();
        CredentialProviders.Find("claude_code")!.Category.Should().Be(CredentialCategory.Ai);
        CredentialProviders.Find("nope").Should().BeNull();
    }

    [Theory]
    [InlineData("abcdef1234", "1234")]
    [InlineData("abcd1234", "1234")]
    [InlineData("abc1234", null)] // короче 8 символов: маска раскрыла бы почти весь секрет
    [InlineData("ab", null)]
    [InlineData("", null)]
    public void ComputeLast4_UsesMaskSourceField(string token, string? expected)
    {
        var p = CredentialProviders.Find("tinvest_read")!;
        p.ComputeLast4(new Dictionary<string, string> { ["token"] = token }).Should().Be(expected);
    }
}

public class ClaudeCodeCredentialValidatorTests
{
    private static Task<CredentialValidationResult> Validate(string value) =>
        new ClaudeCodeCredentialValidator().ValidateAsync(new Dictionary<string, string> { ["oauthToken"] = value });

    [Fact]
    public async Task ValidToken_Passes()
    {
        var r = await Validate("sk-ant-oat01-abcdefghijklmnopqrstuvwxyz");
        r.Ok.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("sk-ant-oat01 with space inside-token")]
    [InlineData("sk-ant-oat01")] // слишком короткий (меньше 20)
    [InlineData("sk-ant-api03-abcdefghijklmnopqrstuvwxyz")] // не тот префикс (API-ключ, а не подписка)
    public async Task BadToken_Fails_WithoutEchoingSecret(string value)
    {
        var r = await Validate(value);
        r.Ok.Should().BeFalse();
        r.Message.Should().NotBeNullOrEmpty();
        if (value.Trim().Length > 0) r.Message.Should().NotContain(value.Trim());
    }

    [Fact]
    public async Task WrongPrefix_MentionsSetupToken()
    {
        var r = await Validate("sk-ant-api03-abcdefghijklmnopqrstuvwxyz");
        r.Message.Should().Contain("claude setup-token");
    }

    [Fact]
    public async Task MissingField_Fails()
    {
        var r = await new ClaudeCodeCredentialValidator().ValidateAsync(new Dictionary<string, string>());
        r.Ok.Should().BeFalse();
    }
}

public class TInvestTradeCredentialValidatorTests
{
    [Theory]
    [InlineData("t.abc", true)]
    [InlineData("", false)]
    [InlineData("t.a bc", false)]
    public async Task FormalCheckOnly(string token, bool ok)
    {
        var r = await new TInvestTradeCredentialValidator().ValidateAsync(new Dictionary<string, string> { ["token"] = token });
        r.Ok.Should().Be(ok);
    }
}

public class UserCredentialStoreTests
{
    private sealed class InMemoryRepo : IUserCredentialRepository
    {
        public readonly Dictionary<(ulong, string), UserCredentialRecord> Rows = new();

        public Task<UserCredentialRecord?> GetAsync(ulong userId, string provider) =>
            Task.FromResult(Rows.TryGetValue((userId, provider), out var r) ? r : null);

        public Task<IReadOnlyList<UserCredentialRecord>> ListAsync(ulong userId) =>
            Task.FromResult<IReadOnlyList<UserCredentialRecord>>(Rows.Values.Where(r => r.UserId == userId).ToList());

        public Task UpsertAsync(UserCredentialRecord record)
        {
            Rows[(record.UserId, record.Provider)] = record;
            return Task.CompletedTask;
        }

        public Task UpdateStatusAsync(ulong userId, string provider, string status, string? statusMessage)
        {
            var r = Rows[(userId, provider)];
            r.Status = status;
            r.StatusMessage = statusMessage;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(ulong userId, string provider)
        {
            Rows.Remove((userId, provider));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ulong>> ListUserIdsAsync(string provider, string status) =>
            Task.FromResult<IReadOnlyList<ulong>>(
                Rows.Values.Where(r => r.Provider == provider && r.Status == status).Select(r => r.UserId).OrderBy(x => x).ToList());
    }

    private sealed class FakeSettingsRepo : IUserSettingsRepository
    {
        public UserSettings? Settings;
        public Task<UserSettings?> GetByUserIdAsync(ulong userId) => Task.FromResult(Settings);
        public Task UpsertAsync(UserSettings settings)
        {
            Settings = settings;
            return Task.CompletedTask;
        }
    }

    private readonly InMemoryRepo _repo = new();
    private readonly FakeSettingsRepo _settings = new();
    private readonly IDataProtectionProvider _dp = new EphemeralDataProtectionProvider();

    private UserCredentialStore CreateStore(IDataProtectionProvider? dp = null) =>
        new(_repo, dp ?? _dp, _settings, NullLogger<UserCredentialStore>.Instance);

    [Fact]
    public async Task SaveThenGet_RoundTrips_JsonFields_AndCiphertextHasNoPlaintext()
    {
        var store = CreateStore();
        await store.SaveAsync(1, "claude_code", new Dictionary<string, string> { ["oauthToken"] = "sk-ant-oat01-SECRETVALUE" }, "ALUE", "{\"a\":1}");

        var row = _repo.Rows[(1UL, "claude_code")];
        row.SecretEncrypted.Should().NotContain("SECRETVALUE");
        row.Status.Should().Be("ok");
        row.SecretLast4.Should().Be("ALUE");
        row.VerifiedAt.Should().NotBeNull();

        var fields = await store.GetSecretAsync(1, "claude_code");
        fields!["oauthToken"].Should().Be("sk-ant-oat01-SECRETVALUE");
    }

    [Fact]
    public async Task TInvestRead_UsesLegacyPurpose_AndBareTokenFormat()
    {
        var store = CreateStore();
        await store.SaveAsync(1, "tinvest_read", new Dictionary<string, string> { ["token"] = "t.LegacyToken" }, "oken", null);

        // Шифротекст читается протектором ЛЕГАСИ-purpose как голая строка — совместим с user_settings.tinvest_token_encrypted.
        var bare = _dp.CreateProtector(TInvestTokenProvider.ProtectorPurpose).Unprotect(_repo.Rows[(1UL, "tinvest_read")].SecretEncrypted);
        bare.Should().Be("t.LegacyToken");
    }

    [Fact]
    public async Task TInvestRead_ReadsCiphertextCopiedFromUserSettings_ByMigration()
    {
        // Миграция 029 копирует user_settings.tinvest_token_encrypted как есть.
        var legacyCipher = _dp.CreateProtector(TInvestTokenProvider.ProtectorPurpose).Protect("t.OldProdToken");
        _repo.Rows[(7UL, "tinvest_read")] = new UserCredentialRecord
        {
            UserId = 7, Provider = "tinvest_read", SecretEncrypted = legacyCipher, SecretLast4 = "oken", Status = "ok",
        };

        var fields = await CreateStore().GetSecretAsync(7, "tinvest_read");
        fields!["token"].Should().Be("t.OldProdToken");
    }

    [Fact]
    public async Task NonLegacyProvider_UsesOwnPurpose()
    {
        var store = CreateStore();
        await store.SaveAsync(1, "tinvest_trade", new Dictionary<string, string> { ["token"] = "x" }, "x", null);
        var cipher = _repo.Rows[(1UL, "tinvest_trade")].SecretEncrypted;

        var act = () => _dp.CreateProtector(TInvestTokenProvider.ProtectorPurpose).Unprotect(cipher);
        act.Should().Throw<Exception>();
        _dp.CreateProtector("bonds.credentials.v1.tinvest_trade").Unprotect(cipher).Should().Contain("\"token\"");
    }

    [Fact]
    public async Task CorruptCiphertext_YieldsInvalid_WithoutException()
    {
        _repo.Rows[(1UL, "claude_code")] = new UserCredentialRecord
        {
            UserId = 1, Provider = "claude_code", SecretEncrypted = "garbage-not-a-protected-payload", Status = "ok",
        };
        var store = CreateStore();

        var result = await store.GetSecretResultAsync(1, "claude_code");
        result.Outcome.Should().Be(CredentialReadOutcome.DecryptionFailed);
        result.Fields.Should().BeNull();
        (await store.GetSecretAsync(1, "claude_code")).Should().BeNull();

        var row = _repo.Rows[(1UL, "claude_code")];
        row.Status.Should().Be("invalid");
        row.StatusMessage.Should().Contain("заново");
        (await store.HasValidAsync(1, "claude_code")).Should().BeFalse();
    }

    [Fact]
    public async Task KeysRotated_YieldsInvalid()
    {
        await CreateStore().SaveAsync(1, "claude_code", new Dictionary<string, string> { ["oauthToken"] = "sk-ant-oat01-whatever-long-token" }, "oken", null);
        var otherKeys = CreateStore(new EphemeralDataProtectionProvider());

        (await otherKeys.GetSecretResultAsync(1, "claude_code")).Outcome.Should().Be(CredentialReadOutcome.DecryptionFailed);
    }

    [Fact]
    public async Task Missing_ReturnsNull_AndMissingOutcome()
    {
        var store = CreateStore();
        (await store.GetSecretAsync(1, "claude_code")).Should().BeNull();
        (await store.GetSecretResultAsync(1, "claude_code")).Outcome.Should().Be(CredentialReadOutcome.Missing);
        (await store.HasValidAsync(1, "claude_code")).Should().BeFalse();
    }

    [Fact]
    public async Task UsersAreIsolated()
    {
        var store = CreateStore();
        await store.SaveAsync(1, "claude_code", new Dictionary<string, string> { ["oauthToken"] = "sk-ant-oat01-user-one-token-xxxx" }, "xxxx", null);

        (await store.GetSecretAsync(2, "claude_code")).Should().BeNull();
        (await store.ListStatusesAsync(2)).Should().BeEmpty();
        (await store.ListStatusesAsync(1)).Should().ContainSingle(s => s.Provider == "claude_code" && s.Status == "ok" && s.Last4 == "xxxx");
    }

    [Fact]
    public async Task Delete_RemovesRow_AndForTInvestRead_ClearsLegacySettingsToken()
    {
        _settings.Settings = new UserSettings { UserId = 1, TInvestTokenEncrypted = "legacy", TInvestTokenLast4 = "1234" };
        var store = CreateStore();
        await store.SaveAsync(1, "tinvest_read", new Dictionary<string, string> { ["token"] = "t.Something1234" }, "1234", null);

        await store.DeleteAsync(1, "tinvest_read");

        _repo.Rows.Should().BeEmpty();
        _settings.Settings!.TInvestTokenEncrypted.Should().BeNull("иначе страховочный fallback воскресил бы удалённый токен");
        _settings.Settings.TInvestTokenLast4.Should().BeNull();
    }
}

public class TInvestTokenProviderCredentialsTests
{
    private readonly Mock<IUserCredentialStore> _store = new();
    private readonly Mock<IUserSettingsRepository> _settings = new();
    private readonly Mock<IUserRepository> _users = new();
    private readonly IDataProtectionProvider _dp = new EphemeralDataProtectionProvider();

    private TInvestTokenProvider Create()
    {
        _users.Setup(u => u.GetPrimaryUserIdAsync()).ReturnsAsync(5UL);
        return new TInvestTokenProvider(_store.Object, _settings.Object, _users.Object, _dp, NullLogger<TInvestTokenProvider>.Instance);
    }

    [Fact]
    public async Task TokenFromCredentialStore_IsPreferred()
    {
        _store.Setup(s => s.GetSecretResultAsync(5UL, "tinvest_read")).ReturnsAsync(
            new CredentialSecretResult(CredentialReadOutcome.Ok, new Dictionary<string, string> { ["token"] = "t.fromStore" }));

        (await Create().GetTokenAsync()).Should().Be("t.fromStore");
    }

    [Fact]
    public async Task NoStoreRow_FallsBackToLegacyUserSettings()
    {
        _store.Setup(s => s.GetSecretResultAsync(5UL, "tinvest_read")).ReturnsAsync(new CredentialSecretResult(CredentialReadOutcome.Missing, null));
        _settings.Setup(s => s.GetByUserIdAsync(5UL)).ReturnsAsync(new UserSettings
        {
            UserId = 5, TInvestTokenEncrypted = _dp.CreateProtector(TInvestTokenProvider.ProtectorPurpose).Protect("t.legacy"),
        });

        (await Create().GetTokenAsync()).Should().Be("t.legacy");
    }

    [Fact]
    public async Task NoStoreRowAndNoLegacy_NotConfigured()
    {
        _store.Setup(s => s.GetSecretResultAsync(5UL, "tinvest_read")).ReturnsAsync(new CredentialSecretResult(CredentialReadOutcome.Missing, null));

        (await Create().GetTokenStatusAsync()).Should().Be(TInvestTokenStatus.NotConfigured);
    }

    [Fact]
    public async Task StoreDecryptionFailed_ReportsDecryptionFailed_WithoutLegacyFallback()
    {
        _store.Setup(s => s.GetSecretResultAsync(5UL, "tinvest_read")).ReturnsAsync(new CredentialSecretResult(CredentialReadOutcome.DecryptionFailed, null));

        var provider = Create();
        (await provider.GetTokenStatusAsync()).Should().Be(TInvestTokenStatus.DecryptionFailed);
        (await provider.GetTokenAsync()).Should().BeNull();
    }
}

public class TInvestReadCredentialValidatorTests
{
    [Fact]
    public async Task Delegates_ToExistingTokenValidator_AndExposesAccountOnlyAsLast4Meta()
    {
        var inner = new Mock<Bonds.Infrastructure.Connectors.TInvest.ITInvestTokenValidator>();
        inner.Setup(v => v.ValidateAsync("t.tok", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Bonds.Infrastructure.Connectors.TInvest.TInvestTokenValidationResult.Valid("ACC-0042"));

        var r = await new TInvestReadCredentialValidator(inner.Object)
            .ValidateAsync(new Dictionary<string, string> { ["token"] = " t.tok " });

        r.Ok.Should().BeTrue();
        r.BrokerAccountId.Should().Be("ACC-0042");
        r.MetaJson.Should().Contain("0042").And.NotContain("ACC-");
    }

    [Fact]
    public async Task InnerRejection_BecomesInvalidWithSameMessage()
    {
        var inner = new Mock<Bonds.Infrastructure.Connectors.TInvest.ITInvestTokenValidator>();
        inner.Setup(v => v.ValidateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Bonds.Infrastructure.Connectors.TInvest.TInvestTokenValidationResult.Invalid("отозван"));

        var r = await new TInvestReadCredentialValidator(inner.Object).ValidateAsync(new Dictionary<string, string> { ["token"] = "t.x" });

        r.Ok.Should().BeFalse();
        r.Message.Should().Be("отозван");
    }
}

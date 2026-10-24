using Bonds.Core.Credentials;
using Bonds.Core.Execution;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Policy;
using Bonds.Infrastructure.Connectors.Freedom;
using Bonds.Infrastructure.Connectors.Freedom.Models;
using Bonds.Infrastructure.Credentials;
using Bonds.Infrastructure.Execution;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;
using BrokerIds = Bonds.Core.Models.Brokers;

namespace Bonds.Tests.Execution;

/// <summary>Задача 66: торговый ключ Freedom — сетевая сверка счёта с ключом на чтение и ворота Freedom из живых данных.</summary>
public class FreedomTradeCredentialTests
{
    private const ulong UserId = 3;
    private static readonly Dictionary<string, string> Keys = new() { ["publicKey"] = "pub-TEST-1", ["privateKey"] = "priv-TEST-2" };

    private static FreedomTradeCredentialValidator Validator(Func<FreedomUserInfo> info)
    {
        var client = new Mock<IFreedomApiClient>();
        client.Setup(c => c.GetUserInfoAsync(It.IsAny<FreedomCredentials>(), It.IsAny<CancellationToken>())).ReturnsAsync(info);
        return new FreedomTradeCredentialValidator(client.Object);
    }

    [Fact]
    public async Task SameAccountAsReadKey_Valid_MetaNetworkVerified_TradeRightUnverified()
    {
        var result = await Validator(() => new FreedomUserInfo("100001", 1431, "Standard"))
            .ValidateAsync(Keys, new CredentialValidationContext(UserId, null, "100001"));

        result.Ok.Should().BeTrue();
        FreedomTradeCredentialMeta.AccountNumberOf(result.MetaJson).Should().Be("100001");
        FreedomTradeCredentialMeta.TariffIdOf(result.MetaJson).Should().Be(1431);
        using var meta = System.Text.Json.JsonDocument.Parse(result.MetaJson!);
        meta.RootElement.GetProperty("tradeRight").GetString().Should().Be("unverified");
        meta.RootElement.GetProperty("note").GetString().Should().Contain("первой заявке");
        result.MetaJson.Should().NotContain("pub-TEST-1").And.NotContain("priv-TEST-2");
    }

    [Fact]
    public async Task OtherAccount_Invalid()
    {
        var result = await Validator(() => new FreedomUserInfo("200002", 1431, "Standard"))
            .ValidateAsync(Keys, new CredentialValidationContext(UserId, null, "100001"));
        result.Ok.Should().BeFalse();
        result.Message.Should().Contain("другого счёта");
    }

    [Fact]
    public async Task NoReadKey_Invalid_WithoutNetworkCall()
    {
        var client = new Mock<IFreedomApiClient>(MockBehavior.Strict);
        var result = await new FreedomTradeCredentialValidator(client.Object).ValidateAsync(Keys, new CredentialValidationContext(UserId, null, null));
        result.Ok.Should().BeFalse();
        result.Message.Should().Contain("ключ Freedom на чтение");
    }

    [Fact]
    public async Task BrokerRefusesKeys_InvalidWithFixedMessage()
    {
        var client = new Mock<IFreedomApiClient>();
        client.Setup(c => c.GetUserInfoAsync(It.IsAny<FreedomCredentials>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new FreedomApiException("GetAllUserTexInfo", "bad sig pub-TEST-1"));
        var result = await new FreedomTradeCredentialValidator(client.Object).ValidateAsync(Keys, new CredentialValidationContext(UserId, null, "100001"));
        result.Ok.Should().BeFalse();
        result.Message.Should().NotContain("pub-TEST-1");
    }

    [Fact]
    public async Task ContextFreeOverload_AlwaysInvalid() =>
        (await Validator(() => new FreedomUserInfo("100001", 1431, "Standard")).ValidateAsync(Keys)).Ok.Should().BeFalse();

    // ─── Ворота Freedom ───────────────────────────────────────────────────────────────────

    private static async Task<ExecutionGateResult> FreedomGate(
        bool enabled, string mode, bool paused, CredentialStatusInfo? trade, CredentialStatusInfo? read)
    {
        var settings = new Mock<IUserSettingsRepository>();
        settings.Setup(s => s.GetExecutionModeAsync(UserId)).ReturnsAsync(mode);
        settings.Setup(s => s.IsExecutionPausedAsync(UserId)).ReturnsAsync(paused);
        var store = new Mock<IUserCredentialStore>();
        store.Setup(s => s.ListStatusesAsync(UserId)).ReturnsAsync(new[] { trade, read }.Where(x => x is not null).Select(x => x!).ToList());
        var monitor = Mock.Of<IOptionsMonitor<ExecutionOptions>>(m => m.CurrentValue == new ExecutionOptions { Enabled = enabled });
        return await new ExecutionGateResolver(monitor, settings.Object, store.Object).ResolveForBrokerAsync(UserId, BrokerIds.Freedom);
    }

    private static CredentialStatusInfo Trade(string account = "100001", string status = CredentialStatus.Ok) =>
        new(CredentialProviders.FreedomTrade, status, null, "0001", FreedomTradeCredentialMeta.Build(account, 1431), DateTime.UtcNow);

    private static CredentialStatusInfo Read(string account = "100001", string status = CredentialStatus.Ok) =>
        new(CredentialProviders.Freedom, status, null, "0001", $"{{\"accountNumber\":\"{account}\",\"tariffId\":1431,\"tariffName\":\"Standard\"}}", DateTime.UtcNow);

    [Fact]
    public async Task Gate_AllConditions_Live() =>
        (await FreedomGate(true, ExecutionModes.Live, false, Trade(), Read())).Live.Should().BeTrue();

    [Fact]
    public async Task Gate_NoTradeKey_Closed_WithFreedomReason()
    {
        var gate = await FreedomGate(true, ExecutionModes.Live, false, null, Read());
        gate.Live.Should().BeFalse();
        gate.Reasons.Should().ContainSingle().Which.Should().Be(ExecutionGate.NoFreedomTradeCredential);
    }

    [Fact]
    public async Task Gate_ReadKeyNowOtherAccount_Closed() =>
        (await FreedomGate(true, ExecutionModes.Live, false, Trade(), Read("200002"))).Live.Should().BeFalse();

    [Fact]
    public async Task Gate_ReadKeyInvalid_Closed() =>
        (await FreedomGate(true, ExecutionModes.Live, false, Trade(), Read(status: CredentialStatus.Invalid))).Live.Should().BeFalse();

    [Fact]
    public async Task Gate_TradeKeyInvalidOrWithoutNetworkMark_Closed()
    {
        (await FreedomGate(true, ExecutionModes.Live, false, Trade(status: CredentialStatus.Invalid), Read())).Live.Should().BeFalse();
        var formal = new CredentialStatusInfo(CredentialProviders.FreedomTrade, CredentialStatus.Ok, null, "0001", "{\"accountNumber\":\"100001\"}", DateTime.UtcNow);
        (await FreedomGate(true, ExecutionModes.Live, false, formal, Read())).Live.Should().BeFalse();
    }

    [Fact]
    public async Task Gate_EnvModePause_SameAsTInvest()
    {
        (await FreedomGate(false, ExecutionModes.Live, false, Trade(), Read())).Live.Should().BeFalse();
        (await FreedomGate(true, ExecutionModes.Observe, false, Trade(), Read())).Live.Should().BeFalse();
        (await FreedomGate(true, ExecutionModes.Live, true, Trade(), Read())).Live.Should().BeFalse();
    }

    [Fact]
    public async Task Gate_TInvestTokenDoesNotOpenFreedom()
    {
        var tinvest = new CredentialStatusInfo(CredentialProviders.TInvestTrade, CredentialStatus.Ok, null, "0001", TradeCredentialMeta.Build("ACC-1"), DateTime.UtcNow);
        (await FreedomGate(true, ExecutionModes.Live, false, tinvest, Read())).Live.Should().BeFalse();
    }

    [Fact]
    public async Task UnknownBroker_GateClosed()
    {
        var resolver = new ExecutionGateResolver(
            Mock.Of<IOptionsMonitor<ExecutionOptions>>(m => m.CurrentValue == new ExecutionOptions { Enabled = true }),
            Mock.Of<IUserSettingsRepository>(), Mock.Of<IUserCredentialStore>());
        (await resolver.ResolveForBrokerAsync(UserId, "bcs")).Live.Should().BeFalse();
    }
}

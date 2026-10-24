using Bonds.Core.Credentials;
using Bonds.Core.Execution;
using Bonds.Core.Policy;
using Bonds.Infrastructure.Execution;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tinkoff.InvestApi.V1;
using Xunit;

namespace Bonds.Tests.Execution;

/// <summary>Задача 65: последний рубеж в самом шлюзе — при закрытых воротах отправка отклоняется ДО чтения торгового токена и любых сетевых вызовов.</summary>
public class TInvestOrderGatewayTests
{
    public static IEnumerable<object[]> ClosedGates()
    {
        foreach (var enabled in new[] { false, true })
        foreach (var live in new[] { false, true })
        foreach (var token in new[] { false, true })
        foreach (var paused in new[] { false, true })
            if (!(enabled && live && token && !paused)) yield return [enabled, live, token, paused];
    }

    [Theory]
    [MemberData(nameof(ClosedGates))]
    public async Task PostLimitOrder_GateClosed_RejectedWithoutTouchingToken(bool enabled, bool live, bool token, bool paused)
    {
        var gate = new Mock<IExecutionGateResolver>();
        gate.Setup(g => g.ResolveAsync(1)).ReturnsAsync(ExecutionGate.Evaluate(enabled, live ? ExecutionModes.Live : ExecutionModes.Observe, token, paused));
        var store = new Mock<IUserCredentialStore>(MockBehavior.Strict);
        var gateway = new TInvestOrderGateway(store.Object, gate.Object, new ConfigurationBuilder().Build(), NullLogger<TInvestOrderGateway>.Instance);

        var act = () => gateway.PostLimitOrderAsync(1, new GatewayLimitOrder("ACC", "uid", PlanSides.Buy, 1, 100m, OrderPriceUnits.Currency, Guid.NewGuid().ToString("D")));

        (await act.Should().ThrowAsync<TInvestGatewayException>()).Which.Should().Match<TInvestGatewayException>(e => e.Kind == GatewayErrorKind.Rejected && e.Code == "gate_closed");
        store.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PostLimitOrder_GateOpen_ButNoTradeToken_Rejected()
    {
        var gate = new Mock<IExecutionGateResolver>();
        gate.Setup(g => g.ResolveAsync(1)).ReturnsAsync(ExecutionGate.Evaluate(true, ExecutionModes.Live, true, false));
        var store = new Mock<IUserCredentialStore>();
        store.Setup(s => s.GetSecretResultAsync(1, CredentialProviders.TInvestTrade)).ReturnsAsync(new CredentialSecretResult(CredentialReadOutcome.Missing, null));
        var gateway = new TInvestOrderGateway(store.Object, gate.Object, new ConfigurationBuilder().Build(), NullLogger<TInvestOrderGateway>.Instance);

        var act = () => gateway.PostLimitOrderAsync(1, new GatewayLimitOrder("ACC", "uid", PlanSides.Buy, 1, 100m, OrderPriceUnits.Currency, Guid.NewGuid().ToString("D")));

        (await act.Should().ThrowAsync<TInvestGatewayException>()).Which.Code.Should().Be("no_trade_token");
        store.Verify(s => s.GetSecretResultAsync(1, CredentialProviders.TInvestTrade), Times.Once);
        store.Verify(s => s.GetSecretResultAsync(It.IsAny<ulong>(), CredentialProviders.TInvestRead), Times.Never, "read-only токен шлюз не использует");
    }

    [Theory]
    [InlineData(OrderExecutionReportStatus.ExecutionReportStatusFill, TradeOrderStatuses.Filled)]
    [InlineData(OrderExecutionReportStatus.ExecutionReportStatusPartiallyfill, TradeOrderStatuses.PartiallyFilled)]
    [InlineData(OrderExecutionReportStatus.ExecutionReportStatusRejected, TradeOrderStatuses.Rejected)]
    [InlineData(OrderExecutionReportStatus.ExecutionReportStatusCancelled, TradeOrderStatuses.Cancelled)]
    [InlineData(OrderExecutionReportStatus.ExecutionReportStatusNew, TradeOrderStatuses.Submitted)]
    [InlineData(OrderExecutionReportStatus.ExecutionReportStatusUnspecified, TradeOrderStatuses.Submitted)]
    public void BrokerStatusMapping(OrderExecutionReportStatus broker, string expected) => TInvestOrderGateway.MapStatus(broker).Should().Be(expected);
}

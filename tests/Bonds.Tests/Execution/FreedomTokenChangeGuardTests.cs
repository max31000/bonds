using Bonds.Core.Execution;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Bonds.Core.Policy;
using Bonds.Infrastructure.Execution;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;
using BrokerIds = Bonds.Core.Models.Brokers;

namespace Bonds.Tests.Execution;

/// <summary>
/// Задача 66 (ревью): смена торгового ключа Freedom при заявке <c>New</c> без ответа брокера. Брокер её ещё не показал, а таймаут сверки не вышел — 409
/// «заявка ещё не сверена» (заявка могла дойти до брокера, а без текущего ключа её не снять); после таймаута — закрывается как не дошедшая.
/// </summary>
public class FreedomTokenChangeGuardTests
{
    private const ulong UserId = 9;
    private static readonly DateTime CreatedAt = new(2026, 10, 23, 9, 0, 0, DateTimeKind.Utc);

    private readonly InMemoryOrders _orders = new();
    private readonly InMemoryPlans _plans = new();
    private readonly FakeFreedomOrderGateway _freedom = new() { Now = CreatedAt };
    private readonly ExecutionOptions _options = new();

    private sealed class FreeLock : IPlanExecutorLock
    {
        private sealed class Held : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        public Task<IAsyncDisposable?> TryAcquireAsync(TimeSpan wait, CancellationToken ct = default) => Task.FromResult<IAsyncDisposable?>(new Held());
    }

    private TradeTokenChangeGuard Guard()
    {
        var accounts = new Mock<IAccountRepository>();
        accounts.Setup(a => a.GetByUserIdAndBrokerAsync(UserId, BrokerIds.Freedom))
            .ReturnsAsync([new Account { Id = 21, UserId = UserId, Broker = BrokerIds.Freedom, BrokerAccountId = "100001" }]);
        return new TradeTokenChangeGuard(
            _orders, _plans, accounts.Object, new FakeOrderGateway(), new FreeLock(), NullLogger<TradeTokenChangeGuard>.Instance, Mock.Of<IExecutionGateResolver>(),
            Mock.Of<IUserCredentialRepository>(), _freedom, Mock.Of<IOptionsMonitor<ExecutionOptions>>(m => m.CurrentValue == _options));
    }

    private TradeOrder UnconfirmedOrder()
    {
        var plan = new TradePlan
        {
            UserId = UserId, Rule = PlanRules.P7FreedomTopUp, Trigger = "t", Status = PlanStatuses.Submitting, DryRun = false, CreatedAt = CreatedAt,
            ExpiresAt = CreatedAt.AddHours(1), DecidedAt = CreatedAt, DecidedVia = DecidedVia.Web, BotNonce = "nonce12345A",
            Lines =
            [
                new TradePlanLine
                {
                    LineNo = 1, Broker = BrokerIds.Freedom, AccountId = 21, InstrumentKey = "VUAA.EU", Ticker = "VUAA.EU", Side = PlanSides.Buy, QuantityLots = 3, LotSize = 1m,
                    LimitPrice = 147.70m, Currency = "USD",
                },
            ],
        };
        _plans.Add(plan);
        var order = new TradeOrder
        {
            PlanId = plan.Id, LineNo = 1, Attempt = 1, ClientOrderId = "712345678", Broker = BrokerIds.Freedom, Status = TradeOrderStatuses.New, LimitPrice = 147.70m,
            LotsRequested = 3, Currency = "USD", CreatedAt = CreatedAt, UpdatedAt = CreatedAt,
        };
        _orders.InsertAsync(order).GetAwaiter().GetResult();
        return order;
    }

    [Fact]
    public async Task NewUnmatchedOrder_WithinTimeout_Conflict_NotFailed_KeyNotChanged()
    {
        var order = UnconfirmedOrder();
        var changed = false;

        var outcome = await Guard().RunAsync(UserId, cancelActive: true, () => { changed = true; return Task.CompletedTask; }, CreatedAt.AddMinutes(1), broker: BrokerIds.Freedom);

        outcome.Ok.Should().BeFalse();
        outcome.Error.Should().Contain("ещё не сверена");
        changed.Should().BeFalse("ключ не меняется, пока заявка не сверена");
        order.Status.Should().Be(TradeOrderStatuses.New, "не закрываем как «не дошла» — она могла дойти до брокера");
    }

    [Fact]
    public async Task NewUnmatchedOrder_AfterTimeout_ClosedAsUnknown_KeyChanged()
    {
        var order = UnconfirmedOrder();
        var changed = false;

        var outcome = await Guard().RunAsync(
            UserId, cancelActive: true, () => { changed = true; return Task.CompletedTask; }, CreatedAt.Add(_options.UnconfirmedOrderTimeout).AddSeconds(1), broker: BrokerIds.Freedom);

        outcome.Ok.Should().BeTrue();
        changed.Should().BeTrue();
        order.Status.Should().Be(TradeOrderStatuses.Failed);
        order.ErrorCode.Should().Be(TradeOrderErrorCodes.UnknownState);
    }

    [Fact]
    public async Task NewOrderFoundByUserOrderId_CancelledWithCurrentKey()
    {
        var order = UnconfirmedOrder();
        _freedom.Orders["900000001"] = new GatewayOrderState(
            "900000001", "api:712345678", TradeOrderStatuses.Submitted, 3, 0, null, null, "USD", null, Ticker: "VUAA.EU", Side: PlanSides.Buy, LimitPrice: 147.70m,
            PlacedAtUtc: CreatedAt.AddSeconds(1));

        var outcome = await Guard().RunAsync(UserId, cancelActive: true, () => Task.CompletedTask, CreatedAt.AddMinutes(1), broker: BrokerIds.Freedom);

        outcome.Ok.Should().BeTrue();
        _freedom.Cancelled.Should().ContainSingle().Which.Should().Be("900000001");
        order.Status.Should().Be(TradeOrderStatuses.Cancelled);
        order.ErrorCode.Should().Be(TradeOrderErrorCodes.TokenChange);
    }
}

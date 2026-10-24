using System.Net;
using System.Text;
using System.Text.Json;
using Bonds.Core.Credentials;
using Bonds.Core.Execution;
using BrokerIds = Bonds.Core.Models.Brokers;
using Bonds.Core.Policy;
using Bonds.Infrastructure.Connectors.Freedom;
using Bonds.Infrastructure.Execution;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Bonds.Tests.Execution;

/// <summary>
/// Задача 66: <see cref="FreedomOrderGateway"/> на фейковом HTTP (фикстуры <c>putTradeOrder.json</c>, <c>putTradeOrder_error.json</c>, синтетический список заявок) —
/// тело заявки, белый список action_id (2/4 не уходят никогда), .ITS и закрытые ворота — без сети, разбор ошибки брокера и классификация «исход неизвестен».
/// </summary>
public class FreedomOrderGatewayTests
{
    private const ulong UserId = 5;
    private const string Pub = "trade-pub-TEST-0001";
    private const string Priv = "trade-priv-TEST-0002";

    private sealed class FakeHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(string Path, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.RequestUri!.AbsolutePath, body));
            return respond(request, body);
        }
    }

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Freedom", name + ".json"));

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static (FreedomOrderGateway Gateway, FakeHandler Handler) Create(
        Func<HttpRequestMessage, string, HttpResponseMessage> respond, bool gateOpen = true, bool keyPresent = true)
    {
        var handler = new FakeHandler(respond);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://tradernet.global") };
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(FreedomApiClient.HttpClientName)).Returns(http);

        var store = new Mock<IUserCredentialStore>();
        store.Setup(s => s.GetSecretResultAsync(UserId, CredentialProviders.FreedomTrade)).ReturnsAsync(keyPresent
            ? new CredentialSecretResult(CredentialReadOutcome.Ok, new Dictionary<string, string> { ["publicKey"] = Pub, ["privateKey"] = Priv })
            : new CredentialSecretResult(CredentialReadOutcome.Missing, null));
        var gate = new Mock<IExecutionGateResolver>();
        gate.Setup(g => g.ResolveForBrokerAsync(UserId, BrokerIds.Freedom))
            .ReturnsAsync(ExecutionGate.Evaluate(true, gateOpen ? ExecutionModes.Live : ExecutionModes.Observe, true, false, ExecutionGate.NoFreedomTradeCredential));

        var client = new FreedomApiClient(factory.Object);
        return (new FreedomOrderGateway(store.Object, gate.Object, client, factory.Object, TimeProvider.System, NullLogger<FreedomOrderGateway>.Instance), handler);
    }

    private static FreedomLimitOrder Order(string ticker = "VUAA.EU", string side = PlanSides.Buy, long qty = 3, decimal price = 147.70m, string id = "1234567890") =>
        new(ticker, side, qty, price, id);

    [Fact]
    public async Task Post_SendsLimitDayOrderWithUserOrderId_AndParsesFixture()
    {
        var (gateway, handler) = Create((_, _) => Json(Fixture("putTradeOrder")));

        var state = await gateway.PostLimitOrderAsync(UserId, Order());

        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Path.Should().Be("/api/putTradeOrder");
        handler.Requests[0].Body.Should().Be(
            "{\"instr_name\":\"VUAA.EU\",\"action_id\":1,\"order_type_id\":2,\"qty\":3,\"limit_price\":147.7,\"expiration_id\":1,\"user_order_id\":1234567890}");
        state.BrokerOrderId.Should().Be("900000001");
        state.Status.Should().Be(TradeOrderStatuses.Submitted, "stat 10 — активна");
        state.LotsRequested.Should().Be(3);
        state.LotsExecuted.Should().Be(0);
        state.Currency.Should().Be("USD");
        handler.Requests[0].Body.Should().NotContain(Priv).And.NotContain(Pub);
    }

    [Fact]
    public async Task Post_SellUsesActionId3()
    {
        var (gateway, handler) = Create((_, _) => Json(Fixture("putTradeOrder")));
        await gateway.PostLimitOrderAsync(UserId, Order(side: PlanSides.Sell));
        JsonDocument.Parse(handler.Requests[0].Body).RootElement.GetProperty("action_id").GetInt32().Should().Be(3);
    }

    [Theory]
    [InlineData(PlanSides.Buy)]
    [InlineData(PlanSides.Sell)]
    [InlineData("Margin")]
    [InlineData("ShortSell")]
    [InlineData("")]
    public async Task MarginActionIds_AreNeverSent(string side)
    {
        var (gateway, handler) = Create((_, _) => Json(Fixture("putTradeOrder")));
        try
        {
            await gateway.PostLimitOrderAsync(UserId, Order(side: side));
        }
        catch (FreedomGatewayException)
        {
            // неизвестная сторона — отказ до сети
        }

        foreach (var (_, body) in handler.Requests)
        {
            var actionId = JsonDocument.Parse(body).RootElement.GetProperty("action_id").GetInt32();
            actionId.Should().BeOneOf(1, 3);
            actionId.Should().NotBe(2).And.NotBe(4);
        }

        if (side is not (PlanSides.Buy or PlanSides.Sell)) handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public void BuildPutParams_OnlyWhitelistedActionIds()
    {
        foreach (var side in new[] { PlanSides.Buy, PlanSides.Sell })
        {
            var p = FreedomOrderGateway.BuildPutParams(Order(side: side), 1234567890);
            var action = (int)p.Single(x => x.Key == "action_id").Value;
            FreedomOrderRules.AllowedActionIds.Should().Contain(action);
            p.Single(x => x.Key == "order_type_id").Value.Should().Be(2);
            p.Single(x => x.Key == "expiration_id").Value.Should().Be(1);
        }
    }

    [Theory]
    [InlineData("SPYM.ITS", "its_forbidden")]
    [InlineData("SPYM", "ticker_forbidden")]
    [InlineData("KZTK.KZ", "ticker_forbidden")]
    public async Task NonUsEuTicker_RejectedBeforeNetwork(string ticker, string code)
    {
        var (gateway, handler) = Create((_, _) => Json(Fixture("putTradeOrder")));
        var act = () => gateway.PostLimitOrderAsync(UserId, Order(ticker: ticker));
        (await act.Should().ThrowAsync<FreedomGatewayException>()).Which.Code.Should().Be(code);
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task GateClosed_RejectedBeforeNetwork()
    {
        var (gateway, handler) = Create((_, _) => Json(Fixture("putTradeOrder")), gateOpen: false);
        var act = () => gateway.PostLimitOrderAsync(UserId, Order());
        var ex = (await act.Should().ThrowAsync<FreedomGatewayException>()).Which;
        ex.Code.Should().Be("gate_closed");
        ex.Kind.Should().Be(GatewayErrorKind.Rejected);
        handler.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0, 147.7, "1234567890")]
    [InlineData(1, 0, "1234567890")]
    [InlineData(1, 147.7, "api:1")]
    [InlineData(1, 147.7, "123")]
    public async Task InvalidOrder_RejectedBeforeNetwork(long qty, double price, string id)
    {
        var (gateway, handler) = Create((_, _) => Json(Fixture("putTradeOrder")));
        var act = () => gateway.PostLimitOrderAsync(UserId, Order(qty: qty, price: (decimal)price, id: id));
        (await act.Should().ThrowAsync<FreedomGatewayException>()).Which.Code.Should().Be("invalid_order");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task NoTradeKey_RejectedBeforeNetwork()
    {
        var (gateway, handler) = Create((_, _) => Json(Fixture("putTradeOrder")), keyPresent: false);
        var act = () => gateway.PostLimitOrderAsync(UserId, Order());
        (await act.Should().ThrowAsync<FreedomGatewayException>()).Which.Code.Should().Be("no_trade_key");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task BrokerError_IsDefiniteRejection_WithBrokerText()
    {
        var (gateway, _) = Create((_, _) => Json(Fixture("putTradeOrder_error")));
        var act = () => gateway.PostLimitOrderAsync(UserId, Order());
        var ex = (await act.Should().ThrowAsync<FreedomGatewayException>()).Which;
        ex.Kind.Should().Be(GatewayErrorKind.Rejected);
        ex.Code.Should().Be("broker_rejected");
        ex.Message.Should().Contain("not available for trading");
    }

    [Fact]
    public async Task BrokerErrorEchoingKey_IsScrubbed()
    {
        var (gateway, _) = Create((_, _) => Json("{\"error\":\"bad key " + Pub + "\"}"));
        var act = () => gateway.PostLimitOrderAsync(UserId, Order());
        (await act.Should().ThrowAsync<FreedomGatewayException>()).Which.Message.Should().NotContain(Pub);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, GatewayErrorKind.Transient)]
    [InlineData(HttpStatusCode.BadGateway, GatewayErrorKind.Transient)]
    [InlineData(HttpStatusCode.TooManyRequests, GatewayErrorKind.Transient)]
    [InlineData(HttpStatusCode.Unauthorized, GatewayErrorKind.Rejected)]
    [InlineData(HttpStatusCode.Forbidden, GatewayErrorKind.Rejected)]
    public async Task HttpStatus_Classified(HttpStatusCode code, GatewayErrorKind kind)
    {
        var (gateway, _) = Create((_, _) => Json("{}", code));
        var act = () => gateway.PostLimitOrderAsync(UserId, Order());
        (await act.Should().ThrowAsync<FreedomGatewayException>()).Which.Kind.Should().Be(kind);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"warning\":\"\"}")]
    public async Task UnparsableOrNoOrderId_IsUnknownOutcome(string body)
    {
        var (gateway, _) = Create((_, _) => Json(body));
        var act = () => gateway.PostLimitOrderAsync(UserId, Order());
        (await act.Should().ThrowAsync<FreedomGatewayException>()).Which.Kind.Should().Be(GatewayErrorKind.Transient);
    }

    [Fact]
    public async Task NetworkError_IsUnknownOutcome()
    {
        var (gateway, _) = Create((_, _) => throw new HttpRequestException("boom"));
        var act = () => gateway.PostLimitOrderAsync(UserId, Order());
        (await act.Should().ThrowAsync<FreedomGatewayException>()).Which.Kind.Should().Be(GatewayErrorKind.Transient);
    }

    [Fact]
    public async Task Orders_ParsedFromSyntheticFixture()
    {
        var (gateway, handler) = Create((_, _) => Json(Fixture("getNotifyOrderJson")));

        var orders = await gateway.GetOrdersAsync(UserId, activeOnly: false);

        handler.Requests[0].Path.Should().Be("/api/getNotifyOrderJson");
        handler.Requests[0].Body.Should().Be("{\"active_only\":0}");
        orders.Should().HaveCount(3);
        orders[0].Should().Match<GatewayOrderState>(o => o.BrokerOrderId == "900000001" && o.Status == TradeOrderStatuses.Submitted && o.ClientOrderId == "1234567890");
        orders[1].Status.Should().Be(TradeOrderStatuses.Filled);
        orders[1].LotsExecuted.Should().Be(2);
        orders[1].AvgFillPrice.Should().Be(54.05m);
        orders[2].Status.Should().Be(TradeOrderStatuses.Rejected);
        orders[2].Message.Should().Be("Not enough funds");
    }

    [Fact]
    public async Task Cancel_SendsBrokerOrderId_ErrorIsRefusal()
    {
        var (gateway, handler) = Create((_, _) => Json("{\"order_id\":900000001}"));
        await gateway.CancelOrderAsync(UserId, "900000001");
        handler.Requests[0].Path.Should().Be("/api/delTradeOrder");
        handler.Requests[0].Body.Should().Be("{\"order_id\":900000001}");

        var (refusing, _) = Create((_, _) => Json("{\"error\":\"Order already filled\"}"));
        var act = () => refusing.CancelOrderAsync(UserId, "900000001");
        (await act.Should().ThrowAsync<FreedomGatewayException>()).Which.Kind.Should().Be(GatewayErrorKind.Rejected);

        var (bad, badHandler) = Create((_, _) => Json("{}"));
        var invalid = () => bad.CancelOrderAsync(UserId, "abc");
        await invalid.Should().ThrowAsync<FreedomGatewayException>();
        badHandler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Requests_AreSignedWithTradeKey()
    {
        HttpRequestMessage? seen = null;
        var (gateway, _) = Create((r, _) =>
        {
            seen = r;
            return Json(Fixture("putTradeOrder"));
        });
        await gateway.PostLimitOrderAsync(UserId, Order());
        seen!.Headers.GetValues("X-NtApi-PublicKey").Single().Should().Be(Pub);
        seen.Headers.Contains("X-NtApi-Sig").Should().BeTrue();
    }
}

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
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Bonds.Tests.Execution;

/// <summary>
/// Задача 66: <see cref="FreedomOrderGateway"/> на фейковом HTTP (фикстуры <c>putTradeOrder.json</c>, <c>putTradeOrder_error.json</c>, список заявок в форме живого ответа) —
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

    /// <summary>Фиксированные часы шлюза: 2026-10-23 14:05 UTC (17:05 МСК).</summary>
    private static readonly DateTime FixedNowUtc = new(2026, 10, 23, 14, 5, 0, DateTimeKind.Utc);

    private sealed class FixedTime(DateTime utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utc);
    }

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
        return (new FreedomOrderGateway(
            store.Object, gate.Object, client, factory.Object, new FixedTime(FixedNowUtc), NullLogger<FreedomOrderGateway>.Instance,
            Options.Create(new FreedomOptions { OrderTimeUtcOffsetHours = 3 })), handler);
    }

    private static FreedomLimitOrder Order(string ticker = "VUAA.EU", string side = PlanSides.Buy, long qty = 3, decimal price = 147.70m, string id = "712345678") =>
        new(ticker, side, qty, price, id);

    [Fact]
    public async Task Post_SendsLimitDayOrderWithUserOrderId_AndParsesFixture()
    {
        var (gateway, handler) = Create((_, _) => Json(Fixture("putTradeOrder")));

        var state = await gateway.PostLimitOrderAsync(UserId, Order());

        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Path.Should().Be("/api/putTradeOrder");
        handler.Requests[0].Body.Should().Be(
            "{\"instr_name\":\"VUAA.EU\",\"action_id\":1,\"order_type_id\":2,\"qty\":3,\"limit_price\":147.7,\"expiration_id\":1,\"user_order_id\":712345678}");
        state.BrokerOrderId.Should().Be("900000001");
        state.ClientOrderId.Should().Be("712345678", "эхо «api:712345678» — наш номер");
        state.Status.Should().Be(TradeOrderStatuses.Submitted, "stat 10 — активен");
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
            var p = FreedomOrderGateway.BuildPutParams(Order(side: side), 712345678);
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
    [InlineData(0, 147.7, "712345678")]
    [InlineData(1, 0, "712345678")]
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

    [Theory]
    [InlineData("{\"user_order_id\":\"712345678\"}")]
    [InlineData("{\"user_order_id\":\"api:712345678\"}")]
    [InlineData("{\"user_order_id\":\"api:1792763201\"}")] // авто-id брокера — заявка всё равно привязана по order_id
    [InlineData("{\"user_order_id\":null}")]
    public async Task Post_EchoedUserOrderId_AnyForm_OrderLinkedByOrderId(string orderJson)
    {
        var body = "{\"order_id\":900000007,\"order\":" + orderJson[..^1] + ",\"order_id\":900000007,\"stat\":10,\"q\":3,\"leaves_qty\":3,\"instr\":\"VUAA.EU\",\"oper\":1,\"p\":147.7}}";
        var (gateway, _) = Create((_, _) => Json(body));

        var state = await gateway.PostLimitOrderAsync(UserId, Order());

        state.BrokerOrderId.Should().Be("900000007");
        state.ClientOrderId.Should().Be("712345678");
        state.Status.Should().Be(TradeOrderStatuses.Submitted);
        state.LotsExecuted.Should().Be(0);
    }

    /// <summary>
    /// Фикстура — структура ЖИВОГО ответа (обезличена): обёртка <c>result</c>, <c>order_id</c> = <c>id</c>, номер клиента <c>userOrderId</c> (camelCase: <c>api:N</c> у API-заявок,
    /// <c>i-6:ios:…</c> у ручной из приложения), сделки — массив <c>trade[]</c>, у окончательных кодов <c>leaves_qty = 0</c> (FIX).
    /// </summary>
    [Fact]
    public async Task Orders_ParsedFromLiveShapeFixture()
    {
        var (gateway, handler) = Create((_, _) => Json(Fixture("getNotifyOrderJson")));

        var orders = await gateway.GetOrdersAsync(UserId, activeOnly: false);

        handler.Requests[0].Path.Should().Be("/api/getNotifyOrderJson");
        handler.Requests[0].Body.Should().Be("{\"active_only\":0}");
        orders.Should().HaveCount(7);

        orders[0].Should().Match<GatewayOrderState>(o => o.BrokerOrderId == "900000001" && o.Status == TradeOrderStatuses.Submitted && o.ClientOrderId == "api:712345678");
        orders[0].Ticker.Should().Be("VUAA.EU");
        orders[0].Side.Should().Be(PlanSides.Buy);
        orders[0].LimitPrice.Should().Be(141.3m);
        orders[0].LotsRequested.Should().Be(4);
        orders[0].LotsExecuted.Should().Be(0, "активная без массива trade — запасной вариант q − leaves_qty = 0");
        orders[0].PlacedAtUtc.Should().Be(new DateTime(2026, 10, 23, 14, 6, 21, 120, DateTimeKind.Utc), "МСК − 3 ч");
        orders[0].PlacedAtUtc!.Value.Kind.Should().Be(DateTimeKind.Utc);

        orders[1].Status.Should().Be(TradeOrderStatuses.Filled, "stat 21 — исполнен");
        orders[1].ClientOrderId.Should().Be("api:1792763201", "авто-id брокера — unix-секунда запроса");
        orders[1].LotsExecuted.Should().Be(2);
        orders[1].AvgFillPrice.Should().Be(54.05m, "VWAP по trade[]");

        orders[2].Status.Should().Be(TradeOrderStatuses.Rejected, "stat 70 — отклонен, leaves_qty = 0 не значит «исполнено»");
        orders[2].LotsExecuted.Should().Be(0);
        orders[2].Side.Should().Be(PlanSides.Sell);
        orders[2].Message.Should().Be("Not enough funds");

        orders[3].Status.Should().Be(TradeOrderStatuses.Cancelled, "stat 30 — частично отменен");
        orders[3].LotsExecuted.Should().Be(2, "Σ trade[].q, а не q − leaves_qty (= 5)");
        orders[3].AvgFillPrice.Should().Be(148.12m);

        orders[4].Status.Should().Be(TradeOrderStatuses.Cancelled, "stat 31 — отменен");
        orders[4].LotsExecuted.Should().Be(0, "leaves_qty = 0 и trade[] пуст — ничего не исполнено");

        orders[5].Status.Should().Be(TradeOrderStatuses.Submitted, "stat 99 неизвестен — активна, даже при leaves_qty = 0");
        TradeOrderStatuses.IsActive(orders[5].Status).Should().BeTrue();

        orders[6].ClientOrderId.Should().Be("i-6:ios:terminal_exchange_currency", "ручная заявка из приложения");
        orders[6].Status.Should().Be(TradeOrderStatuses.Filled);
    }

    [Theory]
    [InlineData("{\"id\":1,\"stat\":31,\"q\":5,\"leaves_qty\":0,\"trade\":[]}", TradeOrderStatuses.Cancelled, 0)]
    [InlineData("{\"id\":1,\"stat\":31,\"q\":5,\"leaves_qty\":0}", TradeOrderStatuses.Submitted, 0)] // T-67E (M1): без trade[] — не окончательна до сверки истории
    [InlineData("{\"id\":1,\"stat\":31,\"q\":5,\"leaves_qty\":0,\"trade\":[{\"id\":7,\"p\":10.5,\"q\":2,\"v\":21}]}", TradeOrderStatuses.Cancelled, 2)]
    [InlineData("{\"id\":1,\"stat\":31,\"q\":5,\"leaves_qty\":0,\"trade\":{\"id\":7,\"p\":10.5,\"q\":3}}", TradeOrderStatuses.Cancelled, 3)]
    [InlineData("{\"id\":1,\"stat\":70,\"q\":5,\"leaves_qty\":0,\"trade\":[]}", TradeOrderStatuses.Rejected, 0)]
    [InlineData("{\"id\":1,\"stat\":10,\"q\":5,\"leaves_qty\":3}", TradeOrderStatuses.PartiallyFilled, 2)]
    [InlineData("{\"id\":1,\"stat\":10,\"q\":5,\"leaves_qty\":3,\"trade\":[{\"p\":10,\"q\":1}]}", TradeOrderStatuses.PartiallyFilled, 1)]
    public void Order_ExecutedFromTrades_NeverFromLeavesForFinalCodes(string json, string status, long executed)
    {
        using var doc = JsonDocument.Parse(json);
        var o = FreedomOrderGateway.ParseOrder(doc.RootElement, TimeSpan.FromHours(3))!;
        o.Status.Should().Be(status);
        o.LotsExecuted.Should().Be(executed);
    }

    [Fact]
    public void Order_UserOrderId_CamelCaseFirst_SnakeCaseFallback()
    {
        static string? Id(string json)
        {
            using var doc = JsonDocument.Parse(json);
            return FreedomOrderGateway.ParseOrder(doc.RootElement, TimeSpan.Zero)!.ClientOrderId;
        }

        Id("{\"id\":1,\"userOrderId\":\"api:712345678\",\"user_order_id\":\"x\"}").Should().Be("api:712345678");
        Id("{\"id\":1,\"user_order_id\":\"api:712345678\"}").Should().Be("api:712345678");
        Id("{\"id\":1,\"userOrderId\":\"\"}").Should().BeNull();
    }

    [Fact]
    public void Orders_WithoutResultWrapperAndSingleObject_AreAccepted_UnknownStatReported()
    {
        const string bare = "{\"orders\":{\"key\":\"X\",\"order\":{\"id\":900000009,\"stat\":42,\"instr\":\"SPYM.US\",\"oper\":3,\"p\":\"54.1\",\"q\":\"2\",\"leaves_qty\":\"2\",\"date\":\"2026-10-23 17:04:15\"}}}";
        var unknown = new List<(string Id, string? Stat)>();
        using var doc = JsonDocument.Parse(bare);

        var orders = FreedomOrderGateway.ParseOrders(doc.RootElement, TimeSpan.FromHours(3), (id, stat) => unknown.Add((id, stat)));

        orders.Should().ContainSingle();
        orders[0].Status.Should().Be(TradeOrderStatuses.Submitted);
        orders[0].Side.Should().Be(PlanSides.Sell);
        orders[0].LimitPrice.Should().Be(54.1m);
        orders[0].PlacedAtUtc.Should().Be(new DateTime(2026, 10, 23, 14, 4, 15, DateTimeKind.Utc));
        unknown.Should().ContainSingle().Which.Should().Be(("900000009", "42"));
    }

    [Theory]
    [InlineData("{\"errMsg\":\"Too many requests\"}")]
    [InlineData("{\"error\":\"Rate limit exceeded, try again later\"}")]
    public async Task RateLimitText_IsTransient_ForOrdersAndPost(string body)
    {
        var (gateway, _) = Create((_, _) => Json(body));
        var list = () => gateway.GetOrdersAsync(UserId, activeOnly: false);
        (await list.Should().ThrowAsync<FreedomGatewayException>()).Which.Kind.Should().Be(GatewayErrorKind.Transient);
        var post = () => gateway.PostLimitOrderAsync(UserId, Order());
        (await post.Should().ThrowAsync<FreedomGatewayException>()).Which.Kind.Should().Be(GatewayErrorKind.Transient, "исход размещения неизвестен — только сверка");
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task AccountCheck_429_408_5xx_IsTransient(HttpStatusCode code)
    {
        var (gateway, _) = Create((_, _) => Json("{}", code));
        var act = () => gateway.GetAccountInfoAsync(UserId);
        (await act.Should().ThrowAsync<FreedomGatewayException>()).Which.Kind.Should().Be(GatewayErrorKind.Transient, "ключ не должен инвалидироваться");
    }

    [Fact]
    public async Task AccountCheck_TooManyRequestsText_IsTransient()
    {
        var (gateway, _) = Create((_, _) => Json("{\"errMsg\":\"Too many requests\"}"));
        var act = () => gateway.GetAccountInfoAsync(UserId);
        (await act.Should().ThrowAsync<FreedomGatewayException>()).Which.Kind.Should().Be(GatewayErrorKind.Transient);
    }

    [Fact]
    public async Task Quote_TimeFromBrokerStamps_MskToUtc_AndMarketStatus()
    {
        // Фиксированные часы шлюза: 14:05 UTC = 17:05 МСК. ltt 16:59:58, bbt 17:00:11, bat 17:00:09 МСК → берётся самая поздняя (bbt) = 14:00:11 UTC.
        var (gateway, _) = Create((_, _) => Json(Fixture("getStockQuotesJson")));
        var q = await gateway.GetQuoteAsync(UserId, "VUAA.EU");
        q.QuoteTimeUtc.Should().Be(new DateTime(2026, 10, 23, 14, 0, 11, DateTimeKind.Utc));
        q.MarketOpen.Should().BeTrue();

        var noStamps = await gateway.GetQuoteAsync(UserId, "LUV.US");
        noStamps.QuoteTimeUtc.Should().BeNull("меток нет — котировка несвежая (preflight откажет)");
        noStamps.MarketOpen.Should().BeNull();
    }

    [Fact]
    public void QuoteTime_BookStampsWin_FreshTradeDoesNotRefreshStaleBook()
    {
        // T-67E (minor 6): свежесть — по стакану max(bbt, bat); свежая сделка (ltt) при застывшем стакане котировку свежей НЕ делает.
        var now = new DateTime(2026, 10, 24, 13, 35, 57, DateTimeKind.Utc);
        var stale = new DateTime(2026, 10, 24, 15, 0, 0); // МСК = 12:00 UTC — полтора часа назад
        var q = new Bonds.Infrastructure.Connectors.Freedom.Models.FreedomQuote(
            "VUAA.EU", 1m, 2m, 1m, "USD", 0.01m,
            LastTradeLocal: new DateTime(2026, 10, 24, 16, 35, 57), // МСК = now — свежая сделка
            BidTimeLocal: stale,
            AskTimeLocal: stale.AddMinutes(-5));
        FreedomOrderGateway.QuoteTimeUtc(q, TimeSpan.FromHours(3), now).Should().Be(new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void QuoteTime_MaxOfBidAskTimes_FutureDropped_LttOnlyWhenBothMissing()
    {
        var now = new DateTime(2026, 10, 24, 13, 35, 57, DateTimeKind.Utc);
        var withBook = new Bonds.Infrastructure.Connectors.Freedom.Models.FreedomQuote(
            "VUAA.EU", 1m, 2m, 1m, "USD", 0.01m,
            LastTradeLocal: new DateTime(2026, 10, 24, 16, 35, 0),
            BidTimeLocal: new DateTime(2026, 10, 24, 16, 30, 0),
            AskTimeLocal: new DateTime(2026, 10, 24, 16, 34, 0));
        FreedomOrderGateway.QuoteTimeUtc(withBook, TimeSpan.FromHours(3), now).Should().Be(new DateTime(2026, 10, 24, 13, 34, 0, DateTimeKind.Utc), "max(bbt, bat), не ltt");

        var future = withBook with { AskTimeLocal = new DateTime(2026, 10, 24, 18, 0, 0) };
        FreedomOrderGateway.QuoteTimeUtc(future, TimeSpan.FromHours(3), now).Should().Be(new DateTime(2026, 10, 24, 13, 30, 0, DateTimeKind.Utc), "метка из будущего отброшена");

        var noBook = withBook with { BidTimeLocal = null, AskTimeLocal = null };
        FreedomOrderGateway.QuoteTimeUtc(noBook, TimeSpan.FromHours(3), now).Should().Be(new DateTime(2026, 10, 24, 13, 35, 0, DateTimeKind.Utc), "нет меток стакана — ltt");
    }

    [Theory]
    [InlineData(30)]
    [InlineData(31)]
    [InlineData(71)]
    [InlineData(72)]
    public void Orders_ClosedCodeWithoutTradeArray_IsActiveUnconfirmed_NotFinalZero(int stat)
    {
        // T-67E (M1): окончательный код снятия БЕЗ массива trade — исполненное неизвестно, заявка не «снята с нулём».
        using var doc = JsonDocument.Parse("{\"orders\":{\"order\":[{\"id\":5,\"stat\":" + stat + ",\"oper\":1,\"q\":10,\"leaves_qty\":0}]}}");
        var s = FreedomOrderGateway.ParseOrders(doc.RootElement, TimeSpan.Zero).Single();
        s.Status.Should().Be(TradeOrderStatuses.Submitted);
        s.LotsExecuted.Should().Be(0);
        s.FillsUnconfirmed.Should().BeTrue();
        s.BrokerStat.Should().Be(stat);
    }

    [Theory]
    [InlineData(2)] // маржинальная покупка — не наша сторона
    [InlineData(4)]
    [InlineData(9)]
    public void Orders_MarginOrUnknownOper_HasNoSide(int oper)
    {
        using var doc = JsonDocument.Parse("{\"orders\":{\"order\":[{\"id\":1,\"stat\":10,\"oper\":" + oper + ",\"q\":1,\"leaves_qty\":1}]}}");
        FreedomOrderGateway.ParseOrders(doc.RootElement, TimeSpan.Zero).Single().Side.Should().BeNull();
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

using System.Net;
using System.Text;
using System.Text.Json;
using Bonds.Core.Credentials;
using Bonds.Infrastructure.Connectors.Freedom;
using Bonds.Infrastructure.Connectors.Freedom.Models;
using Bonds.Infrastructure.Credentials;
using FluentAssertions;
using Moq;
using Xunit;

namespace Bonds.Tests.Connectors.Freedom;

/// <summary>Задача 59: подпись API v3, разбор фикстур, HTTP-слой и валидатор Freedom. Без сети.</summary>
public class FreedomApiTests
{
    private const string Pub = "pub-key-TEST-1234";
    private const string Priv = "test-private-key";
    private static readonly FreedomCredentials Creds = new(Pub, Priv);

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> Bodies { get; } = [];

        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            return _respond(request);
        }
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Freedom", name + ".json"));

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static (FreedomApiClient Client, FakeHandler Handler) Create(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new FakeHandler(respond);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://tradernet.global") };
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(FreedomApiClient.HttpClientName)).Returns(http);
        var time = new FixedTime(DateTimeOffset.FromUnixTimeSeconds(1700000000));
        return (new FreedomApiClient(factory.Object, time), handler);
    }

    // --- подпись -----------------------------------------------------------------------------

    [Fact]
    public void Sign_MatchesReferenceVectors()
    {
        FreedomRequestSigner.Sign("test-private-key", "{\"tickers\":\"VUAA.EU,SPYM.US\"}", 1700000000)
            .Should().Be("576ce1445e7d3db2987b9b5c22eea0b339777c05bbb9baa410a8cd13e8e8e70b");
        FreedomRequestSigner.Sign("test-private-key", "{}", 1700000000)
            .Should().Be("a292e5ed5038526bdd857ff5783fff021fc1562e48dc95a7b7a623b775889cde");
    }

    [Fact]
    public void BuildBody_IsCompact_OrderPreserved_EmptyIsBraces()
    {
        FreedomRequestSigner.BuildBody(null).Should().Be("{}");
        FreedomRequestSigner.BuildBody([new("beginDate", "2026-01-01"), new("max", 1000)])
            .Should().Be("{\"beginDate\":\"2026-01-01\",\"max\":1000}");
    }

    // --- HTTP-слой ---------------------------------------------------------------------------

    [Fact]
    public async Task Request_IsPostToApiCmd_WithExactBodyAndSignedHeaders()
    {
        var (client, handler) = Create(_ => Json(Fixture("getStockQuotesJson")));

        var quotes = await client.GetQuotesAsync(Creds, ["VUAA.EU", "SPYM.US"]);

        quotes.Should().HaveCount(4);
        var req = handler.Requests.Single();
        req.Method.Should().Be(HttpMethod.Post);
        req.RequestUri!.AbsolutePath.Should().Be("/api/getStockQuotesJson");
        handler.Bodies.Single().Should().Be("{\"tickers\":\"VUAA.EU,SPYM.US\"}");
        req.Content!.Headers.ContentType!.MediaType.Should().Be("application/json");
        req.Headers.GetValues("X-NtApi-PublicKey").Single().Should().Be(Pub);
        req.Headers.GetValues("X-NtApi-Timestamp").Single().Should().Be("1700000000");
        req.Headers.GetValues("X-NtApi-Sig").Single().Should().Be(FreedomRequestSigner.Sign(Priv, handler.Bodies.Single(), 1700000000));
    }

    [Fact]
    public async Task GetUserInfo_SendsEmptyBraces_AndParsesFixture()
    {
        var (client, handler) = Create(_ => Json(Fixture("GetAllUserTexInfo")));

        var info = await client.GetUserInfoAsync(Creds);

        handler.Bodies.Single().Should().Be("{}");
        handler.Requests.Single().Headers.GetValues("X-NtApi-Sig").Single()
            .Should().Be("a292e5ed5038526bdd857ff5783fff021fc1562e48dc95a7b7a623b775889cde");
        info.Should().Be(new FreedomUserInfo("100001", 1431, "Standard"));
    }

    [Fact]
    public async Task GetTrades_SendsDatesAndMax()
    {
        var (client, handler) = Create(_ => Json(Fixture("getTradesHistory")));

        await client.GetTradesAsync(Creds, new DateOnly(2026, 1, 5), new DateOnly(2026, 10, 23));

        handler.Bodies.Single().Should().Be("{\"beginDate\":\"2026-01-05\",\"endDate\":\"2026-10-23\",\"max\":1000}");
    }

    [Fact]
    public async Task GetCashFlowsAndFind_SendExpectedBodies()
    {
        var (client, handler) = Create(r => r.RequestUri!.AbsolutePath.EndsWith("tickerFinder")
            ? Json("{\"found\":[{\"t\":\"SPYM.US\",\"nm\":\"SPDR\",\"mkt\":\"FIX\",\"codesub\":\"X\",\"x_curr\":\"USD\"}]}")
            : Json(Fixture("getUserCashFlows")));

        (await client.GetCashFlowsAsync(Creds, 50)).Should().NotBeEmpty();
        var found = await client.FindAsync(Creds, "SPYM");

        handler.Bodies.Should().Equal("{\"take\":50}", "{\"text\":\"SPYM\"}");
        found.Single().Should().Be(new FreedomInstrument("SPYM.US", "SPDR", "FIX", "X", "USD"));
    }

    // --- ошибки ------------------------------------------------------------------------------

    [Fact]
    public async Task ErrMsgInBody_ThrowsWithoutSecrets()
    {
        var (client, _) = Create(_ => Json($"{{\"errMsg\":\"Bad key {Pub} / {Priv}\",\"code\":2}}"));

        var ex = await Assert.ThrowsAsync<FreedomApiException>(() => client.GetPortfolioAsync(Creds));

        ex.Command.Should().Be("getPositionJson");
        ex.Message.Should().NotContain(Pub).And.NotContain(Priv).And.Contain("Bad key");
    }

    [Fact]
    public async Task ErrorField_Throws()
    {
        var (client, _) = Create(_ => Json("{\"error\":\"Access denied\"}"));
        await Assert.ThrowsAsync<FreedomApiException>(() => client.GetUserInfoAsync(Creds));
    }

    [Fact]
    public async Task EmptyErrorField_IsNotAnError()
    {
        var (client, _) = Create(_ => Json("{\"error\":\"\",\"trades\":{\"trade\":[]}}"));
        (await client.GetTradesAsync(Creds, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 2))).Should().BeEmpty();
    }

    [Fact]
    public async Task Http500_ThrowsWithoutSecrets()
    {
        var (client, _) = Create(_ => Json("boom", HttpStatusCode.InternalServerError));
        var ex = await Assert.ThrowsAsync<FreedomApiException>(() => client.GetUserInfoAsync(Creds));
        ex.Message.Should().Contain("500").And.NotContain(Priv).And.NotContain(Pub);
    }

    [Fact]
    public void Credentials_ToString_HidesKeys() => Creds.ToString().Should().NotContain(Pub).And.NotContain(Priv);

    // --- парсинг фикстур ---------------------------------------------------------------------

    private static JsonElement Root(string fixture) => JsonDocument.Parse(Fixture(fixture)).RootElement;

    [Fact]
    public void ParsePortfolio_Fixture()
    {
        var p = FreedomParser.ParsePortfolio(Root("getPositionJson"));

        p.Cash.Should().Contain(new FreedomCashBalance("USD", 4.77m)).And.Contain(new FreedomCashBalance("EUR", 88.57m));
        var luv = p.Positions.Single(x => x.Ticker == "LUV.US");
        luv.Should().Be(new FreedomPosition("LUV.US", "Southwest Airlines", 13m, "USD", 40.3m, 41.56m, 540.28m));
    }

    [Fact]
    public void ParseQuotes_Fixture()
    {
        var q = FreedomParser.ParseQuotes(Root("getStockQuotesJson")).Single(x => x.Ticker == "VUAA.EU");
        q.Should().Be(new FreedomQuote("VUAA.EU", 148.12m, 148.18m, 148.18m, "USD", 0.02m));
    }

    [Fact]
    public void ParseTrades_Fixture_StringNumbersAndSide()
    {
        var trades = FreedomParser.ParseTrades(Root("getTradesHistory"));

        trades.Should().NotBeEmpty();
        var t = trades.First();
        t.Id.Should().Be("853041955");
        t.OrderId.Should().Be("685126461");
        t.Ticker.Should().Be("COPX.EU");
        t.Side.Should().Be(FreedomTradeSide.Sell);
        t.Quantity.Should().Be(1m);
        t.Price.Should().Be(57.28m);
        t.Amount.Should().Be(57.28m);
        t.Commission.Should().Be(1.27m);
        t.CommissionCurrency.Should().Be("USD");
        t.Date.Should().Be(new DateTime(2026, 10, 23, 18, 47, 27));
    }

    [Theory]
    [InlineData("{\"trades\":{\"trade\":[],\"max_trade_id\":0}}")]
    [InlineData("{\"trades\":{}}")]
    [InlineData("{\"trades\":[]}")]
    [InlineData("{\"trades\":\"\"}")]
    [InlineData("{}")]
    public void ParseTrades_EmptyHistoryShapes_ReturnEmpty(string json) =>
        FreedomParser.ParseTrades(JsonDocument.Parse(json).RootElement).Should().BeEmpty();

    [Fact]
    public void ParseCashFlows_Fixture_SignedAmountsAndCommissionComment()
    {
        var flows = FreedomParser.ParseCashFlows(Root("getUserCashFlows"));

        var fee = flows.First(f => f.TypeCode == "commission_for_trades");
        fee.Amount.Should().Be(-0.27m);
        fee.Currency.Should().Be("USD");
        fee.Comment.Should().Contain("Trade 710639658 buy SPYM.US").And.Contain("kazakhstan_its").And.Contain("funds");
        flows.First(f => f.TypeCode == "bank").Amount.Should().Be(73.27m);
        flows.Select(f => f.TypeCode).Should().Contain(["dividend", "tax", "card"]);
    }

    [Fact]
    public void ParseUserInfo_Fixture() =>
        FreedomParser.ParseUserInfo(Root("GetAllUserTexInfo")).TariffId.Should().Be(1431);

    // --- валидатор ---------------------------------------------------------------------------

    [Fact]
    public async Task Validator_Ok_ReturnsMeta_AndAccount()
    {
        var api = new Mock<IFreedomApiClient>();
        api.Setup(a => a.GetUserInfoAsync(Creds, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FreedomUserInfo("100001", 1431, "Standard"));
        var validator = new FreedomCredentialValidator(api.Object);

        var r = await validator.ValidateAsync(new Dictionary<string, string> { ["publicKey"] = Pub, ["privateKey"] = Priv });

        r.Ok.Should().BeTrue();
        r.BrokerAccountId.Should().Be("100001");
        using var meta = JsonDocument.Parse(r.MetaJson!);
        meta.RootElement.GetProperty("accountNumber").GetString().Should().Be("100001");
        meta.RootElement.GetProperty("tariffId").GetInt32().Should().Be(1431);
        meta.RootElement.GetProperty("tariffName").GetString().Should().Be("Standard");
        r.MetaJson.Should().NotContain(Priv);
    }

    [Fact]
    public async Task Validator_BrokerError_IsInvalid_WithSafeMessage()
    {
        var api = new Mock<IFreedomApiClient>();
        api.Setup(a => a.GetUserInfoAsync(It.IsAny<FreedomCredentials>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new FreedomApiException("GetAllUserTexInfo", "bad"));
        var r = await new FreedomCredentialValidator(api.Object)
            .ValidateAsync(new Dictionary<string, string> { ["publicKey"] = Pub, ["privateKey"] = Priv });

        r.Ok.Should().BeFalse();
        r.Message.Should().NotContain(Priv).And.NotContain(Pub);
    }

    [Theory]
    [InlineData("", "x")]
    [InlineData("x", "")]
    [InlineData("a b", "c")]
    public async Task Validator_EmptyOrSpaced_IsInvalid_WithoutNetwork(string pub, string priv)
    {
        var api = new Mock<IFreedomApiClient>(MockBehavior.Strict);
        var r = await new FreedomCredentialValidator(api.Object)
            .ValidateAsync(new Dictionary<string, string> { ["publicKey"] = pub, ["privateKey"] = priv });
        r.Ok.Should().BeFalse();
    }

    [Fact]
    public void Registry_HasFreedom_WithMaskOnPublicKey()
    {
        var p = CredentialProviders.Find("freedom")!;
        p.Category.Should().Be(CredentialCategory.Broker);
        p.Fields.Select(f => f.Name).Should().Equal("publicKey", "privateKey");
        p.MaskSourceField.Name.Should().Be("publicKey");
    }
}

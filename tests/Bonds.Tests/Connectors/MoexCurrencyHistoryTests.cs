using System.Net;
using System.Text;
using Bonds.Core.Currencies;
using Bonds.Infrastructure.Connectors.Moex;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Bonds.Tests.Connectors;

/// <summary>
/// Задача 55: история курса валют MOEX CETS (CLOSE - рубли за FACEVALUE единиц) и номинал. Реальные снимки ISS - Fixtures/Moex
/// (currency_*.json, снято 2026-10-22), без сети. Правило номинала: рубли за 1 единицу = CLOSE / FACEVALUE (CNY 1, JPY 100).
/// </summary>
public class MoexCurrencyHistoryTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<Uri, string> _body;
        public readonly List<Uri> Requests = [];

        public FakeHandler(Func<Uri, string> body) => _body = body;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body(request.RequestUri!), Encoding.UTF8, "application/json"),
            });
        }
    }

    private static MoexIssClient CreateClient(FakeHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://iss.moex.com") };
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(MoexIssClient.HttpClientName)).Returns(http);
        return new MoexIssClient(factory.Object, NullLogger<MoexIssClient>.Instance);
    }

    private static string Fixture(string name)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Moex", name));

    [Fact]
    public async Task GetCurrencyHistory_CnyFixture_ReturnsRubPerYuan_FromCetsBoard()
    {
        var handler = new FakeHandler(_ => Fixture("currency_history_CETS_CNYRUB_TOM_2026-10.json"));

        var result = await CreateClient(handler).GetCurrencyHistoryAsync("CNYRUB_TOM", new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 21));

        result.Should().HaveCount(15);
        result.Select(p => p.TradeDate).Should().BeInAscendingOrder();
        result.First(p => p.TradeDate == new DateOnly(2026, 10, 1)).CloseRubPerFaceValue.Should().Be(12.414m);
        result.Last().CloseRubPerFaceValue.Should().Be(12.2255m, "21.10.2026: ≈ 12.2 руб. за юань");
        handler.Requests.Single().AbsolutePath.Should().Be("/iss/history/engines/currency/markets/selt/boards/CETS/securities/CNYRUB_TOM.json");
    }

    [Fact]
    public async Task GetCurrencyHistory_DaysWithoutDeals_CloseZero_BecomeNull()
    {
        var handler = new FakeHandler(_ => Fixture("currency_history_CETS_JPYRUB_TOM_2026-10_nodeals.json"));

        var result = await CreateClient(handler).GetCurrencyHistoryAsync("JPYRUB_TOM", new DateOnly(2026, 10, 19), new DateOnly(2026, 10, 21));

        result.Should().HaveCount(3);
        result.Should().OnlyContain(p => p.CloseRubPerFaceValue == null, "CLOSE = 0 в ISS означает «сделок не было», а не курс 0");
    }

    [Theory]
    [InlineData("currency_securities_CETS_CNYRUB_TOM.json", "CNYRUB_TOM", 1)]
    [InlineData("currency_securities_CETS_JPYRUB_TOM.json", "JPYRUB_TOM", 100)]
    public async Task GetCurrencyFaceValue_Fixture(string fixture, string secid, int expected)
    {
        var handler = new FakeHandler(_ => Fixture(fixture));

        var face = await CreateClient(handler).GetCurrencyFaceValueAsync(secid);

        face.Should().Be(expected);
        handler.Requests.Single().AbsolutePath.Should().Be($"/iss/engines/currency/markets/selt/boards/CETS/securities/{secid}.json");
    }

    [Fact]
    public async Task NominalRule_JpyClosePer100_BecomesRubPerSingleYen_CnyUnchanged()
    {
        // Правило, зафиксированное тестом: CLOSE делится на FACEVALUE из securities. CNY: 12.2255 / 1; JPY (синтетический CLOSE 8.2 за 100 иен) -> 0.082.
        var client = CreateClient(new FakeHandler(uri => uri.AbsolutePath.Contains("JPYRUB")
            ? Fixture("currency_securities_CETS_JPYRUB_TOM.json")
            : Fixture("currency_securities_CETS_CNYRUB_TOM.json")));

        var cnyFace = (await client.GetCurrencyFaceValueAsync("CNYRUB_TOM"))!.Value;
        var jpyFace = (await client.GetCurrencyFaceValueAsync("JPYRUB_TOM"))!.Value;

        CurrencyRateUnits.RubPerUnit(12.2255m, cnyFace).Should().Be(12.2255m);
        CurrencyRateUnits.RubPerUnit(8.2m, jpyFace).Should().Be(0.082m);
    }

    [Fact]
    public async Task GetCurrencyFaceValue_UnknownSecid_ReturnsNull()
    {
        var handler = new FakeHandler(_ => "{\"securities\": {\"columns\": [\"SECID\", \"FACEVALUE\", \"LOTSIZE\"], \"data\": []}}");

        (await CreateClient(handler).GetCurrencyFaceValueAsync("NOPE")).Should().BeNull();
    }
}

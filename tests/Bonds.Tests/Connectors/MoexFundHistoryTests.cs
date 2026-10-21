using System.Net;
using System.Text;
using Bonds.Infrastructure.Connectors.Moex;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Bonds.Tests.Connectors;

/// <summary>
/// Задача 51: история паёв (TQBR/shares, РУБЛИ за пай) и истории индекса (YIELD % -> доля, DURATION дни -> годы).
/// Реальные снимки ISS — Fixtures/Moex (index_history_RGBI_2026-10.json, shares_history_TQBR_TPAY_2026-10.json), без сети.
/// </summary>
public class MoexFundHistoryTests
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
    public async Task GetIndexHistory_Fixture_ConvertsYieldPercentToFractionAndDurationDaysToYears()
    {
        var handler = new FakeHandler(_ => Fixture("index_history_RGBI_2026-10.json"));
        var result = await CreateClient(handler).GetIndexHistoryAsync("RGBI", new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 20));

        result.Should().HaveCount(14);
        var first = result.First(p => p.TradeDate == new DateOnly(2026, 10, 1));
        first.Close.Should().Be(111.25m);
        first.YieldFraction.Should().Be(0.1608m, "YIELD 16.08 (%) -> доля");
        first.DurationYears.Should().BeApproximately(1648m / 365m, 0.0000001m, "DURATION 1648 дней -> годы");
        result.Select(p => p.TradeDate).Should().BeInAscendingOrder();
        handler.Requests.Single().AbsolutePath.Should().Be("/iss/history/engines/stock/markets/index/securities/RGBI.json");
    }

    [Fact]
    public async Task GetSharesHistory_Fixture_ReturnsRubPricesFromTqbr()
    {
        var handler = new FakeHandler(_ => Fixture("shares_history_TQBR_TPAY_2026-10.json"));
        var result = await CreateClient(handler).GetSharesHistoryAsync("TPAY", new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 20));

        result.Should().HaveCount(14);
        result.First(p => p.TradeDate == new DateOnly(2026, 10, 1)).CloseRub.Should().Be(101.08m, "рубли за пай, без конверсии в % номинала");
        handler.Requests.Single().AbsolutePath.Should().Be("/iss/history/engines/stock/markets/shares/boards/TQBR/securities/TPAY.json");
    }

    [Fact]
    public async Task GetSharesHistory_Paginates_UntilShortPage()
    {
        // 250 строк: страницы 100 + 100 + 50.
        var handler = new FakeHandler(uri =>
        {
            var q = uri.Query.TrimStart('?').Split('&').Select(x => x.Split('=')).ToDictionary(x => x[0], x => x.Length > 1 ? x[1] : "");
            var start = int.Parse(q["start"]);
            var rows = new StringBuilder();
            for (var i = start; i < Math.Min(start + 100, 250); i++)
            {
                if (rows.Length > 0) rows.Append(',');
                rows.Append($"[\"{new DateOnly(2025, 1, 1).AddDays(i):yyyy-MM-dd}\",{100 + i % 5}]");
            }

            return "{\"history\":{\"columns\":[\"TRADEDATE\",\"CLOSE\"],\"data\":[" + rows + "]}}";
        });

        var result = await CreateClient(handler).GetSharesHistoryAsync("TOFZ", new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31));

        result.Should().HaveCount(250);
        handler.Requests.Should().HaveCount(3);
    }

    [Fact]
    public async Task GetIndexHistory_SourceUnavailable_ReturnsEmpty()
    {
        var http = new HttpClient(new FailingHandler()) { BaseAddress = new Uri("https://iss.moex.com") };
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(MoexIssClient.HttpClientName)).Returns(http);
        var client = new MoexIssClient(factory.Object, NullLogger<MoexIssClient>.Instance);

        (await client.GetIndexHistoryAsync("RGBI", new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 2))).Should().BeEmpty();
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
    }
}

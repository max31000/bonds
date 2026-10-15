using System.Net;
using System.Text;
using Bonds.Infrastructure.Connectors.Moex;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Bonds.Tests.Connectors;

/// <summary>
/// Задача 43: постраничное чтение bulk-справочника эмитентов (<c>GetSecurityEmitentsAsync</c>) и карточки
/// бумаги (<c>GetSecurityDescriptionAsync</c>). Без сети — фейковый <see cref="HttpMessageHandler"/>.
/// </summary>
public class MoexSecurityEmitentsPagingTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public List<string> Urls { get; } = [];

        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Urls.Add(request.RequestUri!.PathAndQuery);
            return Task.FromResult(_respond(request));
        }
    }

    private static MoexIssClient CreateClient(FakeHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://iss.moex.com") };
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(MoexIssClient.HttpClientName)).Returns(http);
        return new MoexIssClient(factory.Object, NullLogger<MoexIssClient>.Instance);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static int StartOf(HttpRequestMessage r)
    {
        var q = r.RequestUri!.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).ToDictionary(kv => kv[0], kv => kv.ElementAtOrDefault(1) ?? "");
        return int.Parse(q["start"]);
    }

    private static string Page(int fromIndex, int count) =>
        "{\"securities\":{\"columns\":[\"secid\",\"emitent_id\",\"emitent_title\",\"emitent_inn\"],\"data\":["
        + string.Join(',', Enumerable.Range(fromIndex, count).Select(i => $"[\"S{i:D5}\",{i},\"Эмитент {i}\",\"{i:D10}\"]"))
        + "]}}";

    [Fact]
    public async Task GetSecurityEmitentsAsync_PaginatesUntilShortPage_AndRequestsExpectedColumns()
    {
        var handler = new FakeHandler(r => StartOf(r) switch
        {
            0 => Json(Page(0, 100)),
            100 => Json(Page(100, 100)),
            200 => Json(Page(200, 7)),
            _ => Json(Page(0, 0)),
        });

        var result = await CreateClient(handler).GetSecurityEmitentsAsync();

        result.Should().HaveCount(207);
        handler.Urls.Should().HaveCount(3, "короткая третья страница — последняя, четвёртый запрос не нужен");
        handler.Urls[0].Should().Contain("securities.columns=secid,emitent_id,emitent_title,emitent_inn")
            .And.Contain("engine=stock").And.Contain("market=bonds");
    }

    [Fact]
    public async Task GetSecurityEmitentsAsync_FullPageThenEmpty_StopsOnEmptyPage()
    {
        var handler = new FakeHandler(r => StartOf(r) == 0 ? Json(Page(0, 100)) : Json(Page(0, 0)));

        var result = await CreateClient(handler).GetSecurityEmitentsAsync();

        result.Should().HaveCount(100);
        handler.Urls.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetSecurityEmitentsAsync_IssIgnoresStart_StopsWhenNoNewSecids()
    {
        // ISS игнорирует start и каждый раз отдаёт одну и ту же полную страницу — без критерия
        // «ноль новых SECID» цикл выжег бы все страницы-предохранитель.
        var handler = new FakeHandler(_ => Json(Page(0, 100)));

        var result = await CreateClient(handler).GetSecurityEmitentsAsync();

        result.Should().HaveCount(100);
        handler.Urls.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetSecurityEmitentsAsync_FailureMidway_ReturnsCollectedRows_FirstPageFailure_ReturnsEmpty()
    {
        var midway = new FakeHandler(r => StartOf(r) == 0 ? Json(Page(0, 100)) : Json("", HttpStatusCode.InternalServerError));
        (await CreateClient(midway).GetSecurityEmitentsAsync()).Should().HaveCount(100);

        var firstFails = new FakeHandler(_ => Json("", HttpStatusCode.ServiceUnavailable));
        (await CreateClient(firstFails).GetSecurityEmitentsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task GetSecurityDescriptionAsync_ParsesCard_And404ReturnsNull()
    {
        var card = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Moex", "security_description_default_RU000A10BVW6.json"));
        var handler = new FakeHandler(r => r.RequestUri!.AbsolutePath.Contains("RU000A10BVW6")
            ? Json(card)
            : Json("", HttpStatusCode.NotFound));
        var client = CreateClient(handler);

        var found = await client.GetSecurityDescriptionAsync("RU000A10BVW6");
        var missing = await client.GetSecurityDescriptionAsync("NOPE");

        found!.HasDefault.Should().BeTrue();
        handler.Urls[0].Should().Contain("/iss/securities/RU000A10BVW6.json").And.Contain("iss.only=description");
        missing.Should().BeNull();
    }
}

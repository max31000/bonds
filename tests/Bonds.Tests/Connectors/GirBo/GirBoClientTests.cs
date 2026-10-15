using System.Net;
using System.Text;
using Bonds.Infrastructure.Connectors.GirBo;
using FluentAssertions;
using Moq;
using Xunit;

namespace Bonds.Tests.Connectors.GirBo;

/// <summary>Задача 45: HTTP-слой клиента ГИР БО на фикстурах. Без сети — фейковый HttpMessageHandler.</summary>
public class GirBoClientTests
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

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        private readonly Exception _ex;
        public ThrowingHandler(Exception ex) => _ex = ex;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw _ex;
    }

    private static GirBoClient CreateClient(HttpMessageHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://bo.nalog.gov.ru") };
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(GirBoClient.HttpClientName)).Returns(http);
        return new GirBoClient(factory.Object);
    }

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "GirBo", name));

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task FindOrganizationAsync_RequestsSearchByInn_AndParses()
    {
        var handler = new FakeHandler(_ => Json(Fixture("search_lenta_7814148471.json")));

        var org = await CreateClient(handler).FindOrganizationAsync("7814148471");

        org!.Id.Should().Be(4953069);
        handler.Urls.Should().ContainSingle().Which.Should().Be("/advanced-search/organizations/search?query=7814148471&page=0");
    }

    [Fact]
    public async Task FindOrganizationAsync_EmptyContent_ReturnsNull_NotException()
    {
        var handler = new FakeHandler(_ => Json(Fixture("search_empty.json")));

        (await CreateClient(handler).FindOrganizationAsync("0000000001")).Should().BeNull();
    }

    [Fact]
    public async Task GetFilingsAsync_NotFound404_ReturnsEmpty_NotException()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        (await CreateClient(handler).GetFilingsAsync(1)).Should().BeEmpty();
        handler.Urls.Should().ContainSingle().Which.Should().Be("/nbo/organizations/1/bfo/");
    }

    [Fact]
    public async Task GetAnnualFinancialsAsync_RequestsDetails_AndConvertsToRubles()
    {
        var handler = new FakeHandler(_ => Json(Fixture("details_lenta_14810112.json")));
        var now = DateTime.UtcNow;

        var rows = await CreateClient(handler).GetAnnualFinancialsAsync(14810112, "7814148471", 2021, now);

        rows.Should().HaveCount(3);
        rows[0].Revenue.Should().Be(495_877_663_000m);
        handler.Urls.Should().ContainSingle().Which.Should().Be("/nbo/bfo/14810112/details");
    }

    [Fact]
    public async Task GetAnnualFinancialsAsync_Details404_ReturnsEmpty()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        (await CreateClient(handler).GetAnnualFinancialsAsync(1, "7700000003", 2024, DateTime.UtcNow)).Should().BeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Server_NonSuccessExceptNotFound_IsUnavailable(HttpStatusCode code)
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(code));

        var act = () => CreateClient(handler).FindOrganizationAsync("7814148471");

        await act.Should().ThrowAsync<GirBoUnavailableException>();
    }

    [Fact]
    public async Task NetworkError_IsUnavailable()
    {
        var act = () => CreateClient(new ThrowingHandler(new HttpRequestException("dns"))).GetFilingsAsync(1);

        await act.Should().ThrowAsync<GirBoUnavailableException>();
    }

    [Fact]
    public async Task HttpClientTimeout_IsUnavailable()
    {
        var act = () => CreateClient(new ThrowingHandler(new TaskCanceledException("timeout"))).GetFilingsAsync(1);

        await act.Should().ThrowAsync<GirBoUnavailableException>();
    }

    [Fact]
    public async Task MalformedJson_IsUnavailable()
    {
        var handler = new FakeHandler(_ => Json("<html>maintenance</html>"));

        var act = () => CreateClient(handler).GetFilingsAsync(1);

        await act.Should().ThrowAsync<GirBoUnavailableException>();
    }

    [Fact]
    public async Task CallerCancellation_Propagates_NotWrapped()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var act = () => CreateClient(new ThrowingHandler(new OperationCanceledException(cts.Token))).GetFilingsAsync(1, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}

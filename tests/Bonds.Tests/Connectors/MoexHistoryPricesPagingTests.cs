using System.Net;
using System.Text;
using Bonds.Infrastructure.Connectors.Moex;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Bonds.Tests.Connectors;

/// <summary>
/// Plan/15 §A.1: MOEX ISS history-эндпоинт (дневные свечи) отдаёт страницы по 100 строк с блоком
/// <c>history.cursor</c>. Без пагинации бэкфилл истории XIRR молча теряет хвост ценового ряда для
/// любой бумаги в портфеле дольше ~100 торговых дней. Проверяем дочитывание всех страниц —
/// без сети, фейковый <see cref="HttpMessageHandler"/>, тот же паттерн, что
/// <see cref="MoexBondizationPagingTests"/>.
/// </summary>
public class MoexHistoryPricesPagingTests
{
    private const int PageSize = 100;

    /// <summary>Фейковый ISS: отдаёт <paramref name="totalDays"/> дневных свечей постранично по 100.
    /// Каждый второй день — "нет сделок" (CLOSE=null), чтобы проверить, что null проходит через
    /// клиента как есть (forward fill — забота потребителя, не клиента).</summary>
    private sealed class FakeIssHistoryHandler : HttpMessageHandler
    {
        private readonly int _totalDays;
        public int RequestCount;

        public FakeIssHistoryHandler(int totalDays) => _totalDays = totalDays;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            var query = ParseQuery(request.RequestUri!.Query);
            var start = int.TryParse(query.GetValueOrDefault("start"), out var s) ? s : 0;

            var rows = new StringBuilder();
            for (var i = start; i < Math.Min(start + PageSize, _totalDays); i++)
            {
                var date = new DateOnly(2025, 1, 1).AddDays(i);
                if (rows.Length > 0) rows.Append(',');
                if (i % 2 == 0)
                {
                    rows.Append($"[\"{date:yyyy-MM-dd}\",100.5,1.23]");
                }
                else
                {
                    rows.Append($"[\"{date:yyyy-MM-dd}\",null,null]");
                }
            }

            var json = "{\"history\":{\"columns\":[\"TRADEDATE\",\"CLOSE\",\"ACCINT\"],"
                      + $"\"data\":[{rows}]}}}}";

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }

        private static Dictionary<string, string> ParseQuery(string query)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = pair.Split('=', 2);
                result[Uri.UnescapeDataString(kv[0])] = kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : string.Empty;
            }
            return result;
        }
    }

    private static MoexIssClient CreateClient(FakeIssHistoryHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://iss.moex.com") };
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(MoexIssClient.HttpClientName)).Returns(http);
        return new MoexIssClient(factory.Object, NullLogger<MoexIssClient>.Instance);
    }

    [Fact]
    public async Task GetHistoryPricesAsync_FetchesAllPages()
    {
        var handler = new FakeIssHistoryHandler(totalDays: 250);
        var client = CreateClient(handler);

        var result = await client.GetHistoryPricesAsync("RU000A10AZ45", new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31));

        result.Should().HaveCount(250, "клиент обязан дочитать все страницы history, а не первые 100");
        handler.RequestCount.Should().Be(3, "250 строк = 3 страницы (100+100+50)");
    }

    [Fact]
    public async Task GetHistoryPricesAsync_SinglePage_NoExtraRequests()
    {
        var handler = new FakeIssHistoryHandler(totalDays: 30);
        var client = CreateClient(handler);

        var result = await client.GetHistoryPricesAsync("RU000A10BPQ0", new DateOnly(2025, 1, 1), new DateOnly(2025, 1, 30));

        result.Should().HaveCount(30);
        handler.RequestCount.Should().Be(1, "одной страницы достаточно — лишних запросов быть не должно");
    }

    [Fact]
    public async Task GetHistoryPricesAsync_PreservesNullClosePrice_ForDaysWithoutTrades()
    {
        var handler = new FakeIssHistoryHandler(totalDays: 10);
        var client = CreateClient(handler);

        var result = await client.GetHistoryPricesAsync("RU000A10AZ45", new DateOnly(2025, 1, 1), new DateOnly(2025, 1, 10));

        result.Should().Contain(p => p.ClosePricePercent == null, "дни без сделок — null, forward fill делает потребитель, не клиент");
        result.Should().Contain(p => p.ClosePricePercent == 100.5m);
    }
}

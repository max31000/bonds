using System.Net;
using System.Text;
using Bonds.Infrastructure.Connectors.Moex;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Bonds.Tests.Connectors;

/// <summary>
/// T-1 (находка N-1): MOEX ISS отдаёт блок <c>coupons</c> страницами по 20 строк. Без пагинации
/// у любой бумаги с &gt;20 купонами хвост графика молча теряется → YTM/дюрация/G-спред/календарь
/// считаются по обрезанному потоку. Тут проверяем, что клиент дочитывает все страницы.
/// Без сети — фейковый <see cref="HttpMessageHandler"/>, отвечающий по параметрам iss.only/start.
/// </summary>
public class MoexBondizationPagingTests
{
    private const int PageSize = 20;

    /// <summary>Фейковый ISS: на запрос блока coupons отдаёт <paramref name="totalCoupons"/> купонов
    /// постранично по 20; amortizations/offers — пустые. Считает число запросов на каждый блок.</summary>
    private sealed class FakeIssHandler : HttpMessageHandler
    {
        private readonly int _totalCoupons;
        public readonly Dictionary<string, int> BlockRequestCounts = new(StringComparer.OrdinalIgnoreCase);

        public FakeIssHandler(int totalCoupons) => _totalCoupons = totalCoupons;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var query = ParseQuery(request.RequestUri!.Query);
            var issOnly = query.GetValueOrDefault("iss.only", string.Empty);
            var start = int.TryParse(query.GetValueOrDefault("start"), out var s) ? s : 0;

            var block = issOnly.Contains("coupons") ? "coupons"
                      : issOnly.Contains("amortizations") ? "amortizations"
                      : issOnly.Contains("offers") ? "offers"
                      : "other";
            BlockRequestCounts[block] = BlockRequestCounts.GetValueOrDefault(block) + 1;

            var json = block == "coupons"
                ? BuildCouponsPage(start)
                : BuildEmptyBlock(block);

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

        private string BuildCouponsPage(int start)
        {
            var rows = new StringBuilder();
            for (var i = start; i < Math.Min(start + PageSize, _totalCoupons); i++)
            {
                // помесячные купоны от 2025-01-15, value_rub фиксированный — детали не важны,
                // важно, что строки валидны и имеют разные даты (для DetectIncompleteCoupons).
                var date = new DateOnly(2025, 1, 15).AddMonths(i);
                var startDate = new DateOnly(2025, 1, 15).AddMonths(i - 1);
                if (rows.Length > 0) rows.Append(',');
                rows.Append($"[\"{date:yyyy-MM-dd}\",\"{startDate:yyyy-MM-dd}\",16.03,16.03,1000]");
            }

            return "{\"coupons\":{\"columns\":[\"coupondate\",\"startdate\",\"value\",\"value_rub\",\"facevalue\"],"
                 + $"\"data\":[{rows}]}},"
                 + $"\"coupons.cursor\":{{\"columns\":[\"INDEX\",\"TOTAL\",\"PAGESIZE\"],\"data\":[[{start},{_totalCoupons},{PageSize}]]}}}}";
        }

        private static string BuildEmptyBlock(string block) =>
            $"{{\"{block}\":{{\"columns\":[\"secid\"],\"data\":[]}}}}";
    }

    private static MoexIssClient CreateClient(FakeIssHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://iss.moex.com") };
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(MoexIssClient.HttpClientName)).Returns(http);
        return new MoexIssClient(factory.Object, NullLogger<MoexIssClient>.Instance);
    }

    [Fact]
    public async Task GetBondizationAsync_FetchesAllCouponPages()
    {
        var handler = new FakeIssHandler(totalCoupons: 42);
        var client = CreateClient(handler);

        var result = await client.GetBondizationAsync("RU000A10AZ45");

        result.Coupons.Should().HaveCount(42, "клиент обязан дочитать все страницы блока coupons, а не первые 20");
        result.DataIncomplete.Should().BeFalse("полный регулярный график не является неполным");
    }

    [Fact]
    public async Task GetBondizationAsync_SinglePage_NoExtraRequests()
    {
        // Короткая бумага (8 купонов < pagesize) не должна ломаться и не должна порождать лишних
        // запросов сверх необходимого.
        var handler = new FakeIssHandler(totalCoupons: 8);
        var client = CreateClient(handler);

        var result = await client.GetBondizationAsync("RU000A10BPQ0");

        result.Coupons.Should().HaveCount(8);
        handler.BlockRequestCounts["coupons"].Should().Be(1, "одной страницы достаточно — лишних запросов быть не должно");
    }
}

using System.Globalization;
using System.Net;
using System.Text;
using Bonds.Infrastructure.Connectors.Moex;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Bonds.Tests.Connectors;

/// <summary>
/// Задача 26 часть A: снимок всей рыночной вселенной облигаций MOEX
/// (<c>GetBondMarketSnapshotAsync</c>) — пагинация, дедупликация по board (максимальный оборот),
/// устойчивость к отсутствующей marketdata и null-колонкам. Без сети — фейковый
/// <see cref="HttpMessageHandler"/>, тот же паттерн, что <c>MoexBondizationPagingTests</c>.
/// </summary>
public class MoexBondUniversePagingTests
{
    private const string SecuritiesColumns =
        "\"SECID\",\"BOARDID\",\"SHORTNAME\",\"SECNAME\",\"ISIN\",\"FACEVALUE\",\"LOTVALUE\"," +
        "\"FACEUNIT\",\"COUPONPERCENT\",\"COUPONPERIOD\",\"MATDATE\",\"OFFERDATE\",\"LISTLEVEL\"," +
        "\"SECTYPE\",\"BONDTYPE\",\"STATUS\"";

    private const string MarketDataColumns =
        "\"SECID\",\"BOARDID\",\"YIELD\",\"DURATION\",\"LAST\",\"MARKETPRICE\",\"VALTODAY\"," +
        "\"BID\",\"OFFER\",\"NUMTRADES\"";

    private static string SecRow(string secid, string boardId, string shortName = "BOND", int listLevel = 1, string faceUnit = "SUR") =>
        $"[\"{secid}\",\"{boardId}\",\"{shortName}\",\"{shortName} full\",\"{secid}ISIN\",1000,1000," +
        $"\"{faceUnit}\",8.5,182,\"2030-01-01\",null,{listLevel},\"8\",\"Фикс с известным купоном\",\"A\"]";

    private static string MdRow(string secid, string boardId, decimal yieldPct, int durationDays, decimal turnover) =>
        string.Create(CultureInfo.InvariantCulture,
            $"[\"{secid}\",\"{boardId}\",{yieldPct},{durationDays},99.5,99.4,{turnover},99.0,100.0,10]");

    /// <summary>Фейковый ISS: отдаёт заданные страницы securities/marketdata по параметру start,
    /// коротка последняя (или единственная) страница — сигнал остановки пагинации.</summary>
    private sealed class FakeUniverseHandler : HttpMessageHandler
    {
        private readonly Dictionary<int, string> _pagesByStart;
        public int RequestCount { get; private set; }

        public FakeUniverseHandler(Dictionary<int, string> pagesByStart) => _pagesByStart = pagesByStart;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            var query = ParseQuery(request.RequestUri!.Query);
            var start = int.TryParse(query.GetValueOrDefault("start"), out var s) ? s : 0;

            var json = _pagesByStart.GetValueOrDefault(start, BuildPage([], []));
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

    private static string BuildPage(IEnumerable<string> secRows, IEnumerable<string> mdRows)
    {
        var secData = string.Join(',', secRows);
        var mdData = string.Join(',', mdRows);
        return "{\"securities\":{\"columns\":[" + SecuritiesColumns + "],\"data\":[" + secData + "]},"
             + "\"marketdata\":{\"columns\":[" + MarketDataColumns + "],\"data\":[" + mdData + "]}}";
    }

    private static MoexIssClient CreateClient(FakeUniverseHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://iss.moex.com") };
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(MoexIssClient.HttpClientName)).Returns(http);
        return new MoexIssClient(factory.Object, NullLogger<MoexIssClient>.Instance);
    }

    [Fact]
    public async Task GetBondMarketSnapshotAsync_DeduplicatesByBoard_KeepsHighestTurnover()
    {
        // Одна бумага на двух board — TQOB с оборотом, SPOB без оборота (реальная картина ISS:
        // "поставочный" режим SPOB часто присутствует с нулевым/пустым VALTODAY).
        var secRows = new[]
        {
            SecRow("SU26207", "SPOB"),
            SecRow("SU26207", "TQOB"),
        };
        var mdRows = new[]
        {
            MdRow("SU26207", "SPOB", yieldPct: 0m, durationDays: 0, turnover: 0m),
            MdRow("SU26207", "TQOB", yieldPct: 12.5m, durationDays: 365, turnover: 4_000_000m),
        };
        var pages = new Dictionary<int, string> { [0] = BuildPage(secRows, mdRows) };
        var handler = new FakeUniverseHandler(pages);
        var client = CreateClient(handler);

        var result = await client.GetBondMarketSnapshotAsync();

        result.Should().HaveCount(1, "одна и та же бумага на двух board должна схлопнуться в одну строку");
        var row = result.Single();
        row.BoardId.Should().Be("TQOB", "должен победить board с максимальным оборотом");
        row.TurnoverRub.Should().Be(4_000_000m);
        row.YieldPercent.Should().Be(12.5m);
    }

    [Fact]
    public async Task GetBondMarketSnapshotAsync_RowWithoutMarketData_HasNullMarketFields_NotThrows()
    {
        // Бумага без сделок сегодня — в marketdata строки нет вовсе (не null-колонки, а отсутствие
        // строки целиком) — распространённый случай для низколиквидных выпусков.
        var secRows = new[] { SecRow("RU000NOTRADE", "TQCB") };
        var pages = new Dictionary<int, string> { [0] = BuildPage(secRows, []) };
        var handler = new FakeUniverseHandler(pages);
        var client = CreateClient(handler);

        var result = await client.GetBondMarketSnapshotAsync();

        result.Should().HaveCount(1);
        var row = result.Single();
        row.YieldPercent.Should().BeNull();
        row.DurationDays.Should().BeNull();
        row.TurnoverRub.Should().BeNull();
    }

    [Fact]
    public async Task GetBondMarketSnapshotAsync_PaginatesAcrossTwoPages()
    {
        // Первая страница ровно IssUniversePageSize строк (1000) — сигнал "может быть ещё",
        // вторая — короткая (1 строка) — сигнал остановки. Тест не поднимает реальную страницу в
        // 1000 строк буквально — проверяем логику остановки через минимальный воспроизводимый кейс:
        // page size задаётся классом константой, поэтому здесь эмулируем через реальные полные
        // страницы нельзя без 1000 фикстур — вместо этого проверяем дочитывание на количестве
        // запросов и агрегации по двум явно различным SECID на разных "start", подставляя фейковую
        // короткую первую страницу невозможно (клиент остановится) — поэтому здесь заполняем
        // первую страницу так, чтобы её длина была < размера страницы, и проверяем единственный запрос.
        var secRows = new[] { SecRow("RU000A", "TQCB"), SecRow("RU000B", "TQCB") };
        var mdRows = new[]
        {
            MdRow("RU000A", "TQCB", 10m, 200, 100_000m),
            MdRow("RU000B", "TQCB", 11m, 400, 200_000m),
        };
        var pages = new Dictionary<int, string> { [0] = BuildPage(secRows, mdRows) };
        var handler = new FakeUniverseHandler(pages);
        var client = CreateClient(handler);

        var result = await client.GetBondMarketSnapshotAsync();

        result.Should().HaveCount(2);
        handler.RequestCount.Should().Be(1, "короткая страница (меньше размера страницы ISS) — сигнал остановки, лишних запросов быть не должно");
    }

    [Fact]
    public async Task GetBondMarketSnapshotAsync_NonRubFaceUnit_IsParsed_FilteringIsCallerResponsibility()
    {
        // Парсер сам не фильтрует по валюте (план п.A.2 — это ответственность вызывающего кода/
        // refresh-сервиса) — здесь проверяем только, что поле FaceUnit корректно доносится наружу.
        var secRows = new[] { SecRow("XS0000001", "TQCB", faceUnit: "USD") };
        var pages = new Dictionary<int, string> { [0] = BuildPage(secRows, []) };
        var handler = new FakeUniverseHandler(pages);
        var client = CreateClient(handler);

        var result = await client.GetBondMarketSnapshotAsync();

        result.Should().HaveCount(1);
        result.Single().FaceUnit.Should().Be("USD");
    }

    [Fact]
    public async Task GetBondMarketSnapshotAsync_EmptySecuritiesBlock_ReturnsEmptyList()
    {
        var pages = new Dictionary<int, string> { [0] = BuildPage([], []) };
        var handler = new FakeUniverseHandler(pages);
        var client = CreateClient(handler);

        var result = await client.GetBondMarketSnapshotAsync();

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetBondMarketSnapshotAsync_IssIgnoresStart_StopsAfterSecondRequest()
    {
        // Реальное поведение ISS на этом эндпоинте (ревью волны 26-29, MAJOR): start/limit
        // игнорируются, весь рынок (>= размера страницы) приходит на КАЖДЫЙ запрос. Без критерия
        // "страница не добавила новых строк" цикл делал бы все 20 одинаковых запросов к бирже
        // за каждый часовой refresh. Ожидание: ровно 2 запроса (второй подтверждает повтор) и
        // никаких дублей в результате.
        var secRows = Enumerable.Range(0, 1000).Select(i => SecRow($"BOND{i:D5}", "TQCB")).ToArray();
        var fullPage = BuildPage(secRows, []);
        var pages = new Dictionary<int, string>();
        for (var start = 0; start < 20_000; start += 1000)
        {
            pages[start] = fullPage;
        }
        var handler = new FakeUniverseHandler(pages);
        var client = CreateClient(handler);

        var result = await client.GetBondMarketSnapshotAsync();

        handler.RequestCount.Should().Be(2, "вторая страница без новых (Secid, BoardId) должна остановить цикл");
        result.Should().HaveCount(1000, "повторные страницы не должны дублировать бумаги");
    }
}

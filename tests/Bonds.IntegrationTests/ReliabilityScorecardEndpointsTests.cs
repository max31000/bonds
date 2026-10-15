using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bonds.Core.Models;
using Bonds.Infrastructure.Repositories;
using Bonds.IntegrationTests.Infrastructure;
using Dapper;
using FluentAssertions;
using MySqlConnector;
using Xunit;

namespace Bonds.IntegrationTests;

/// <summary>
/// Задача 46: скоркарта надёжности через API — светофор v2 («дешёвая» здоровая бумага не Red, ОФЗ Green, эмитент с
/// HASDEFAULT Red, банк без отчётности Yellow с Coverage &lt; 0.6), ручной ввод рейтингов/отчётности (приоритет над
/// автоматикой, валидация 422), фильтр recommended (Spread=Caution не исключает, Red исключает). Контейнер общий —
/// сущности маркируются Guid-суффиксом, кэш скоркарты в тестовой фабрике отключён (RelativeValueCacheDuration=0).
/// </summary>
[Collection("Integration")]
public class ReliabilityScorecardEndpointsTests
{
    private readonly TestWebApplicationFactory _factory;

    public ReliabilityScorecardEndpointsTests(TestWebApplicationFactory factory) => _factory = factory;

    private string Conn => _factory.Database.ConnectionString;

    private static string Marker() => Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    private async Task<HttpClient> AuthorizedClientAsync()
    {
        var userRepo = new UserRepository(Conn);
        var telegramId = Random.Shared.NextInt64(1, long.MaxValue);
        await userRepo.CreateAsync(new User { TelegramId = telegramId });
        var client = _factory.CreateClient();
        Mvv42TestAuth.Authorize(client, telegramId);
        return client;
    }

    /// <summary>Живая бумага банка: листинг 1, высокая ликвидность, оферты нет, спред задаётся явно.</summary>
    private static BondUniverseEntry Bond(string secid, string sector, decimal gspread, decimal yield = 0.20m, int? listLevel = 1, decimal turnover = 10_000_000m) => new()
    {
        Secid = secid, Isin = $"I{secid}", ShortName = $"BOND {secid}", SecName = $"Full name {secid}",
        FaceValue = 1000m, Sector = sector, YieldFraction = yield, DurationYears = 2m, PricePercent = 99.5m,
        TurnoverRub = turnover, BidPercent = 99.9m, OfferPercent = 100m, NumTrades = 50, ListLevel = listLevel,
        GspreadApproxFraction = gspread,
        MaturityDate = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(3)), UpdatedAt = DateTime.UtcNow,
    };

    private async Task<JsonElement> GetReliabilityAsync(HttpClient client, string secid)
    {
        var response = await client.GetAsync($"/api/universe/{secid}/reliability");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("riskSignals");
    }

    private static JsonElement Component(JsonElement riskSignals, string key) =>
        riskSignals.GetProperty("components").EnumerateArray().Single(c => c.GetProperty("key").GetString() == key);

    // ─── Светофор v2 ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CheapHealthyBond_IsGreen_NotRed_AndValueVerdictIsSeparateField()
    {
        var marker = Marker();
        var sector = $"Sector{marker}";
        var universe = new BondUniverseRepository(Conn);
        // Корзина из 6 бумаг: пять с G-спредом 300 б.п. и одна «дешёвая» (400 б.п., +100 б.п. к медиане) — по старой
        // матрице ось спреда Caution автоматически делала её Red.
        var peers = Enumerable.Range(0, 5).Select(i => Bond($"PR{i}{marker}", sector, 0.0300m)).ToList();
        var cheap = Bond($"CH{marker}", sector, 0.0400m);
        await universe.UpsertSnapshotBatchAsync([.. peers, cheap]);
        await ReliabilitySeed.SeedHealthyIssuerAsync(Conn, cheap.Secid, cheap.Isin!);

        var client = await AuthorizedClientAsync();
        var risk = await GetReliabilityAsync(client, cheap.Secid);

        risk.GetProperty("spread").GetString().Should().Be("Caution", "ось спреда по-прежнему видит дешевизну как информационный сигнал");
        risk.GetProperty("reliability").GetString().Should().Be("Green", "дешевизна больше не краснит бумагу");
        risk.GetProperty("valueVerdict").GetString().Should().Be("Cheap");
        risk.GetProperty("score").GetDecimal().Should().Be(100m);
        risk.GetProperty("coverage").GetDecimal().Should().BeGreaterThanOrEqualTo(0.6m);
        risk.GetProperty("criticalFlags").GetArrayLength().Should().Be(0);
        Component(risk, "revenue_size").GetProperty("points").GetInt32().Should().Be(8);
        Component(risk, "issuer_default_history").GetProperty("points").GetInt32().Should().Be(12);
        Component(risk, "broker_risk_level").GetProperty("points").GetInt32().Should().Be(10);
        Component(risk, "spread_momentum").GetProperty("missing").GetBoolean().Should().BeTrue("истории меньше 10 дней — явный Missing, не ноль");

        // Те же поля в строке скринера (свёртка без компонентов).
        var list = await (await client.GetAsync($"/api/universe?search=CH{marker}")).Content.ReadFromJsonAsync<JsonElement>();
        var row = list.GetProperty("rows").EnumerateArray().Single();
        row.GetProperty("reliability").GetString().Should().Be("Green");
        row.GetProperty("valueVerdict").GetString().Should().Be("Cheap");
    }

    /// <summary>Задача 47: UI тултипа Value («G-спред X против медианы корзины Y, порог ±Z») и формы ручного ввода
    /// (нужен ИНН эмитента) читают эти поля из полной карточки; строка скринера несёт те же числа Value.</summary>
    [Fact]
    public async Task Reliability_ExposesValueBasketDetails_AndEmitentInn_ForUi()
    {
        var marker = Marker();
        var sector = $"Sector{marker}";
        var universe = new BondUniverseRepository(Conn);
        var peers = Enumerable.Range(0, 5).Select(i => Bond($"PR{i}{marker}", sector, 0.0300m)).ToList();
        var cheap = Bond($"CH{marker}", sector, 0.0400m);
        await universe.UpsertSnapshotBatchAsync([.. peers, cheap]);
        var inn = ReliabilitySeed.NewInn();
        await ReliabilitySeed.AttachIssuerAsync(Conn, cheap.Secid, ReliabilitySeed.NewEmitentId(), inn);

        var client = await AuthorizedClientAsync();
        var response = await client.GetAsync($"/api/universe/{cheap.Isin}/reliability");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("emitentInn").GetString().Should().Be(inn);
        var risk = body.GetProperty("riskSignals");
        risk.GetProperty("valueVerdict").GetString().Should().Be("Cheap");
        risk.GetProperty("valueMedianFraction").GetDecimal().Should().Be(0.0300m, "медиана корзины — ДОЛЯ (300 б.п.)");
        risk.GetProperty("valueThresholdFraction").GetDecimal().Should().Be(0.0020m, "порог = max(20 б.п., 0.25 × IQR); IQR корзины 0");

        var list = await (await client.GetAsync($"/api/universe?search=CH{marker}")).Content.ReadFromJsonAsync<JsonElement>();
        var row = list.GetProperty("rows").EnumerateArray().Single();
        row.GetProperty("valueMedianFraction").GetDecimal().Should().Be(0.0300m);
        row.GetProperty("valueThresholdFraction").GetDecimal().Should().Be(0.0020m);
    }

    [Fact]
    public async Task Ofz_IsGreen_WithSovereignBonusInsteadOfLayerB()
    {
        var marker = Marker();
        var ofz = Bond($"OFZ{marker}", "Гособлигации", 0.0100m);
        await new BondUniverseRepository(Conn).UpsertSnapshotBatchAsync([ofz]);
        var emitentId = ReliabilitySeed.NewEmitentId();
        await ReliabilitySeed.AttachIssuerAsync(Conn, ofz.Secid, emitentId, null);
        await ReliabilitySeed.SeedCardAsync(Conn, ofz.Secid, ofz.Isin!, emitentId, false, false);
        await ReliabilitySeed.SeedRiskLevelAsync(Conn, ofz.Isin!, BrokerRiskLevel.Low);

        var risk = await GetReliabilityAsync(await AuthorizedClientAsync(), ofz.Secid);

        risk.GetProperty("reliability").GetString().Should().Be("Green");
        Component(risk, "sovereign").GetProperty("points").GetInt32().Should().Be(15);
        risk.GetProperty("components").EnumerateArray().Select(c => c.GetProperty("key").GetString())
            .Should().NotContain("revenue_size");
    }

    [Fact]
    public async Task IssuerWithDefaultOnAnotherIssue_IsRed_ForEveryIssueOfTheEmitent()
    {
        var marker = Marker();
        var sector = $"Sector{marker}";
        var clean = Bond($"CL{marker}", sector, 0.0300m);
        var defaulted = Bond($"DF{marker}", sector, 0.0300m);
        await new BondUniverseRepository(Conn).UpsertSnapshotBatchAsync([clean, defaulted]);
        var inn = ReliabilitySeed.NewInn();
        var emitentId = ReliabilitySeed.NewEmitentId();
        await ReliabilitySeed.AttachIssuerAsync(Conn, clean.Secid, emitentId, inn);
        await ReliabilitySeed.AttachIssuerAsync(Conn, defaulted.Secid, emitentId, inn);
        await ReliabilitySeed.SeedCardAsync(Conn, clean.Secid, clean.Isin!, emitentId, hasDefault: false, hasTechnicalDefault: false);
        await ReliabilitySeed.SeedCardAsync(Conn, defaulted.Secid, defaulted.Isin!, emitentId, hasDefault: true, hasTechnicalDefault: false);
        await ReliabilitySeed.SeedFinancialsAsync(Conn, inn, IssuerFinancialsStatuses.Ok, ReliabilitySeed.HealthyFinancials(inn));

        var risk = await GetReliabilityAsync(await AuthorizedClientAsync(), clean.Secid);

        risk.GetProperty("reliability").GetString().Should().Be("Red", "дефолт по любому выпуску эмитента — критический флаг");
        risk.GetProperty("criticalFlags").EnumerateArray().Select(f => f.GetString()).Should().Contain("IssuerDefaulted");
        Component(risk, "issuer_default_history").GetProperty("points").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task BankWithoutFinancials_IsYellow_WithLowCoverageAndExplainedMissingLayerB()
    {
        var marker = Marker();
        var bank = Bond($"BK{marker}", $"Sector{marker}", 0.0300m);
        await new BondUniverseRepository(Conn).UpsertSnapshotBatchAsync([bank]);
        var inn = ReliabilitySeed.NewInn();
        var emitentId = ReliabilitySeed.NewEmitentId();
        await ReliabilitySeed.AttachIssuerAsync(Conn, bank.Secid, emitentId, inn);
        await ReliabilitySeed.SeedCardAsync(Conn, bank.Secid, bank.Isin!, emitentId, false, false);
        await ReliabilitySeed.SeedRiskLevelAsync(Conn, bank.Isin!, BrokerRiskLevel.Low);
        await ReliabilitySeed.SeedFinancialsAsync(Conn, inn, IssuerFinancialsStatuses.Cb);

        var risk = await GetReliabilityAsync(await AuthorizedClientAsync(), bank.Secid);

        risk.GetProperty("reliability").GetString().Should().Be("Yellow");
        risk.GetProperty("coverage").GetDecimal().Should().BeLessThan(0.6m);
        var layerB = risk.GetProperty("components").EnumerateArray().Where(c => c.GetProperty("layer").GetString() == "B").ToList();
        layerB.Should().HaveCount(6).And.OnlyContain(c => c.GetProperty("missing").GetBoolean());
        layerB[0].GetProperty("rawDisplay").GetString().Should().Contain("кредитная организация");
    }

    [Fact]
    public async Task SpreadWidening_AgainstOwnHistoryMedian_IsSpreadShock_RedWithPriceDrawdownWarning()
    {
        var marker = Marker();
        var bond = Bond($"SH{marker}", $"Sector{marker}", 0.0300m);
        bond.PricePercent = 90m; // 10% ниже 20-дневного максимума 100
        await new BondUniverseRepository(Conn).UpsertSnapshotBatchAsync([bond]);
        await ReliabilitySeed.SeedHealthyIssuerAsync(Conn, bond.Secid, bond.Isin!);

        // 12 торговых дней истории (строго до сегодня): спред 200 б.п., цена 100% — сегодня спред 300 б.п. (+100 б.п.).
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await using var conn = new MySqlConnection(Conn);
        try
        {
            for (var i = 1; i <= 12; i++)
            {
                await conn.ExecuteAsync(@"
                    INSERT INTO bond_universe_history (snapshot_date, secid, gspread_approx_fraction, gspread_effective_fraction, turnover_rub, price_percent)
                    VALUES (@Date, @Secid, 0.0200, 0.0200, 10000000, 100)",
                    new { Date = today.AddDays(-i).ToDateTime(TimeOnly.MinValue), bond.Secid });
            }

            var risk = await GetReliabilityAsync(await AuthorizedClientAsync(), bond.Secid);

            risk.GetProperty("reliability").GetString().Should().Be("Red");
            risk.GetProperty("criticalFlags").EnumerateArray().Select(f => f.GetString()).Should().Contain("SpreadShock");
            risk.GetProperty("warningFlags").EnumerateArray().Select(f => f.GetString()).Should().Contain("PriceDrawdown");
            Component(risk, "spread_momentum").GetProperty("rawValue").GetDecimal().Should().Be(0.0100m, "сдвиг к медиане — ДОЛЯ (100 б.п.)");
            Component(risk, "spread_momentum").GetProperty("missing").GetBoolean().Should().BeFalse();
        }
        finally
        {
            await conn.ExecuteAsync("DELETE FROM bond_universe_history WHERE secid = @Secid", new { bond.Secid });
        }
    }

    [Fact]
    public async Task UnknownSecid_Returns404_AndEndpointRequiresAuth()
    {
        var client = await AuthorizedClientAsync();
        (await client.GetAsync($"/api/universe/NOPE{Marker()}/reliability")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var anonymous = _factory.CreateClient();
        (await anonymous.GetAsync("/api/universe/ANY/reliability")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anonymous.GetAsync("/api/issuers/7700000001/ratings")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anonymous.PostAsJsonAsync("/api/issuers/7700000001/financials", new { period = 2024, revenue = 1m })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ─── Ручной ввод ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ManualFinancials_AndRating_FeedScorecardImmediately_AndWinOverAutomaticData()
    {
        var marker = Marker();
        var sector = $"Sector{marker}";
        var bond = Bond($"MN{marker}", sector, 0.0300m);
        await new BondUniverseRepository(Conn).UpsertSnapshotBatchAsync([bond]);
        var inn = ReliabilitySeed.NewInn();
        var emitentId = ReliabilitySeed.NewEmitentId();
        await ReliabilitySeed.AttachIssuerAsync(Conn, bond.Secid, emitentId, inn);
        await ReliabilitySeed.SeedCardAsync(Conn, bond.Secid, bond.Isin!, emitentId, false, false);
        await ReliabilitySeed.SeedRiskLevelAsync(Conn, bond.Isin!, BrokerRiskLevel.Low);
        // ГИР БО скрыт: автоматической отчётности нет.
        await ReliabilitySeed.SeedFinancialsAsync(Conn, inn, IssuerFinancialsStatuses.Hidden);
        var client = await AuthorizedClientAsync();

        var before = await GetReliabilityAsync(client, bond.Secid);
        before.GetProperty("reliability").GetString().Should().Be("Yellow");
        Component(before, "revenue_size").GetProperty("missing").GetBoolean().Should().BeTrue();
        before.GetProperty("components").EnumerateArray().Select(c => c.GetProperty("key").GetString()).Should().NotContain("manual_rating");

        // Ручная отчётность (РУБЛИ) — здоровый профиль.
        var h = ReliabilitySeed.HealthyFinancials(inn)[0];
        var post = await client.PostAsJsonAsync($"/api/issuers/{inn}/financials", new
        {
            period = h.Period, revenue = h.Revenue, operatingProfit = h.OperatingProfit, interestPaid = h.InterestPaid, netProfit = h.NetProfit,
            equity = h.Equity, totalAssets = h.TotalAssets, currentAssets = h.CurrentAssets, currentLiabilities = h.CurrentLiabilities,
            cash = h.Cash, longDebt = h.LongDebt, shortDebt = h.ShortDebt, retainedEarnings = h.RetainedEarnings,
        });
        post.StatusCode.Should().Be(HttpStatusCode.OK);
        (await post.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("source").GetString().Should().Be("manual");

        var after = await GetReliabilityAsync(client, bond.Secid);
        Component(after, "revenue_size").GetProperty("points").GetInt32().Should().Be(8);
        Component(after, "revenue_size").GetProperty("source").GetString().Should().Contain("Ручной ввод");
        after.GetProperty("reliability").GetString().Should().Be("Green");

        // Рейтинг AAA попадает в слой A (+10 к максимуму), потом удаляется.
        var ratingResponse = await client.PostAsJsonAsync($"/api/issuers/{inn}/ratings",
            new { agency = "АКРА", rating = "AAA(RU)", ratingDate = DateTime.UtcNow.AddMonths(-2).ToString("yyyy-MM-dd") });
        ratingResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var withRating = await GetReliabilityAsync(client, bond.Secid);
        Component(withRating, "manual_rating").GetProperty("points").GetInt32().Should().Be(10);
        Component(withRating, "manual_rating").GetProperty("maxPoints").GetInt32().Should().Be(10);

        var list = await (await client.GetAsync($"/api/issuers/{inn}/ratings")).Content.ReadFromJsonAsync<JsonElement>();
        list.GetProperty("ratings").GetArrayLength().Should().Be(1);
        list.GetProperty("ratings")[0].GetProperty("isStale").GetBoolean().Should().BeFalse();

        (await client.DeleteAsync($"/api/issuers/{inn}/ratings/АКРА")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.DeleteAsync($"/api/issuers/{inn}/ratings/АКРА")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var withoutRating = await GetReliabilityAsync(client, bond.Secid);
        withoutRating.GetProperty("components").EnumerateArray().Select(c => c.GetProperty("key").GetString()).Should().NotContain("manual_rating");
    }

    [Fact]
    public async Task ManualFinancialsForSameYear_OverrideAutomaticRow_InScorecardAndInFinancialsList()
    {
        var marker = Marker();
        var bond = Bond($"PM{marker}", $"Sector{marker}", 0.0300m);
        await new BondUniverseRepository(Conn).UpsertSnapshotBatchAsync([bond]);
        var inn = ReliabilitySeed.NewInn();
        await ReliabilitySeed.AttachIssuerAsync(Conn, bond.Secid, ReliabilitySeed.NewEmitentId(), inn);
        // ГИР БО (ok): слабая выручка 0.5 млрд за последний год.
        var weak = new IssuerFinancials { Inn = inn, Period = ReliabilitySeed.LastReportYear, Source = IssuerFinancialsSources.GirBo, FetchedAt = DateTime.UtcNow, Revenue = 0.5e9m };
        await ReliabilitySeed.SeedFinancialsAsync(Conn, inn, IssuerFinancialsStatuses.Ok, [weak]);
        var client = await AuthorizedClientAsync();
        Component(await GetReliabilityAsync(client, bond.Secid), "revenue_size").GetProperty("points").GetInt32().Should().Be(0);

        (await client.PostAsJsonAsync($"/api/issuers/{inn}/financials", new { period = ReliabilitySeed.LastReportYear, revenue = 5e9m }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        Component(await GetReliabilityAsync(client, bond.Secid), "revenue_size").GetProperty("points").GetInt32().Should().Be(8, "ручная запись приоритетнее ГИР БО");
        var financials = await (await client.GetAsync($"/api/issuers/{inn}/financials")).Content.ReadFromJsonAsync<JsonElement>();
        financials.GetProperty("girBoStatus").GetString().Should().Be("ok");
        var year = financials.GetProperty("years").EnumerateArray().Single(y => y.GetProperty("period").GetInt32() == ReliabilitySeed.LastReportYear);
        year.GetProperty("source").GetString().Should().Be("manual");
        year.GetProperty("revenue").GetDecimal().Should().Be(5e9m);
    }

    [Theory]
    [InlineData("123")]          // не 10/12 цифр
    [InlineData("77000000AB")]
    public async Task Issuers_InvalidInn_Returns422(string inn)
    {
        var client = await AuthorizedClientAsync();

        (await client.GetAsync($"/api/issuers/{inn}/ratings")).StatusCode.Should().Be((HttpStatusCode)422);
        (await client.PostAsJsonAsync($"/api/issuers/{inn}/financials", new { period = 2024, revenue = 1m })).StatusCode.Should().Be((HttpStatusCode)422);
    }

    [Fact]
    public async Task Ratings_Validation_Returns422WithValidationExceptionType()
    {
        var client = await AuthorizedClientAsync();
        var inn = ReliabilitySeed.NewInn();
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");

        async Task AssertInvalid(object body)
        {
            var response = await client.PostAsJsonAsync($"/api/issuers/{inn}/ratings", body);
            response.StatusCode.Should().Be((HttpStatusCode)422);
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("type").GetString().Should().Be("ValidationException");
        }

        await AssertInvalid(new { agency = "", rating = "AA", ratingDate = today });
        await AssertInvalid(new { agency = "АКРА", rating = "отлично", ratingDate = today });
        await AssertInvalid(new { agency = "АКРА", rating = "AA", ratingDate = DateTime.UtcNow.AddDays(30).ToString("yyyy-MM-dd") });
        await AssertInvalid(new { agency = "АКРА", rating = "AA", ratingDate = DateTime.UtcNow.AddYears(-11).ToString("yyyy-MM-dd") });
        await AssertInvalid(new { agency = "АКРА", rating = "AA" });
    }

    [Fact]
    public async Task Financials_Validation_Returns422()
    {
        var client = await AuthorizedClientAsync();
        var inn = ReliabilitySeed.NewInn();

        async Task AssertInvalid(object body) =>
            (await client.PostAsJsonAsync($"/api/issuers/{inn}/financials", body)).StatusCode.Should().Be((HttpStatusCode)422);

        await AssertInvalid(new { revenue = 1m });                                   // нет периода
        await AssertInvalid(new { period = 1990, revenue = 1m });                    // год вне диапазона
        await AssertInvalid(new { period = DateTime.UtcNow.Year + 1, revenue = 1m });
        await AssertInvalid(new { period = 2024 });                                  // нет ни одной суммы
        await AssertInvalid(new { period = 2024, revenue = -5m });                   // выручка не может быть < 0
        await AssertInvalid(new { period = 2024, equity = 1e18m });                  // нереалистично (тысячи vs рубли)

        // Отрицательные капитал/прибыль допустимы — это валидные данные убыточного эмитента.
        (await client.PostAsJsonAsync($"/api/issuers/{inn}/financials", new { period = 2024, equity = -1000m, netProfit = -500m }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ─── recommended-аллокация ────────────────────────────────────────────────────────────

    [Fact]
    public async Task RecommendedAllocation_KeepsSpreadCautionCandidate_ButExcludesRedIssuer()
    {
        var marker = Marker();
        var sector = $"Sector{marker}";
        // Высокая (но в рамках гигиены, потолок 0.45) доходность — гарантированно попадают в топ пула. Пять соседей по корзине (спред 300 б.п.),
        // «дорогой по риску» DEAR со спредом 500 б.п. (Spread=Caution по старой оси) и эмитент с дефолтом DEF.
        var peers = Enumerable.Range(0, 5).Select(i => Bond($"AP{i}{marker}", sector, 0.0300m, yield: 0.445m)).ToList();
        var dear = Bond($"DR{marker}", sector, 0.0500m, yield: 0.449m);
        var defaulted = Bond($"DF{marker}", sector, 0.0300m, yield: 0.4495m);
        await new BondUniverseRepository(Conn).UpsertSnapshotBatchAsync([.. peers, dear, defaulted]);
        var emitentId = ReliabilitySeed.NewEmitentId();
        await ReliabilitySeed.AttachIssuerAsync(Conn, defaulted.Secid, emitentId, null);
        await ReliabilitySeed.SeedCardAsync(Conn, defaulted.Secid, defaulted.Isin!, emitentId, hasDefault: true, hasTechnicalDefault: false);

        var client = await AuthorizedClientAsync();
        var response = await client.GetAsync("/api/analytics/allocation?amountRub=1000000000&source=recommended");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        var secids = body.GetProperty("allocations").EnumerateArray().Select(a => a.GetProperty("secid").GetString())
            .Concat(body.GetProperty("skipped").EnumerateArray().Select(s => s.GetProperty("secid").GetString()))
            .ToList();

        secids.Should().Contain(dear.Secid, "дешевизна (спред выше медианы корзины) — не риск и не исключает кандидата");
        secids.Should().NotContain(defaulted.Secid, "Red по скоркарте (история дефолта эмитента) исключается из recommended");

        var dearLine = body.GetProperty("allocations").EnumerateArray().SingleOrDefault(a => a.GetProperty("secid").GetString() == dear.Secid);
        if (dearLine.ValueKind != JsonValueKind.Undefined)
        {
            var risk = dearLine.GetProperty("riskSignals");
            risk.GetProperty("spread").GetString().Should().Be("Caution");
            risk.GetProperty("reliability").GetString().Should().NotBe("Red");
            risk.GetProperty("valueVerdict").GetString().Should().Be("Cheap");
        }
    }
}

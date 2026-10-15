using Bonds.Core.Models;
using Bonds.Infrastructure.Connectors.GirBo;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Connectors.GirBo;

/// <summary>
/// Задача 45: разбор ответов ГИР БО на реальных снимках (Fixtures/GirBo, сняты живыми запросами 2026-10-15,
/// данные публичные). Ключевой контракт: значения ГИР БО в ТЫСЯЧАХ рублей → в модели РУБЛИ (×1000).
/// </summary>
public class GirBoParserTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "GirBo", name));

    private static readonly DateTime Now = DateTime.UtcNow;

    // ─── search ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ParseSearch_StripsStrongMarkupFromInn_AndReturnsOrganization()
    {
        var org = GirBoParser.ParseSearch(Fixture("search_lenta_7814148471.json"), "7814148471");

        org.Should().NotBeNull();
        org!.Id.Should().Be(4953069);
        org.Inn.Should().Be("7814148471", "поисковая выдача оборачивает ИНН в <strong>, разметку нужно срезать");
        org.IsCb.Should().BeFalse();
    }

    [Fact]
    public void ParseSearch_EmptyContent_ReturnsNull()
    {
        GirBoParser.ParseSearch(Fixture("search_empty.json"), "0000000001").Should().BeNull();
    }

    [Fact]
    public void ParseSearch_InnMismatch_ReturnsNull_FuzzyHitsAreIgnored()
    {
        GirBoParser.ParseSearch(Fixture("search_lenta_7814148471.json"), "7740000076").Should().BeNull();
    }

    [Fact]
    public void ParseSearch_PrefersActiveOrganizationWithLatestFiling()
    {
        const string json = """
        {"content":[
          {"id":1,"inn":"<strong>7700000001</strong>","statusCode":"LIQUIDATED","bfo":{"period":"2024","isCb":false}},
          {"id":2,"inn":"<strong>7700000001</strong>","statusCode":"ACTIVE","bfo":{"period":"2019","isCb":false}},
          {"id":3,"inn":"<strong>7700000001</strong>","statusCode":"ACTIVE","bfo":{"period":"2023","isCb":true}}
        ]}
        """;

        var org = GirBoParser.ParseSearch(json, "7700000001");

        org!.Id.Should().Be(3);
        org.IsCb.Should().BeTrue();
    }

    [Fact]
    public void ParseSearch_NoBfoBlock_IsCbUnknown()
    {
        const string json = """{"content":[{"id":9,"inn":"<strong>7700000002</strong>","statusCode":"ACTIVE","bfo":null}]}""";

        GirBoParser.ParseSearch(json, "7700000002")!.IsCb.Should().BeNull();
    }

    // ─── filings ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ParseFilings_ReadsAllFilings_PeriodAsInt()
    {
        var filings = GirBoParser.ParseFilings(Fixture("bfo_list_mts_7155648.json"));

        filings.Should().HaveCount(5);
        filings.Select(f => f.Period).Should().BeEquivalentTo(new[] { 2025, 2024, 2023, 2021, 2022 });
        filings.Single(f => f.Period == 2025).Id.Should().Be(28452021);
        filings.Should().OnlyContain(f => !f.IsCb);
    }

    [Fact]
    public void ParseFilings_SkipsEntriesWithoutNumericPeriod()
    {
        const string json = """[{"id":1,"period":"2024","isCb":true},{"id":2,"period":null},{"id":3,"period":"abc"}]""";

        var filings = GirBoParser.ParseFilings(json);

        filings.Should().ContainSingle().Which.Should().Be(new GirBoFiling(1, 2024, true));
    }

    // ─── details ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ParseAnnualFinancials_Lenta_ThousandsBecomeRubles_ThreeYearsFromOneFiling()
    {
        var rows = GirBoParser.ParseAnnualFinancials(Fixture("details_lenta_14810112.json"), "7814148471", 2021, Now);

        rows.Select(r => r.Period).Should().Equal(2021, 2020, 2019);
        rows.Should().OnlyContain(r => r.Inn == "7814148471" && r.Source == IssuerFinancialsSources.GirBo && r.FetchedAt == Now);

        var current = rows[0];
        current.Revenue.Should().Be(495_877_663_000m, "2110 = 495 877 663 тыс. ₽ → ×1000 в рубли");
        current.OperatingProfit.Should().Be(19_914_681_000m);
        current.InterestPaid.Should().Be(5_826_333_000m);
        current.NetProfit.Should().Be(2_781_787_000m);
        current.Equity.Should().Be(80_413_461_000m);
        current.TotalAssets.Should().Be(251_949_004_000m);
        current.CurrentAssets.Should().Be(109_655_104_000m);
        current.CurrentLiabilities.Should().Be(96_330_009_000m);
        current.Cash.Should().Be(31_564_532_000m);
        current.LongDebt.Should().Be(70_874_807_000m);
        current.ShortDebt.Should().Be(21_506_485_000m);
        current.RetainedEarnings.Should().Be(64_872_878_000m);
        current.HasAudit.Should().BeTrue("в подаче есть auditReport");

        rows[1].Revenue.Should().Be(468_171_778_000m, "previous2110");
        rows[1].TotalAssets.Should().Be(228_019_468_000m, "previous1600");
        rows[1].HasAudit.Should().BeNull("аудит известен только для отчётного года подачи");
    }

    [Fact]
    public void ParseAnnualFinancials_ThirdYear_HasBalanceButNoProfitAndLoss()
    {
        // В форме 2 ГИР БО только current/previous — beforePrevious у ОФР нет, баланс — все три года.
        var rows = GirBoParser.ParseAnnualFinancials(Fixture("details_lenta_14810112.json"), "7814148471", 2021, Now);

        var third = rows.Single(r => r.Period == 2019);
        third.TotalAssets.Should().Be(278_315_186_000m);
        third.Equity.Should().Be(62_821_276_000m);
        third.Revenue.Should().BeNull("нет beforePrevious2110 — неизвестно, не ноль");
        third.NetProfit.Should().BeNull();
    }

    [Fact]
    public void ParseAnnualFinancials_Mts2025_PeriodDrivesYears()
    {
        var rows = GirBoParser.ParseAnnualFinancials(Fixture("details_mts_28452021.json"), "7740000076", 2025, Now);

        rows.Select(r => r.Period).Should().Equal(2025, 2024, 2023);
        rows[0].Revenue.Should().Be(476_837_411_000m);
        rows[0].InterestPaid.Should().Be(183_657_184_000m);
        rows[1].NetProfit.Should().Be(37_519_595_000m);
    }

    [Fact]
    public void ParseAnnualFinancials_PicksMaxCorrectionVersion_AmongAnnual()
    {
        var json = Details(
            Correction(version: 0, periodType: 12, revenue: 100, assets: 1000),
            Correction(version: 2, periodType: 12, revenue: 300, assets: 3000),
            Correction(version: 1, periodType: 12, revenue: 200, assets: 2000));

        var rows = GirBoParser.ParseAnnualFinancials(json, "7700000003", 2024, Now);

        rows[0].Revenue.Should().Be(300_000m, "корректировка 2 — самая свежая");
        rows[0].TotalAssets.Should().Be(3_000_000m);
    }

    [Fact]
    public void ParseAnnualFinancials_SkipsNonAnnualPeriodTypes()
    {
        var json = Details(
            Correction(version: 5, periodType: 3, revenue: 999, assets: 9990),   // квартальная — пропустить
            Correction(version: 0, periodType: 12, revenue: 100, assets: 1000));

        var rows = GirBoParser.ParseAnnualFinancials(json, "7700000003", 2024, Now);

        rows[0].Revenue.Should().Be(100_000m, "квартальная подача игнорируется даже при бОльшей версии");
    }

    [Fact]
    public void ParseAnnualFinancials_OnlyNonAnnual_ReturnsEmpty()
    {
        var json = Details(Correction(version: 0, periodType: 6, revenue: 1, assets: 1));

        GirBoParser.ParseAnnualFinancials(json, "7700000003", 2024, Now).Should().BeEmpty();
    }

    [Fact]
    public void ParseAnnualFinancials_SkipsYearWithoutAnyKeyData()
    {
        // Current заполнен, previous — полностью пуст (ни баланса, ни выручки) → год пропускается.
        var json = """
        [{"periodType":12,"correctionVersion":0,"auditReport":null,
          "balance":{"current1600":5.0,"previous1600":null,"beforePrevious1600":null},
          "financialResult":{"current2110":7.0,"previous2110":null}}]
        """;

        var rows = GirBoParser.ParseAnnualFinancials(json, "7700000003", 2024, Now);

        rows.Should().ContainSingle();
        rows[0].Period.Should().Be(2024);
        rows[0].Revenue.Should().Be(7_000m);
        rows[0].HasAudit.Should().BeFalse("auditReport отсутствует");
    }

    [Fact]
    public void ParseAnnualFinancials_EmptyArray_ReturnsEmpty()
    {
        GirBoParser.ParseAnnualFinancials("[]", "7700000003", 2024, Now).Should().BeEmpty();
    }

    [Fact]
    public void ParseAnnualFinancials_ExponentNumbers_AreReadExactly()
    {
        var json = """
        [{"periodType":12,"correctionVersion":0,"balance":{"current1600":1.25438177E8},"financialResult":{}}]
        """;

        GirBoParser.ParseAnnualFinancials(json, "7700000003", 2024, Now)[0].TotalAssets
            .Should().Be(125_438_177_000m);
    }

    private static string Details(params string[] corrections) => "[" + string.Join(',', corrections) + "]";

    private static string Correction(int version, int periodType, long revenue, long assets) =>
        $$$"""
        {"periodType":{{{periodType}}},"correctionVersion":{{{version}}},"auditReport":null,
         "balance":{"current1600":{{{assets}}}.0},"financialResult":{"current2110":{{{revenue}}}.0}}
        """;
}

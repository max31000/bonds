using Bonds.Infrastructure.Connectors.Moex;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Connectors.Moex;

/// <summary>
/// Задача 43 часть B: разбор карточки бумаги ISS <c>/iss/securities/{secid}.json?iss.only=description</c>
/// (пары name/value) на РЕАЛЬНЫХ ответах живого MOEX ISS 2026-10-15: бумага с HASDEFAULT=1, флоатер с
/// RUONIA+1.90%, ОФЗ-флоатер со спредом "0.00", бумага с CPI и EARLYREPAYMENT, ОФЗ без флагов.
/// COUPON_BENCHMARK_SPREAD приходит в ПРОЦЕНТНЫХ пунктах — парсер отдаёт ДОЛЮ (÷100).
/// </summary>
public class MoexSecurityDescriptionParserTests
{
    private static string Load(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Moex", name));

    [Fact]
    public void Parse_DefaultedBond_SetsBothDefaultFlags()
    {
        var d = MoexSecurityDescriptionParser.Parse(Load("security_description_default_RU000A10BVW6.json"));

        d.Should().NotBeNull();
        d!.Secid.Should().Be("RU000A10BVW6");
        d.Isin.Should().Be("RU000A10BVW6");
        d.EmitterId.Should().Be(14218);
        d.HasDefault.Should().BeTrue();
        d.HasTechnicalDefault.Should().BeTrue();
        d.IsQualifiedInvestors.Should().BeFalse();
        d.CouponBenchmark.Should().BeNull("у фикс-купонной бумаги бенчмарка нет — отсутствие поля это null, а не пустая строка");
        d.CouponBenchmarkSpreadFraction.Should().BeNull();
        d.EarlyRepayment.Should().BeNull("карточка не содержит EARLYREPAYMENT — неизвестно, а не false");
    }

    [Fact]
    public void Parse_FloaterWithRuoniaSpread_ConvertsPercentToFraction()
    {
        var d = MoexSecurityDescriptionParser.Parse(Load("security_description_floater_ruonia_RU000A10FV69.json"));

        d!.CouponBenchmark.Should().Be("RUONIA");
        d.CouponBenchmarkSpreadFraction.Should().Be(0.019m, "COUPON_BENCHMARK_SPREAD='1.90' (проценты) → 0.019 (доля)");
        d.HasDefault.Should().BeFalse();
        d.HasTechnicalDefault.Should().BeFalse();
        d.EmitterId.Should().Be(712);
    }

    [Fact]
    public void Parse_OfzFloaterWithZeroSpreadString_YieldsZeroFractionNotNull()
    {
        var d = MoexSecurityDescriptionParser.Parse(Load("security_description_ofz_floater_SU29025RMFS2.json"));

        d!.CouponBenchmark.Should().Be("RUONIA");
        d.CouponBenchmarkSpreadFraction.Should().Be(0m, "спред '0.00' — валидный ноль, не отсутствие данных");
        d.EmitterId.Should().Be(1228);
    }

    [Fact]
    public void Parse_EarlyRepaymentAndCpiBenchmark()
    {
        var d = MoexSecurityDescriptionParser.Parse(Load("security_description_cpi_early_repayment_RU000A0JVCB5.json"));

        d!.EarlyRepayment.Should().BeFalse();
        d.CouponBenchmark.Should().Be("CPI");
        d.CouponBenchmarkSpreadFraction.Should().Be(0.01m);
    }

    [Fact]
    public void Parse_PlainOfz_FlagsAreExplicitFalse()
    {
        var d = MoexSecurityDescriptionParser.Parse(Load("security_description_ofz_fixed_SU26238RMFS4.json"));

        d!.HasDefault.Should().BeFalse();
        d.HasTechnicalDefault.Should().BeFalse();
        d.IsQualifiedInvestors.Should().BeFalse();
        d.EmitterId.Should().Be(1228);
    }

    [Fact]
    public void Parse_EmptyAndGarbageValues_BecomeNullNotException()
    {
        const string json = "{\"description\":{\"columns\":[\"name\",\"title\",\"value\",\"type\",\"sort_order\",\"is_hidden\",\"precision\"],\"data\":["
            + "[\"SECID\",\"\",\"X1\",\"string\",1,0,null],"
            + "[\"HASDEFAULT\",\"\",\"\",\"boolean\",2,0,0],"
            + "[\"HASTECHNICALDEFAULT\",\"\",\"yes\",\"boolean\",3,0,0],"
            + "[\"COUPON_BENCHMARK\",\"\",\"  \",\"string\",4,0,null],"
            + "[\"COUPON_BENCHMARK_SPREAD\",\"\",\"n/a\",\"string\",5,0,null],"
            + "[\"EMITTER_ID\",\"\",null,\"number\",6,0,null]]}}";

        var d = MoexSecurityDescriptionParser.Parse(json);

        d!.Secid.Should().Be("X1");
        d.HasDefault.Should().BeNull();
        d.HasTechnicalDefault.Should().BeNull();
        d.CouponBenchmark.Should().BeNull();
        d.CouponBenchmarkSpreadFraction.Should().BeNull();
        d.EmitterId.Should().BeNull();
    }

    [Fact]
    public void Parse_CommaDecimalSeparator_IsAccepted()
    {
        const string json = "{\"description\":{\"columns\":[\"name\",\"title\",\"value\"],\"data\":["
            + "[\"SECID\",\"\",\"X2\"],[\"COUPON_BENCHMARK_SPREAD\",\"\",\"2,50\"]]}}";

        MoexSecurityDescriptionParser.Parse(json)!.CouponBenchmarkSpreadFraction.Should().Be(0.025m);
    }

    [Fact]
    public void Parse_NoDescriptionBlockOrNoRows_ReturnsNull()
    {
        MoexSecurityDescriptionParser.Parse("{}").Should().BeNull();
        MoexSecurityDescriptionParser.Parse("{\"description\":{\"columns\":[\"name\",\"title\",\"value\"],\"data\":[]}}")
            .Should().BeNull("ISS отдаёт пустую карточку для неизвестного SECID");
    }
}
